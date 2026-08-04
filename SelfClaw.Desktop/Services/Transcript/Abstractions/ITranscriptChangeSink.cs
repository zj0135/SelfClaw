namespace SelfClaw.Desktop.Services.Transcript.Abstractions;

internal interface ITranscriptChangeSink
{
    void RequestStreamingPublish(bool autoScroll);

    void PublishNow(bool autoScroll);
}
