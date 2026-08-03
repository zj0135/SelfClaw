namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void OnSelectedTranscriptChanged(bool immediate)
    {
        if (immediate)
        {
            PublishShellNow(true);
            return;
        }

        RequestStreamingShellPublish(true);
    }
}
