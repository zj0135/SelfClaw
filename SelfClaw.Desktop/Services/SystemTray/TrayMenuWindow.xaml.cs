using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SelfClaw.Desktop.Services.Windowing;
using Forms = System.Windows.Forms;

namespace SelfClaw.Desktop.Services.SystemTray;

internal sealed partial class TrayMenuWindow : Window
{
    private bool _isPetVisible;
    private bool _isDark;
    private Brush _hoverBrush = new SolidColorBrush(Color.FromArgb(0x0E, 0x00, 0x00, 0x00));
    private Brush _exitHoverBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xEF, 0x44, 0x44));
    private Brush _exitHoverTextBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private Brush _switchInactiveBrush = new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xCC));
    private Brush _switchActiveBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private Brush _normalTextBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));

    public TrayMenuWindow()
    {
        InitializeComponent();
        ApplyPlatformCorners();
    }

    public event Func<Task>? TogglePetRequested;
    public event Func<Task>? OpenMainWindowRequested;
    public event Action? ExitRequested;

    public void UpdatePetState(bool isVisible, bool animate = false)
    {
        _isPetVisible = isVisible;
        double targetX = isVisible ? 16 : 0;
        SwitchTrack.Background = isVisible ? _switchActiveBrush : _switchInactiveBrush;

        if (animate)
        {
            var animation = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            ThumbTransform.BeginAnimation(TranslateTransform.XProperty, animation);
            return;
        }

        ThumbTransform.BeginAnimation(TranslateTransform.XProperty, null);
        ThumbTransform.X = targetX;
    }

    public void ApplyTheme(bool isDark)
    {
        _isDark = isDark;
        if (isDark)
        {
            CardBorder.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24));
            CardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
            _normalTextBrush = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
            _hoverBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            _exitHoverBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xEF, 0x44, 0x44));
            _exitHoverTextBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            _switchInactiveBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
            _switchActiveBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        }
        else
        {
            CardBorder.Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF5));
            CardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
            _normalTextBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
            _hoverBrush = new SolidColorBrush(Color.FromArgb(0x0E, 0x00, 0x00, 0x00));
            _exitHoverBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xEF, 0x44, 0x44));
            _exitHoverTextBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            _switchInactiveBrush = new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xCC));
            _switchActiveBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        }

        PetIcon.Stroke = _normalTextBrush;
        PetText.Foreground = _normalTextBrush;
        OpenWindowIcon.Stroke = _normalTextBrush;
        OpenWindowText.Foreground = _normalTextBrush;
        ExitIcon.Stroke = _normalTextBrush;
        ExitText.Foreground = _normalTextBrush;
        SwitchTrack.Background = _isPetVisible ? _switchActiveBrush : _switchInactiveBrush;
    }

    public void ShowAtCursor()
    {
        var mousePos = Forms.Cursor.Position;
        var screen = Forms.Screen.FromPoint(mousePos);

        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        double workLeft = screen.WorkingArea.Left / scaleX;
        double workTop = screen.WorkingArea.Top / scaleY;
        double workRight = screen.WorkingArea.Right / scaleX;
        double workBottom = screen.WorkingArea.Bottom / scaleY;

        double cursorX = mousePos.X / scaleX;
        double cursorY = mousePos.Y / scaleY;

        UpdateLayout();
        double width = ActualWidth > 0 ? ActualWidth : 260;
        double height = ActualHeight > 0 ? ActualHeight : 106;

        double targetX = cursorX - (width / 2);
        double targetY = cursorY - height - 4;

        if (cursorY < (workTop + workBottom) / 2)
        {
            targetY = cursorY + 4;
        }

        targetX = Math.Max(workLeft + 6, Math.Min(targetX, workRight - width - 6));
        targetY = Math.Max(workTop + 6, Math.Min(targetY, workBottom - height - 6));

        Left = targetX;
        Top = targetY;

        Show();
        Activate();
        _ = SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Hide();
        }
    }

    private void OnItemMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = _hoverBrush;
        }
    }

    private void OnItemMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = Brushes.Transparent;
        }
    }

    private void OnExitMouseEnter(object sender, MouseEventArgs e)
    {
        ExitBtn.Background = _exitHoverBrush;
        ExitIcon.Stroke = _exitHoverTextBrush;
        ExitText.Foreground = _exitHoverTextBrush;
    }

    private void OnExitMouseLeave(object sender, MouseEventArgs e)
    {
        ExitBtn.Background = Brushes.Transparent;
        ExitIcon.Stroke = _normalTextBrush;
        ExitText.Foreground = _normalTextBrush;
    }

    private async void OnPetRowClick(object sender, MouseButtonEventArgs e)
    {
        UpdatePetState(!_isPetVisible, animate: true);
        if (TogglePetRequested is not null)
        {
            await TogglePetRequested.Invoke();
        }
    }

    private async void OnOpenWindowClick(object sender, MouseButtonEventArgs e)
    {
        Hide();
        if (OpenMainWindowRequested is not null)
        {
            await OpenMainWindowRequested.Invoke();
        }
    }

    private void OnExitClick(object sender, MouseButtonEventArgs e)
    {
        Hide();
        ExitRequested?.Invoke();
    }

    private void ApplyPlatformCorners()
    {
        if (!WindowDwmAttributes.IsWindows11OrGreater)
        {
            CardBorder.CornerRadius = new CornerRadius(0);
            PetRow.CornerRadius = new CornerRadius(0);
            OpenWindowBtn.CornerRadius = new CornerRadius(0);
            ExitBtn.CornerRadius = new CornerRadius(0);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);
}
