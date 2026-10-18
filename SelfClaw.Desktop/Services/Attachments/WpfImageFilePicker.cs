using Forms = System.Windows.Forms;
using SelfClaw.Desktop.Services.Attachments.Abstractions;

namespace SelfClaw.Desktop.Services.Attachments;

internal sealed class WpfImageFilePicker : IImageFilePicker
{
    public IReadOnlyList<string> Pick(nint ownerHandle, string initialDirectory)
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "添加图片",
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.webp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.gif",
            InitialDirectory = initialDirectory
        };
        // The host passes the WebView's own HWND; WinForms is the only dialog family that accepts a bare
        // handle, which is why this mirrors WpfWorkspaceFolderPicker instead of Microsoft.Win32.
        var result = dialog.ShowDialog(new WindowHandleWrapper(ownerHandle));
        return result == Forms.DialogResult.OK ? dialog.FileNames : [];
    }

    private sealed class WindowHandleWrapper(nint handle) : Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }
}
