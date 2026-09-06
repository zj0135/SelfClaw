using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.Http;

internal sealed class AiProviderHttpClientProvider : IDisposable
{
    internal const int DefaultTimeoutSeconds = 100;

    private readonly ConcurrentDictionary<ClientCacheKey, Lazy<HttpClient>> _clients = new();
    private readonly ConcurrentDictionary<string, Lazy<HttpMessageHandler>> _sharedHandlers = new();
    private readonly Func<HttpMessageHandler> _primaryHandlerFactory;
    private bool _disposed;

    public AiProviderHttpClientProvider()
        : this(CreateSocketsHttpHandler)
    {
    }

    internal AiProviderHttpClientProvider(Func<HttpMessageHandler> primaryHandlerFactory)
    {
        _primaryHandlerFactory = primaryHandlerFactory;
    }

    public HttpClient GetStreamingClient(AiProviderConnection connection)
        => GetClient(connection, streaming: true);

    public HttpClient GetNonStreamingClient(AiProviderConnection connection)
        => GetClient(connection, streaming: false);

    public TimeSpan GetNonStreamingTimeout(AiProviderConnection connection)
        => ReadConfiguration(connection).Timeout;

    /// <summary>
    /// Returns the shared streaming handler for a connection: every caller gets the same handler and
    /// therefore the same connection pool. A caller that builds its own <see cref="HttpClient"/> on top
    /// must construct it with <c>disposeHandler: false</c> — this provider owns the handler's lifetime.
    /// </summary>
    internal HttpMessageHandler GetSharedStreamingHandler(AiProviderConnection connection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var configuration = ReadConfiguration(connection);
        return _sharedHandlers.GetOrAdd(
            configuration.Fingerprint,
            _ => new Lazy<HttpMessageHandler>(
                () => CreateHandler(configuration),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    internal int CachedClientCount => _clients.Count;

    internal int CachedSharedHandlerCount => _sharedHandlers.Count;

    private HttpClient GetClient(AiProviderConnection connection, bool streaming)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var configuration = ReadConfiguration(connection);
        var key = new ClientCacheKey(configuration.Fingerprint, streaming);
        var lazy = _clients.GetOrAdd(
            key,
            _ => new Lazy<HttpClient>(
                () => CreateClient(connection.Endpoint, configuration, streaming),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private HttpClient CreateClient(Uri endpoint, ClientConfiguration configuration, bool streaming)
        => new(CreateHandler(configuration), disposeHandler: true)
        {
            BaseAddress = endpoint,
            Timeout = streaming ? Timeout.InfiniteTimeSpan : configuration.Timeout
        };

    private HttpMessageHandler CreateHandler(ClientConfiguration configuration)
    {
        HttpMessageHandler handler = _primaryHandlerFactory();
        if (configuration.ExtraHeaders.Count > 0)
        {
            handler = new ExtraHeadersHandler(configuration.ExtraHeaders) { InnerHandler = handler };
        }

        return handler;
    }

    private static ClientConfiguration ReadConfiguration(AiProviderConnection connection)
    {
        var timeout = TimeSpan.FromSeconds(ReadTimeoutSeconds(connection.ConnectionOptions));
        var headers = ReadExtraHeaders(connection.ConnectionOptions);
        var fingerprint = ComputeFingerprint(connection.Endpoint, timeout, headers);
        return new ClientConfiguration(timeout, headers, fingerprint);
    }

    private static int ReadTimeoutSeconds(IReadOnlyDictionary<string, JsonElement> options)
    {
        if (!options.TryGetValue("timeout_seconds", out var value))
        {
            return DefaultTimeoutSeconds;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var seconds) ||
            seconds <= 0 ||
            seconds > 3600)
        {
            throw new InvalidOperationException(
                "AI provider connection option 'timeout_seconds' must be an integer from 1 to 3600.");
        }

        return seconds;
    }

    private static IReadOnlyDictionary<string, string> ReadExtraHeaders(
        IReadOnlyDictionary<string, JsonElement> options)
    {
        if (!options.TryGetValue("extra_headers", out var value))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "AI provider connection option 'extra_headers' must be a JSON object of string values.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"AI provider extra header '{property.Name}' must have a string value.");
            }

            var headerValue = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(property.Name) || headerValue is null)
            {
                throw new InvalidOperationException("AI provider extra header names and values cannot be null.");
            }

            headers[property.Name] = headerValue;
        }

        return headers;
    }

    private static string ComputeFingerprint(
        Uri endpoint,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string> headers)
    {
        var builder = new StringBuilder();
        builder.Append(endpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant());
        builder.Append(endpoint.PathAndQuery.TrimEnd('/'));
        builder.Append('|').Append(timeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var header in headers.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append('|').Append(header.Key.ToUpperInvariant()).Append(':').Append(header.Value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static SocketsHttpHandler CreateSocketsHttpHandler()
        => new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var client in _clients.Values)
        {
            if (client.IsValueCreated)
            {
                client.Value.Dispose();
            }
        }

        _clients.Clear();
        foreach (var handler in _sharedHandlers.Values)
        {
            if (handler.IsValueCreated)
            {
                handler.Value.Dispose();
            }
        }

        _sharedHandlers.Clear();
    }

    private sealed record ClientConfiguration(
        TimeSpan Timeout,
        IReadOnlyDictionary<string, string> ExtraHeaders,
        string Fingerprint);

    private readonly record struct ClientCacheKey(string Fingerprint, bool Streaming);

    private sealed class ExtraHeadersHandler : DelegatingHandler
    {
        private readonly IReadOnlyDictionary<string, string> _headers;

        public ExtraHeadersHandler(IReadOnlyDictionary<string, string> headers)
        {
            _headers = headers;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            foreach (var header in _headers)
            {
                if (!request.Headers.Contains(header.Key))
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
