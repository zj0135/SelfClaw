namespace SelfClaw.Desktop.Services.Workspace.Abstractions;

public interface IWorkspaceFolderPicker
{
    string? PickFolder(nint ownerHandle, string initialDirectory);
}
