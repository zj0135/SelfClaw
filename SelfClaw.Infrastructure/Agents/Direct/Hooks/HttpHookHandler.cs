using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks;

/// <summary>
/// The per-turn outermost HTTP handler: it runs the <c>httpRequestSending</c> hooks before the request
/// travels down the shared handler chain and reports <c>httpResponseReceived</c> once the response
/// headers arrive (or the transport fails). It never wraps or waits for the response body.
/// </summary>
internal sealed class HttpHookHandler : DelegatingHandler
{
    private const int MaximumBodyBytes = 1024 * 1024;

    private readonly DirectTurnHooks _hooks;
    private readonly IReadOnlySet<string> _reservedHeaderNames;
    private readonly CancellationToken _turnCancellation;
    private readonly ILogger<HttpHookHandler> _logger;
    private int _sequence;

    public HttpHookHandler(
        DirectTurnHooks hooks,
        IReadOnlySet<string> reservedHeaderNames,
        CancellationToken turnCancellation,
        ILogger<HttpHookHandler>? logger = null)
    {
        _hooks = hooks;
        _reservedHeaderNames = reservedHeaderNames;
        _turnCancellation = turnCancellation;
        _logger = logger ?? NullLogger<HttpHookHandler>.Instance;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var host = request.RequestUri?.Host ?? string.Empty;
        if (_hooks.HasHttpHooks)
        {
            string? body = null;
            var bodyTruncated = false;
            if (_hooks.WantsRequestBody(host) && request.Content is not null)
            {
                (body, bodyTruncated) = await ReadBodyAsync(request.Content, cancellationToken).ConfigureAwait(false);
            }

            var outcome = await _hooks
                .HttpRequestSendingAsync(new HttpHookRequest(sequence, request, body, bodyTruncated), cancellationToken)
                .ConfigureAwait(false);
            ApplyHeaders(request, outcome);
        }

        var start = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_turnCancellation.IsCancellationRequested)
        {
            _hooks.HttpResponseReceived(new HttpHookResponse(
                sequence, request, null, Elapsed(start), "timeout"));
            throw;
        }
        catch (OperationCanceledException)
        {
            // The turn itself was cancelled: the hook set is torn down, so nothing is delivered.
            throw;
        }
        catch (Exception exception)
        {
            _hooks.HttpResponseReceived(new HttpHookResponse(
                sequence, request, null, Elapsed(start), exception.Message));
            throw;
        }

        _hooks.HttpResponseReceived(new HttpHookResponse(sequence, request, response, Elapsed(start), null));
        return response;
    }

    private void ApplyHeaders(HttpRequestMessage request, HttpRequestSendingOutcome outcome)
    {
        foreach (var pair in outcome.Headers)
        {
            if (request.Headers.Contains(pair.Key) ||
                request.Content is not null && request.Content.Headers.Contains(pair.Key))
            {
                _logger.LogInformation("Hook header '{Header}' is already set on the request and was ignored.", pair.Key);
                continue;
            }

            if (_reservedHeaderNames.Contains(pair.Key))
            {
                _logger.LogInformation("Hook header '{Header}' is reserved by the connection and was ignored.", pair.Key);
                continue;
            }

            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
    }

    private static async Task<(string? Body, bool Truncated)> ReadBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[8192];
        using var sink = new MemoryStream();
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            var remaining = MaximumBodyBytes - sink.Length;
            if (remaining <= 0)
            {
                break;
            }

            sink.Write(buffer, 0, (int)Math.Min(remaining, read));
        }

        var bytes = sink.ToArray();
        var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();
        var characters = new char[bytes.Length];
        // flush:false leaves an incomplete trailing multi-byte sequence out of the decoded text.
        var count = decoder.GetChars(bytes, 0, bytes.Length, characters, 0, flush: false);
        return (new string(characters, 0, count), total > MaximumBodyBytes);
    }

    private static TimeSpan Elapsed(long startTimestamp)
        => TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency);
}
