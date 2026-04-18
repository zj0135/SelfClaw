using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SelfClaw.Desktop.Notifications;

public partial class DesktopNotificationWindow : Window
{
    private readonly Func<Task>? _primaryAction;
    private readonly Func<Task>? _secondaryAction;
    private readonly Func<string, Task>? _replyAction;
    private readonly TimeSpan? _autoCloseAfter;
    private DispatcherTimer? _autoCloseTimer;

    public DesktopNotificationWindow(
        string title,
        string message,
        string? primaryActionLabel = null,
        Func<Task>? primaryAction = null,
        string? secondaryActionLabel = null,
        Func<Task>? secondaryAction = null,
        Func<string, Task>? replyAction = null,
        TimeSpan? autoCloseAfter = null)
    {
        InitializeComponent();

        _primaryAction = primaryAction;
        _secondaryAction = secondaryAction;
        _replyAction = replyAction;
        _autoCloseAfter = autoCloseAfter;

        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;

        ConfigureActionButton(PrimaryActionButton, primaryActionLabel, primaryAction);
        ConfigureActionButton(SecondaryActionButton, secondaryActionLabel, secondaryAction);
        ActionPanel.Visibility = PrimaryActionButton.Visibility == Visibility.Visible ||
                                 SecondaryActionButton.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReplyPanel.Visibility = _replyAction is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        ShowActivated = ActionPanel.Visibility == Visibility.Visible ||
                        ReplyPanel.Visibility == Visibility.Visible;
        Loaded += OnLoaded;
        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ReplyPanel.Visibility == Visibility.Visible)
        {
            ReplyTextBox.Focus();
        }

        RestartAutoCloseTimer();
    }

    private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _autoCloseTimer?.Stop();
    }

    private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        RestartAutoCloseTimer();
    }

    private void RestartAutoCloseTimer()
    {
        if (_autoCloseAfter is not TimeSpan delay ||
            ActionPanel.Visibility == Visibility.Visible ||
            ReplyPanel.Visibility == Visibility.Visible)
        {
            return;
        }

        _autoCloseTimer?.Stop();
        _autoCloseTimer ??= new DispatcherTimer
        {
            Interval = delay
        };
        _autoCloseTimer.Tick -= OnAutoCloseTimerTick;
        _autoCloseTimer.Tick += OnAutoCloseTimerTick;
        _autoCloseTimer.Start();
    }

    private void OnAutoCloseTimerTick(object? sender, EventArgs e)
    {
        _autoCloseTimer?.Stop();
        Close();
    }

    private void ConfigureActionButton(Button button, string? label, Func<Task>? action)
    {
        if (action is null || string.IsNullOrWhiteSpace(label))
        {
            button.Visibility = Visibility.Collapsed;
            return;
        }

        button.Content = label.Trim();
        button.Visibility = Visibility.Visible;
    }

    private async void OnDismissButtonClick(object sender, RoutedEventArgs e)
    {
        if (ActionPanel.Visibility == Visibility.Visible && _secondaryAction is not null)
        {
            await ExecuteActionAndCloseAsync(_secondaryAction);
            return;
        }

        Close();
    }

    private async void OnPrimaryActionButtonClick(object sender, RoutedEventArgs e)
    {
        await ExecuteActionAndCloseAsync(_primaryAction);
    }

    private async void OnSecondaryActionButtonClick(object sender, RoutedEventArgs e)
    {
        await ExecuteActionAndCloseAsync(_secondaryAction);
    }

    private void OnReplyTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        ReplyButton.IsEnabled = !string.IsNullOrWhiteSpace(ReplyTextBox.Text);
    }

    private async void OnReplyTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await ExecuteReplyAndCloseAsync();
    }

    private async void OnReplyButtonClick(object sender, RoutedEventArgs e)
    {
        await ExecuteReplyAndCloseAsync();
    }

    private async Task ExecuteActionAndCloseAsync(Func<Task>? action)
    {
        SetInteractiveState(false);

        try
        {
            if (action is not null)
            {
                await action();
            }
        }
        finally
        {
            Close();
        }
    }

    private async Task ExecuteReplyAndCloseAsync()
    {
        var prompt = ReplyTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt) || _replyAction is null)
        {
            return;
        }

        SetInteractiveState(false);
        Close();

        await _replyAction(prompt);
    }

    private void SetInteractiveState(bool isEnabled)
    {
        PrimaryActionButton.IsEnabled = isEnabled;
        SecondaryActionButton.IsEnabled = isEnabled;
        ReplyButton.IsEnabled = isEnabled && !string.IsNullOrWhiteSpace(ReplyTextBox.Text);
        ReplyTextBox.IsEnabled = isEnabled;
    }
}
