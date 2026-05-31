using System;
using System.Windows;
using System.Windows.Controls;

namespace SelfClaw.Desktop.Controls;

public partial class TerminalPanel : UserControl
{
    public event EventHandler? CloseRequested;

    public TerminalPanel()
    {
        InitializeComponent();
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
