using System.Text.Json;
using System.Text.Json.Serialization;

namespace SelfClaw.Desktop.Services.WebView;

public sealed class WebViewHostChannel
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private Action<string>? _postJson;
    private bool _isReady;

    public void Attach(Action<string> postJson)
    {
        ArgumentNullException.ThrowIfNull(postJson);
        _postJson = postJson;
        _isReady = false;
        ReadyChanged?.Invoke(false);
    }

    public void Detach()
    {
        _postJson = null;
        _isReady = false;
        ReadyChanged?.Invoke(false);
    }

    public void MarkReady()
    {
        _isReady = true;
        ReadyChanged?.Invoke(true);
    }

    public void MarkNotReady()
    {
        _isReady = false;
        ReadyChanged?.Invoke(false);
    }

    /// <summary>Raised when the WebView can accept pushes, or when navigation invalidates them.</summary>
    public event Action<bool>? ReadyChanged;

    internal bool IsReady => _isReady && _postJson is not null;

    internal static byte[] SerializeToUtf8Bytes(object payload)
        => JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

    public bool PostPush(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _isReady && Post(payload);
    }

    public bool PostResponse(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Post(payload);
    }

    private bool Post(object payload)
    {
        if (_postJson is null)
        {
            return false;
        }

        _postJson(JsonSerializer.Serialize(payload, JsonOptions));
        return true;
    }

}
