using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SelfClaw.Desktop.Pet;

/// <summary>
/// 桌宠浮窗:无边框、透明、置顶、不占任务栏的独立窗口。
/// 负责窗口几何与指针交互(拖拽、点击/拖拽区分),并把交互转发给 ViewModel 状态机。
/// 详见 docs/pet-system-design.md §6.3 / §7。
/// </summary>
public partial class PetWindow : Window
{
    /// <summary>抖动过滤阈值(DIP):指针位移小于此值不视为拖动(区分点击 vs 拖动)。见 §3.3。</summary>
    private const double DragThreshold = 4d;
    private const double DragGestureMin = 14d;
    private const double DragAxisBias = 1.18d;

    private Point _pressOriginScreen;
    private double _windowLeftAtPress;
    private double _windowTopAtPress;
    private bool _isPressed;
    private bool _isDragging;
    private readonly PetViewModel _viewModel;

    /// <summary>拖拽结束且位置发生变化时触发,携带当前窗口左上角坐标(屏幕 DIP)。</summary>
    public event EventHandler<Point>? PositionCommitted;

    /// <summary>被点击(按下到抬起未超过抖动阈值)时触发。阶段3 用于切换气泡。</summary>
    public event EventHandler? Clicked;

    public PetWindow()
        : this(new PetViewModel())
    {
    }

    public PetWindow(PetViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;
        IsVisibleChanged += OnIsVisibleChanged;
        Closed += OnClosed;
    }

    public void LoadPet(PetSettings settings)
    {
        _viewModel.Load(settings);
        if (IsVisible)
        {
            _viewModel.StartAnimation();
        }
    }

    private void OnPetMouseEnter(object sender, MouseEventArgs e)
    {
        _viewModel.PointerEntered();
    }

    private void OnPetMouseLeave(object sender, MouseEventArgs e)
    {
        _viewModel.PointerExited();
    }

    private void OnPetMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPressed = true;
        _isDragging = false;
        _pressOriginScreen = PointToScreen(e.GetPosition(this));
        _windowLeftAtPress = Left;
        _windowTopAtPress = Top;
        _viewModel.PointerPressed();
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
        _viewModel.DismissBubble();
        var dragInteraction = ResolveDragInteraction(dx, dy);
        if (dragInteraction is not null)
        {
            _viewModel.DragDirectionChanged(dragInteraction.Value);
        }

        Left = _windowLeftAtPress + dx;
        Top = _windowTopAtPress + dy;
    }

    private static PetInteraction? ResolveDragInteraction(double dx, double dy)
    {
        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < DragGestureMin)
        {
            return null;
        }

        var absX = Math.Abs(dx);
        var absY = Math.Abs(dy);
        if (absX > absY * DragAxisBias)
        {
            return dx >= 0 ? PetInteraction.DragRight : PetInteraction.DragLeft;
        }

        if (absY > absX * DragAxisBias)
        {
            return dy >= 0 ? PetInteraction.DragDown : PetInteraction.DragUp;
        }

        return null;
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
            _viewModel.PointerReleased(PetRoot.IsMouseOver);
            PositionCommitted?.Invoke(this, new Point(Left, Top));
            return;
        }

        _viewModel.PointerReleased(PetRoot.IsMouseOver);
        if (commitClick)
        {
            _viewModel.ToggleBubble();
            Clicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Equals(e.NewValue, true))
        {
            _viewModel.StartAnimation();
        }
        else
        {
            _viewModel.StopAnimation();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        IsVisibleChanged -= OnIsVisibleChanged;
        Closed -= OnClosed;
        _viewModel.Dispose();
    }
}
