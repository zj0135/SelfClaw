using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SelfClaw.Desktop.Pet;

/// <summary>
/// 桌宠浮窗:无边框、透明、置顶、不占任务栏的独立窗口。
/// 阶段1 只负责窗口几何与指针交互(拖拽、点击/拖拽区分),
/// 视觉为占位图形,帧动画留给阶段2。位置持久化委托给 <see cref="PetService"/>。
/// 详见 docs/pet-system-design.md §6.3 / §7。
/// </summary>
public partial class PetWindow : Window
{
    /// <summary>抖动过滤阈值(DIP):指针位移小于此值不视为拖动(区分点击 vs 拖动)。见 §3.3。</summary>
    private const double DragThreshold = 4d;

    private Point _pressOriginScreen;
    private double _windowLeftAtPress;
    private double _windowTopAtPress;
    private bool _isPressed;
    private bool _isDragging;

    /// <summary>拖拽结束且位置发生变化时触发,携带当前窗口左上角坐标(屏幕 DIP)。</summary>
    public event EventHandler<Point>? PositionCommitted;

    /// <summary>被点击(按下到抬起未超过抖动阈值)时触发。阶段3 用于切换气泡。</summary>
    public event EventHandler? Clicked;

    public PetWindow()
    {
        InitializeComponent();
    }

    private void OnPetMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPressed = true;
        _isDragging = false;
        _pressOriginScreen = PointToScreen(e.GetPosition(this));
        _windowLeftAtPress = Left;
        _windowTopAtPress = Top;
        PetRoot.CaptureMouse();
        e.Handled = true;
    }

    private void OnPetMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPressed)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CompletePointerInteraction(commitClick: false);
            return;
        }

        var current = PointToScreen(e.GetPosition(this));
        var physicalDx = current.X - _pressOriginScreen.X;
        var physicalDy = current.Y - _pressOriginScreen.Y;

        // PointToScreen 返回物理像素,Left/Top 是 DIP。在非 100% 缩放屏幕上
        // 必须把物理位移增量转成 DIP,否则拖动速度会与光标错位。
        var (dx, dy) = PhysicalToDip(physicalDx, physicalDy);

        if (!_isDragging && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold)
        {
            // 仍在抖动地板内,尚未构成拖动。
            return;
        }

        _isDragging = true;
        Left = _windowLeftAtPress + dx;
        Top = _windowTopAtPress + dy;
    }

    /// <summary>把物理像素位移转换为 DIP 位移,适配非 100% DPI 缩放。</summary>
    private (double Dx, double Dy) PhysicalToDip(double physicalDx, double physicalDy)
    {
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice;
        if (transform is null)
        {
            return (physicalDx, physicalDy);
        }

        var dip = transform.Value.Transform(new Vector(physicalDx, physicalDy));
        return (dip.X, dip.Y);
    }

    private void OnPetMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPressed)
        {
            return;
        }

        e.Handled = true;
        CompletePointerInteraction(commitClick: true);
    }

    private void OnPetLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isPressed)
        {
            return;
        }

        CompletePointerInteraction(commitClick: false);
    }

    private void CompletePointerInteraction(bool commitClick)
    {
        _isPressed = false;

        if (PetRoot.IsMouseCaptured)
        {
            PetRoot.ReleaseMouseCapture();
        }

        if (_isDragging)
        {
            _isDragging = false;
            PositionCommitted?.Invoke(this, new Point(Left, Top));
            return;
        }

        if (commitClick)
        {
            Clicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
