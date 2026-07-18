using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders;

internal sealed class AiChatClientFactory : IAiChatClientFactory
{
    private const string ApiKeySecretName = AiProviderSecrets.ApiKeySecretName;

    private readonly IAiProviderRepository _repository;
    private readonly IAiProviderRegistry _registry;
    private readonly ISecretProtector _secretProtector;
    private readonly ILoggerFactory _safeLoggerFactory;

    public AiChatClientFactory(
        IAiProviderRepository repository,
        IAiProviderRegistry registry,
        ISecretProtector secretProtector,
        ILoggerFactory? loggerFactory = null)
    {
        _repository = repository;
        _registry = registry;
        _secretProtector = secretProtector;
        _safeLoggerFactory = new NonSensitiveLoggerFactory(loggerFactory ?? NullLoggerFactory.Instance);
    }

    public async Task<AiChatClientLease> CreateAsync(
        Guid modelProfileId,
        AiChatRuntimeInputs inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var profile = await _repository.GetModelProfileAsync(modelProfileId, cancellationToken)
            ?? throw new KeyNotFoundException($"AI model profile '{modelProfileId}' was not found.");
        if (!profile.IsEnabled)
        {
            throw new InvalidOperationException($"AI model profile '{profile.Name}' is disabled.");
        }

        var connection = await _repository.GetProviderConnectionAsync(
                profile.ProviderConnectionId,
                cancellationToken)
            ?? throw new KeyNotFoundException(
                $"AI provider connection '{profile.ProviderConnectionId}' for model '{profile.Name}' was not found.");
        if (!connection.IsEnabled)
        {
            throw new InvalidOperationException($"AI provider connection '{connection.Name}' is disabled.");
        }

        var secrets = await ResolveSecretsAsync(connection, cancellationToken);
        var adapter = _registry.GetRequiredAdapter(connection.ProviderKind);
        if (!adapter.SupportsApiFormat(profile.ApiFormat))
        {
            throw new NotSupportedException(
                $"AI provider '{connection.Name}' does not support API format '{profile.ApiFormat}' " +
                $"for model profile '{profile.Name}'.");
        }

        var request = new AiProviderClientRequest(
            connection,
            profile,
            secrets,
            inputs.EnableReasoning,
            inputs.Tools);
        var options = adapter.CreateChatOptions(request);
        var nativeClient = adapter.CreateChatClient(request);

        try
        {
            var client = new ChatClientBuilder(nativeClient)
                .UseFunctionInvocation(_safeLoggerFactory)
                .UseLogging(_safeLoggerFactory)
                .Build();
            return new AiChatClientLease(client, options, profile);
        }
        catch
        {
            nativeClient.Dispose();
            throw;
        }
    }

    public async Task<AiChatClientLease> CreateForScopeAsync(
        string scope,
        AiChatRuntimeInputs inputs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("A model selection scope is required.", nameof(scope));
        }

        var selection = await _repository.GetModelProfileSelectionAsync(scope.Trim(), cancellationToken)
            ?? throw new InvalidOperationException(
                "No default Direct model is selected. Choose a default model in the AI provider settings.");
        return await CreateAsync(selection.ModelProfileId, inputs, cancellationToken);
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

        var secrets = await AiProviderSecrets.ResolveAsync(_secretProtector, connection, cancellationToken);
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
