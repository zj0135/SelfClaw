using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.RegularExpressions;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Abstractions;
using SelfClaw.Infrastructure.Agents.Cli.Discovery;
using SelfClaw.Infrastructure.Agents.Cli.Process;
using SelfClaw.Infrastructure.Agents.Cli.Process.Models;

namespace SelfClaw.Desktop.Services.ProgrammingAssistant;

internal sealed partial class ProgrammingCliDiscovery : IProgrammingCliDiscovery
{
    internal const string DefaultModelOption = "Default (CLI config)";
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ModelDiscoveryTimeout = TimeSpan.FromSeconds(8);
    private static readonly IReadOnlyList<ProgrammingCliDefinition> CliDefinitions =
    [
        new(
            Id: "claude",
            Kind: CliAgentKind.Claude,
            Name: "Claude Code",
            Vendor: "Anthropic official CLI",
            Commands: ["claude"],
            VersionArguments: ["--version"],
            // Claude Code has no "list models" command, so this catalogue is maintained by hand. Keep the
            // Default sentinel first, then the stable CLI aliases (`claude --model <alias>`).
            Models: [DefaultModelOption, "opus", "sonnet", "haiku"],
            // Static list — Claude accepts these via `--effort <level>` per its `--help` output. Sentinel
            // first so "Default (CLI config)" means "omit the flag and let the CLI decide".
            ReasoningLevels: [DefaultModelOption, "low", "medium", "high", "xhigh", "max"],
            ModelListArguments: null,
            ParseModels: null,
            ParseReasoningLevels: null),
        new(
            Id: "codex",
            Kind: CliAgentKind.Codex,
            Name: "Codex CLI",
            Vendor: "OpenAI official CLI",
            Commands: ["codex"],
            VersionArguments: ["--version"],
            Models: [DefaultModelOption],
            // Fallback only: the live list (incl. xhigh) is derived from codex debug models' per-model
            // supported_reasoning_levels; this mirrors it for when discovery is unavailable.
            ReasoningLevels: [DefaultModelOption, "low", "medium", "high", "xhigh"],
            ModelListArguments: ["debug", "models"],
            ParseModels: CliModelListParser.ParseCodexDebugModels,
            ParseReasoningLevels: CliModelListParser.ParseCodexReasoningLevels),
        new(
            Id: "opencode",
            Kind: CliAgentKind.OpenCode,
            Name: "OpenCode",
            Vendor: "Open-source agent CLI",
            Commands: ["opencode"],
            VersionArguments: ["--version"],
            Models: [DefaultModelOption],
            ReasoningLevels: [],
            ModelListArguments: ["models"],
            ParseModels: CliModelListParser.ParseOpenCodeModels,
            ParseReasoningLevels: null),
    ];


    private readonly CliProbeProcess _probe;
    private readonly ILogger<ProgrammingCliDiscovery> _logger;
    public ProgrammingCliDiscovery(CliProbeProcess probe, ILogger<ProgrammingCliDiscovery> logger)
    { _probe = probe; _logger = logger; }
    public async Task<CliTestResult> TestAsync(string? cliId, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeCliId(cliId);
        var definition = CliDefinitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, normalized, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return new CliTestResult(cliId ?? string.Empty, Success: false, Version: null, Error: "未识别的 CLI");
        }

        Exception? lastError = null;
        foreach (var command in definition.Commands)
        {
            var resolver = new CliCommandResolver();
            try
            {
                var invocation = resolver.Resolve(command, definition.VersionArguments);
                var rawVersion = await ReadVersionAsync(invocation, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(rawVersion))
                {
                    // Timed out or no output — try the next command name, otherwise fall through to the "not found" reply.
                    continue;
                }

                return new CliTestResult(definition.Id, Success: true, Version: NormalizeVersion(command, rawVersion), Error: null);
            }
            catch (FileNotFoundException)
            {
                // This command name isn't on PATH; try the next alias.
                continue;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                lastError = exception;
                _logger.LogDebug(exception, "CLI detection command failed.");
            }
        }

        return new CliTestResult(
            definition.Id,
            Success: false,
            Version: null,
            Error: lastError?.Message ?? "未在 PATH 中检测到该 CLI");
    }


    public async Task<IReadOnlyList<DetectedProgrammingCli>> ScanAsync(CancellationToken cancellationToken)
    {
        var detected = await Task.WhenAll(CliDefinitions.Select(definition => ScanDefinitionAsync(definition, cancellationToken))).ConfigureAwait(false);
        return detected.OfType<DetectedProgrammingCli>().ToArray();
    }

    private async Task<DetectedProgrammingCli?> ScanDefinitionAsync(
        ProgrammingCliDefinition definition,
        CancellationToken cancellationToken)
    {
        foreach (var command in definition.Commands)
        {
            var resolver = new CliCommandResolver();
            try
            {
                var invocation = resolver.Resolve(command, definition.VersionArguments);
                var rawVersion = await ReadVersionAsync(invocation, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(rawVersion)) continue;
                var version = NormalizeVersion(command, rawVersion);
                var catalog = await DiscoverCatalogAsync(resolver, command, definition, cancellationToken).ConfigureAwait(false);

                return new DetectedProgrammingCli(
                    definition.Id,
                    definition.Kind,
                    definition.Name,
                    definition.Vendor,
                    version,
                    catalog.Models,
                    catalog.ReasoningLevels);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                _logger.LogDebug(exception, "CLI detection command failed.");
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the CLI's "list models" command (when it has one) and derives its model catalogue — and, for
    /// CLIs that report it, the reasoning levels — from a single invocation, merging each list behind the
    /// <see cref="DefaultModelOption"/> sentinel. Any failure — no discovery command, a timeout, an
    /// unparseable response, or an empty result — falls back to the definition's static lists so a live CLI
    /// is never left without a usable list.
    /// </summary>
    private async Task<CliCatalog> DiscoverCatalogAsync(
        CliCommandResolver resolver,
        string command,
        ProgrammingCliDefinition definition,
        CancellationToken cancellationToken)
    {
        var fallback = new CliCatalog(definition.Models, definition.ReasoningLevels);
        if (definition.ModelListArguments is null || definition.ParseModels is null)
        {
            return fallback;
        }

        try
        {
            var invocation = resolver.Resolve(command, definition.ModelListArguments);
            var output = await _probe.RunAsync(invocation, ModelDiscoveryTimeout, cancellationToken).ConfigureAwait(false);
            if (output.ExitCode != 0 || output.Truncated)
            {
                return fallback;
            }

            var stdout = output.StandardOutput;

            var discoveredModels = definition.ParseModels(stdout);
            var models = discoveredModels.Count == 0 ? definition.Models : MergeWithDefault(discoveredModels);

            var reasoningLevels = definition.ReasoningLevels;
            if (definition.ParseReasoningLevels is not null)
            {
                var discoveredReasoning = definition.ParseReasoningLevels(stdout);
                if (discoveredReasoning.Count > 0)
                {
                    reasoningLevels = MergeWithDefault(discoveredReasoning);
                }
            }

            return new CliCatalog(models, reasoningLevels);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException or TimeoutException)
        {
            _logger.LogDebug(exception, "CLI detection command failed.");
            return fallback;
        }
    }

    /// <summary>
    /// Prepends the <see cref="DefaultModelOption"/> sentinel to a discovered list, dropping any duplicate
    /// the CLI may already have emitted so the sentinel appears exactly once and first.
    /// </summary>
    private static IReadOnlyList<string> MergeWithDefault(IReadOnlyList<string> discovered)
    {
        var models = new List<string>(discovered.Count + 1) { DefaultModelOption };
        models.AddRange(discovered.Where(model =>
            !string.Equals(model, DefaultModelOption, StringComparison.OrdinalIgnoreCase)));
        return models;
    }

    private async Task<string?> ReadVersionAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        var output = await _probe.RunAsync(invocation, VersionTimeout, cancellationToken).ConfigureAwait(false);
        if (output.ExitCode != 0) throw new InvalidOperationException($"CLI probe exited with code {output.ExitCode}.");
        if (output.Truncated) throw new InvalidOperationException("CLI version output exceeded its size limit.");

        // Some CLIs print their version banner to stderr; prefer stdout but accept stderr as a fallback.
        return string.IsNullOrWhiteSpace(output.StandardOutput) ? output.StandardError : output.StandardOutput;
    }


    private static string NormalizeVersion(string command, string? rawVersion)
    {
        var version = rawVersion?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);

        if (string.IsNullOrWhiteSpace(version))
        {
            return "已安装";
        }

        return VersionRegex().IsMatch(version)
            ? version
            : $"{command} {version}";
    }

    private static string? NormalizeCliId(string? cliId)
    {
        var normalized = cliId?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }


    [GeneratedRegex(@"\d+\.\d+")]
    private static partial Regex VersionRegex();
}
