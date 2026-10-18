namespace SelfClaw.Desktop.Services.Attachments.Abstractions;

public interface IImageFilePicker
{
    IReadOnlyList<string> Pick(nint ownerHandle, string initialDirectory);
}
