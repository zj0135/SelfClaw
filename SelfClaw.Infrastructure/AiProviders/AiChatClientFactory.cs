using SelfClaw.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders;

internal sealed class AiChatClientFactory : IAiChatClientFactory
{
    private const string ApiKeySecretName = AiProviderSecrets.ApiKeySecretName;

    private readonly IAiProviderRepository _repository;
    private readonly AiProviderRegistry _registry;
    private readonly ISecretProtector _secretProtector;
    private readonly AiProviderHttpClientProvider _httpClientProvider;
    private readonly ILoggerFactory _safeLoggerFactory;

    public AiChatClientFactory(
        IAiProviderRepository repository,
        AiProviderRegistry registry,
        ISecretProtector secretProtector,
        AiProviderHttpClientProvider httpClientProvider,
        ILoggerFactory? loggerFactory = null)
    {
        _repository = repository;
        _registry = registry;
        _secretProtector = secretProtector;
        _httpClientProvider = httpClientProvider;
        _safeLoggerFactory = new NonSensitiveLoggerFactory(loggerFactory ?? NullLoggerFactory.Instance);
    }

    public async Task<AiProviderClientRequest> PrepareAsync(
        Guid? modelProfileId,
        CancellationToken cancellationToken = default)
    {
        if (modelProfileId is null)
        {
            var selection = await _repository.GetModelProfileSelectionAsync(
                AiModelSelectionScopes.DesktopDefault, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "No default Direct model is selected. Choose a default model in the AI provider settings.");
            modelProfileId = selection.ModelProfileId;
        }

        var profile = await _repository.GetModelProfileAsync(modelProfileId.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"AI model profile '{modelProfileId}' was not found.");
        if (!profile.IsEnabled)
        {
            throw new InvalidOperationException($"AI model profile '{profile.Name}' is disabled.");
        }

        var connection = await _repository.GetProviderConnectionAsync(
                profile.ProviderConnectionId,
                cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"AI provider connection '{profile.ProviderConnectionId}' for model '{profile.Name}' was not found.");
        if (!connection.IsEnabled)
        {
            throw new InvalidOperationException($"AI provider connection '{connection.Name}' is disabled.");
        }

        var secrets = await ResolveSecretsAsync(connection, cancellationToken).ConfigureAwait(false);
        var adapter = _registry.GetRequiredAdapter(connection.ProviderKind);
        if (!adapter.SupportsApiFormat(profile.ApiFormat))
        {
            throw new NotSupportedException(
                $"AI provider '{connection.Name}' does not support API format '{profile.ApiFormat}' " +
                $"for model profile '{profile.Name}'.");
        }

        return new AiProviderClientRequest(
            connection,
            profile,
            secrets,
            profile.Configuration?.ReasoningEffort is string effort && effort != "none",
            []);
    }

    public AiChatClientLease Create(AiProviderClientRequest preparation, AiChatClientPipelineOptions pipeline)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(pipeline);
        var request = preparation with { Tools = pipeline.Tools };
        var adapter = _registry.GetRequiredAdapter(request.Connection.ProviderKind);
        var httpClient = _httpClientProvider.CreateTurnClient(request.Connection, pipeline.HttpHandler);
        IChatClient? nativeClient = null;

        try
        {
            var options = adapter.CreateChatOptions(request);
            nativeClient = adapter.CreateChatClient(request, httpClient);
            var client = new ChatClientBuilder(nativeClient)
                .UseFunctionInvocation(_safeLoggerFactory, option => {
                    option.MaximumIterationsPerRequest = 128;
                    option.FunctionInvoker = pipeline.FunctionInvoker;
                })
                .UseLogging(_safeLoggerFactory)
                .Build();
            return new AiChatClientLease(client, options, request.Profile, httpClient);
        }
        catch
        {
            // Cleanup must never mask the creation failure, so failures are logged and dropped.
            DisposeAfterFailedCreation(nativeClient, httpClient);
            throw;
        }
    }

    private void DisposeAfterFailedCreation(IChatClient? nativeClient, HttpClient httpClient)
    {
        var logger = _safeLoggerFactory.CreateLogger<AiChatClientFactory>();
        if (nativeClient is not null)
        {
            try
            {
                nativeClient.Dispose();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to dispose the Direct chat client after a failed pipeline setup.");
            }
        }

        try
        {
            httpClient.Dispose();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to dispose the per-turn HttpClient after a failed pipeline setup.");
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveSecretsAsync(
        AiProviderConnection connection,
        CancellationToken cancellationToken)
    {
        if (connection.AuthKind == AiProviderAuthKind.None)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (connection.AuthKind != AiProviderAuthKind.ApiKey)
        {
            throw new NotSupportedException(
                $"AI provider connection '{connection.Name}' uses unsupported authentication kind '{connection.AuthKind}'.");
        }

        if (!connection.CredentialRefs.TryGetValue(ApiKeySecretName, out var apiKeyRef) ||
            string.IsNullOrWhiteSpace(apiKeyRef))
        {
            throw MissingApiKey(connection);
        }

        var secrets = await AiProviderSecrets.ResolveAsync(_secretProtector, connection, cancellationToken).ConfigureAwait(false);
        if (!secrets.ContainsKey(ApiKeySecretName))
        {
            throw MissingApiKey(connection);
        }

        return secrets;
    }

    private static InvalidOperationException MissingApiKey(AiProviderConnection connection)
        => new($"AI provider connection '{connection.Name}' is missing the required '{ApiKeySecretName}' secret.");

    /// <summary>
    /// M.E.AI logs raw messages and options only at Trace. The wrapper keeps
    /// application logging providers while permanently suppressing Trace for
    /// this pipeline, even if global configuration enables sensitive logging.
    /// </summary>
    private sealed class NonSensitiveLoggerFactory : ILoggerFactory
    {
        private readonly ILoggerFactory _inner;

        public NonSensitiveLoggerFactory(ILoggerFactory inner)
        {
            _inner = inner;
        }

        public ILogger CreateLogger(string categoryName)
            => new NonSensitiveLogger(_inner.CreateLogger(categoryName));

        public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

        public void Dispose()
        {
            // The application container owns the wrapped logger factory.
        }
    }

    private sealed class NonSensitiveLogger : ILogger
    {
        private readonly ILogger _inner;

        public NonSensitiveLogger(ILogger inner)
        {
            _inner = inner;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.Trace && _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Trace)
            {
                _inner.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
