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
    private TranscriptRenderState? _latestTranscript;
    private bool _isReady;

    public void Attach(Action<string> postJson)
    {
        ArgumentNullException.ThrowIfNull(postJson);
        _postJson = postJson;
    }

    public void Detach()
    {
        _postJson = null;
        _isReady = false;
    }

    public void MarkReady()
    {
        _isReady = true;
        if (_latestTranscript is not null)
        {
            PostPush(CreateTranscriptPayload(_latestTranscript));
        }
    }

    public void MarkNotReady() => _isReady = false;

    public void PublishTranscript(TranscriptRenderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _latestTranscript = state;
        PostPush(CreateTranscriptPayload(state));
    }

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

    private static object CreateTranscriptPayload(TranscriptRenderState state)
        => new
        {
            type = "replaceState",
            state.AutoScroll,
            state.Items,
            state.Conversations,
            state.SelectedConversationId,
            state.IsBusy,
            state.ActivityText,
            state.AgentMode,
            state.SelectedAgentId,
            state.SelectedAgentName,
            state.CapabilityRevision
        };
}
