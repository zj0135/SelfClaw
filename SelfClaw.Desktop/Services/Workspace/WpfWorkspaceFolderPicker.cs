using Forms = System.Windows.Forms;
using SelfClaw.Desktop.Services.Workspace.Abstractions;

namespace SelfClaw.Desktop.Services.Workspace;

internal sealed class WpfWorkspaceFolderPicker : IWorkspaceFolderPicker
{
    public string? PickFolder(nint ownerHandle, string initialDirectory)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择工作目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = initialDirectory
        };
        var result = dialog.ShowDialog(new WindowHandleWrapper(ownerHandle));
        return result == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath)
            ? dialog.SelectedPath
            : null;
    }

    private sealed class WindowHandleWrapper(nint handle) : Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }
}
