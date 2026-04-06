using System.Globalization;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SelfClaw.Infrastructure.Channels.Feishu;

/// <summary>
/// Native .NET implementation of Feishu long-connection mode.
/// Handles endpoint discovery, websocket reconnect, ping/pong and protobuf frame packing.
/// </summary>
public sealed class FeishuLongConnectionClient : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly string _appId;
    private readonly string _appSecret;
    private readonly Func<string, CancellationToken, Task> _eventHandler;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Dictionary<string, FeishuSplitPayloadBuffer> _splitBuffers = new(StringComparer.Ordinal);
    private readonly object _splitLock = new();

    private CancellationTokenSource? _lifetimeCts;
    private Task? _backgroundTask;
    private ClientWebSocket? _socket;
    private FeishuWsClientConfig _clientConfig = new();
    private int _serviceId;

    public FeishuLongConnectionClient(
        string appId,
        string appSecret,
        HttpClient httpClient,
        Func<string, CancellationToken, Task> eventHandler,
        string baseUrl = FeishuApiClient.DefaultBaseUrl,
        Action<string>? log = null)
    {
        _appId = appId;
        _appSecret = appSecret;
        _httpClient = httpClient;
        _eventHandler = eventHandler;
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _log = log;
    }

    public bool IsRunning { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning)
                return;

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await ConnectAsync(_lifetimeCts.Token);
            IsRunning = true;
            _backgroundTask = Task.Run(() => RunAsync(_lifetimeCts.Token), CancellationToken.None);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? backgroundTask;

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsRunning && _lifetimeCts is null)
                return;

            backgroundTask = _backgroundTask;
            _backgroundTask = null;
            IsRunning = false;

            _lifetimeCts?.Cancel();
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        await CloseSocketAsync();

        if (backgroundTask is not null)
        {
            try
            {
                await backgroundTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sendLock.Dispose();
        _lifecycleLock.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pingTask = PingLoopAsync(pingCts.Token);

            Exception? receiveError = null;
            try
            {
                await ReceiveLoopAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                receiveError = ex;
                _log?.Invoke($"[Feishu] Long connection receive loop failed: {ex.Message}");
            }
            finally
            {
                pingCts.Cancel();
                try
                {
                    await pingTask;
                }
                catch
                {
                }
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            await CloseSocketAsync();

            try
            {
                await ReconnectAsync(receiveError, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Feishu] Long connection stopped permanently: {ex.Message}");
                break;
            }
        }

        await CloseSocketAsync();
        IsRunning = false;
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var endpoint = await RequestEndpointAsync(cancellationToken);
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        socket.Options.SetRequestHeader("User-Agent", "OpenCowork.Feishu/1.0");

        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw new FeishuLongConnectionException(
                $"Failed to connect to Feishu long connection endpoint: {ex.Message}",
                isClientFault: false,
                inner: ex);
        }

        _socket = socket;
        _serviceId = ParseServiceId(endpoint);
        _log?.Invoke($"[Feishu] Long connection established: serviceId={_serviceId}");
    }

    private async Task<Uri> RequestEndpointAsync(CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["AppID"] = _appId,
            ["AppSecret"] = _appSecret
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(_baseUri, FeishuWsConstants.EndpointPath.TrimStart('/')))
        {
            Content = FeishuJson.CreateJsonContent(body)
        };
        request.Headers.TryAddWithoutValidation("locale", "zh");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        using var document = await FeishuJson.ReadJsonDocumentAsync(response, cancellationToken);
        var root = document.RootElement;
        var code = FeishuJson.GetInt32(root, "code") ?? -1;
        if (code != FeishuWsConstants.ResponseOk)
            throw CreateEndpointException(root, code);

        var data = FeishuJson.GetPropertyOrThrow(root, "data");
        var endpointUrl = FeishuJson.GetString(data, "URL");
        if (string.IsNullOrWhiteSpace(endpointUrl))
            throw new FeishuLongConnectionException("Feishu endpoint response did not contain a URL.", isClientFault: false);

        var clientConfigElement = FeishuJson.GetPropertyOrNull(data, "ClientConfig");
        _clientConfig = clientConfigElement is { ValueKind: JsonValueKind.Object }
            ? FeishuWsClientConfig.Parse(clientConfigElement.Value)
            : new FeishuWsClientConfig();

        return new Uri(endpointUrl, UriKind.Absolute);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var socket = _socket;
            if (socket is null || socket.State != WebSocketState.Open)
                return;

            using var messageBuffer = new MemoryStream();
            while (true)
            {
                var segment = new byte[16 * 1024];
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(segment), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                if (result.Count > 0)
                    messageBuffer.Write(segment, 0, result.Count);

                if (result.EndOfMessage)
                {
                    if (messageBuffer.Length > 0)
                    {
                        if (result.MessageType == WebSocketMessageType.Binary)
                            await HandleFrameAsync(messageBuffer.ToArray(), cancellationToken);
                        else if (result.MessageType == WebSocketMessageType.Text)
                            _log?.Invoke($"[Feishu] Ignored unexpected text frame: {Encoding.UTF8.GetString(messageBuffer.ToArray())}");
                    }
                    break;
                }
            }
        }
    }

    private async Task HandleFrameAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        FeishuWsFrame frame;
        try
        {
            frame = FeishuWsFrame.Parse(bytes);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Feishu] Failed to parse frame: {ex.Message}; len={bytes.Length}; head={ToHexPreview(bytes)}");
            throw;
        }

        var frameType = frame.GetHeader(FeishuWsConstants.HeaderType);

        if (frame.Method == FeishuWsConstants.FrameTypeControl)
        {
            if (string.Equals(frameType, FeishuWsConstants.MessageTypePong, StringComparison.Ordinal))
                ApplyPongConfig(DecodePayload(frame));
            return;
        }

        var payload = GetCompletePayload(frame);
        if (payload is null)
            return;

        var startedAt = DateTimeOffset.UtcNow;
        var responseCode = 200;
        try
        {
            if (string.Equals(frameType, FeishuWsConstants.MessageTypeEvent, StringComparison.Ordinal))
            {
                var raw = Encoding.UTF8.GetString(payload);
                await _eventHandler(raw, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            responseCode = 500;
            _log?.Invoke($"[Feishu] Event handling failed: {ex.Message}");
        }

        var latencyMs = Math.Max(1, (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        frame.SetHeader(FeishuWsConstants.HeaderBizRt, latencyMs.ToString(CultureInfo.InvariantCulture));
        frame.PayloadEncoding = string.Empty;
        frame.PayloadType = "application/json";
        frame.Payload = BuildAckPayload(responseCode);
        await SendFrameAsync(frame, cancellationToken);
    }

    private byte[]? GetCompletePayload(FeishuWsFrame frame)
    {
        var decodedPayload = DecodePayload(frame);
        var totalSegments = frame.GetHeaderInt(FeishuWsConstants.HeaderSum);
        if (totalSegments <= 1)
            return decodedPayload;

        var sequence = frame.GetHeaderInt(FeishuWsConstants.HeaderSeq);
        var messageId = FirstNonEmpty(
            frame.GetHeader(FeishuWsConstants.HeaderMessageId),
            frame.GetHeader(FeishuWsConstants.HeaderTraceId),
            frame.LogIdNew,
            $"{frame.LogId}:{frame.SeqId}");

        lock (_splitLock)
        {
            CleanupExpiredSplitBuffers();

            if (!_splitBuffers.TryGetValue(messageId, out var buffer))
            {
                buffer = new FeishuSplitPayloadBuffer(totalSegments);
                _splitBuffers[messageId] = buffer;
            }

            var combined = buffer.AddSegment(sequence, decodedPayload);
            if (combined is not null)
                _splitBuffers.Remove(messageId);

            return combined;
        }
    }

    private byte[] DecodePayload(FeishuWsFrame frame)
    {
        if (frame.Payload.Length == 0)
            return frame.Payload;

        var encoding = frame.PayloadEncoding?.Trim().ToLowerInvariant();
        return encoding switch
        {
            null or "" or "identity" => frame.Payload,
            "gzip" => Decompress(frame.Payload, useGzip: true),
            "deflate" or "zlib" => Decompress(frame.Payload, useGzip: false),
            _ => frame.Payload
        };
    }

    private static byte[] Decompress(byte[] payload, bool useGzip)
    {
        using var input = new MemoryStream(payload);
        using Stream stream = useGzip
            ? new GZipStream(input, CompressionMode.Decompress)
            : new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private void ApplyPongConfig(byte[] payload)
    {
        if (payload.Length == 0)
            return;

        var json = Encoding.UTF8.GetString(payload);
        if (!FeishuJson.TryParseDocument(json, out var document) || document is null)
            return;

        using (document)
        {
            var root = document.RootElement;
            var config = FeishuJson.GetPropertyOrNull(root, "data");
            if (config is not { ValueKind: JsonValueKind.Object })
                return;

            _clientConfig = FeishuWsClientConfig.Parse(config.Value);
        }
    }

    private async Task PingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_clientConfig.PingInterval, cancellationToken);

            var socket = _socket;
            if (socket is null || socket.State != WebSocketState.Open || _serviceId == 0)
                continue;

            await SendFrameAsync(FeishuWsFrame.CreatePing(_serviceId), cancellationToken);
        }
    }

    private async Task ReconnectAsync(Exception? lastError, CancellationToken cancellationToken)
    {
        if (_clientConfig.ReconnectNonceSeconds > 0)
        {
            var jitterSeconds = Random.Shared.Next(0, _clientConfig.ReconnectNonceSeconds + 1);
            await Task.Delay(TimeSpan.FromSeconds(jitterSeconds), cancellationToken);
        }

        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await ConnectAsync(cancellationToken);
                return;
            }
            catch (FeishuLongConnectionException ex) when (ex.IsClientFault)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log?.Invoke($"[Feishu] Reconnect attempt {attempt} failed: {ex.Message}");
            }

            if (_clientConfig.ReconnectCount >= 0 && attempt >= _clientConfig.ReconnectCount)
            {
                throw new FeishuLongConnectionException(
                    $"Feishu long connection reconnect limit reached. Last error: {lastError?.Message}",
                    isClientFault: false,
                    inner: lastError);
            }

            await Task.Delay(_clientConfig.ReconnectInterval, cancellationToken);
        }
    }

    private async Task SendFrameAsync(FeishuWsFrame frame, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            return;

        var payload = frame.ToArray();
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(
                new ArraySegment<byte>(payload),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task CloseSocketAsync()
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket is null)
            return;

        try
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "client shutdown",
                    CancellationToken.None);
            }
        }
        catch
        {
        }
        finally
        {
            socket.Dispose();
        }
    }

    private void CleanupExpiredSplitBuffers()
    {
        if (_splitBuffers.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var expiredKeys = _splitBuffers
            .Where(entry => now - entry.Value.LastUpdatedUtc > TimeSpan.FromSeconds(5))
            .Select(entry => entry.Key)
            .ToArray();

        foreach (var key in expiredKeys)
        {
            _splitBuffers.Remove(key);
        }
    }

    private static FeishuLongConnectionException CreateEndpointException(JsonElement root, int code)
    {
        var message = FeishuJson.GetString(root, "msg", "Unknown error");
        var isClientFault = code is FeishuWsConstants.Forbidden or FeishuWsConstants.AuthFailed or FeishuWsConstants.ExceedConnectionLimit;
        return new FeishuLongConnectionException(
            $"Feishu endpoint request failed: {message} (code={code})",
            isClientFault,
            code);
    }

    private static int ParseServiceId(Uri endpoint)
    {
        var query = endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var item in query)
        {
            var separator = item.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = Uri.UnescapeDataString(item[..separator]);
            if (!string.Equals(key, FeishuWsConstants.ServiceId, StringComparison.Ordinal))
                continue;

            var value = Uri.UnescapeDataString(item[(separator + 1)..]);
            return int.TryParse(value, out var serviceId) ? serviceId : 0;
        }

        return 0;
    }

    private static byte[] BuildAckPayload(int responseCode)
    {
        var payload = new JsonObject
        {
            ["code"] = responseCode,
            ["headers"] = new JsonObject(),
            ["data"] = null
        };

        return Encoding.UTF8.GetBytes(payload.ToJsonString());
    }

    private static string ToHexPreview(byte[] bytes)
    {
        var count = Math.Min(bytes.Length, 32);
        return Convert.ToHexString(bytes, 0, count);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }
}



