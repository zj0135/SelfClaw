using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SelfClaw.Desktop.Pet;

public sealed class PetViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILogger<PetViewModel>? _logger;
    private readonly PetActivityPresenter? _activityPresenter;
    private readonly PetBehavior _behavior = new();
    private readonly DispatcherTimer _waitingTimer;
    private readonly DispatcherTimer _ambientTimer;
    private SpriteAnimator? _animator;
    private ImageSource? _currentFrame;
    private BitmapScalingMode _bitmapScalingMode = BitmapScalingMode.HighQuality;
    private string _bubbleText = "Ready.";
    private string _bubbleTitle = "SelfClaw";
    private string? _bubbleDetail;
    private bool _isBubbleVisible;
    private bool _isBubblePinned;
    private bool _canApprove;
    private bool _canOpenConversation;
    private bool _disposed;

    internal PetViewModel(ILogger<PetViewModel>? logger, PetActivityPresenter? activityPresenter)
    {
        _logger = logger;
        _activityPresenter = activityPresenter;
        _waitingTimer = new DispatcherTimer();
        _waitingTimer.Tick += OnWaitingTimerTick;
        _ambientTimer = new DispatcherTimer();
        _ambientTimer.Tick += OnAmbientTimerTick;
        if (_activityPresenter is not null)
        {
            _activityPresenter.StateChanged += OnActivityStateChanged;
            ApplyBubbleState(_activityPresenter.Current);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImageSource? CurrentFrame
    {
        get => _currentFrame;
        private set => SetField(ref _currentFrame, value);
    }

    public BitmapScalingMode BitmapScalingMode
    {
        get => _bitmapScalingMode;
        private set => SetField(ref _bitmapScalingMode, value);
    }

    public string BubbleText
    {
        get => _bubbleText;
        private set => SetField(ref _bubbleText, value);
    }

    public string BubbleTitle
    {
        get => _bubbleTitle;
        private set => SetField(ref _bubbleTitle, value);
    }

    public string? BubbleDetail
    {
        get => _bubbleDetail;
        private set
        {
            if (SetField(ref _bubbleDetail, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBubbleDetail)));
            }
        }
    }

    public bool HasBubbleDetail => !string.IsNullOrWhiteSpace(BubbleDetail);

    public bool IsBubbleVisible
    {
        get => _isBubbleVisible;
        private set => SetField(ref _isBubbleVisible, value);
    }

    public bool IsBubblePinned
    {
        get => _isBubblePinned;
        private set => SetField(ref _isBubblePinned, value);
    }

    public bool CanApprove
    {
        get => _canApprove;
        private set => SetField(ref _canApprove, value);
    }

    public bool CanOpenConversation
    {
        get => _canOpenConversation;
        private set => SetField(ref _canOpenConversation, value);
    }

    internal void Install(PetSettings settings, PetLoadedPackage package)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(package);
        ThrowIfDisposed();
        var sheet = package.SpriteSheet;
        var firstFrame = sheet.GetFrame(PetLayout.IdleRowId, 0);
        var animator = new SpriteAnimator(sheet, PetLayout.IdleRowId);
        if (_animator is not null)
        {
            _animator.FrameChanged -= OnFrameChanged;
            _animator.Dispose();
        }
        _animator = animator;
        _animator.FrameChanged += OnFrameChanged;
        CurrentFrame = firstFrame;
        BitmapScalingMode = settings.PixelArt ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
        SetAnimationRow(_behavior.ConfigureRows(sheet.RowIds).AnimationRowId);
    }

    public void StartAnimation()
    {
        ThrowIfDisposed();
        _animator?.Start();
        ApplyBehavior(new PetBehaviorEvent(PetBehaviorEventKind.AnimationStarted));
    }

    public void StopAnimation()
    {
        _animator?.Stop();
        ApplyBehavior(new PetBehaviorEvent(PetBehaviorEventKind.AnimationStopped));
    }

    public void PointerEntered()
    {
        ApplyBehavior(new PetBehaviorEvent(PetBehaviorEventKind.PointerEntered));
    }

    public void PointerExited()
    {
        ApplyBehavior(new PetBehaviorEvent(PetBehaviorEventKind.PointerExited));
    }

    public void PointerPressed()
    {
        ApplyBehavior(new PetBehaviorEvent(PetBehaviorEventKind.PointerPressed));
    }

    public void DismissBubble()
    {
        if (_activityPresenter is not null)
        {
            _activityPresenter.DismissBubble();
            return;
        }

        IsBubbleVisible = false;
    }

    public void DragDirectionChanged(PetInteraction dragInteraction)
    {
        DismissBubble();
        ApplyBehavior(new PetBehaviorEvent(
            PetBehaviorEventKind.DragDirectionChanged,
            DragInteraction: dragInteraction));
    }

    public void PointerReleased(bool isHovering)
    {
        ApplyBehavior(new PetBehaviorEvent(
            PetBehaviorEventKind.PointerReleased,
            IsHovering: isHovering));
    }

    public void ToggleBubble()
    {
        if (_activityPresenter is not null)
        {
            _activityPresenter.ToggleBubble();
            return;
        }

        BubbleTitle = "SelfClaw";
        BubbleText = "Ready.";
        BubbleDetail = null;
        IsBubbleVisible = !IsBubbleVisible;
    }

    public void ApproveCurrentApproval()
    {
        _activityPresenter?.TryResolveCurrentApproval(approved: true);
    }

    public void RejectCurrentApproval()
    {
        _activityPresenter?.TryResolveCurrentApproval(approved: false);
    }

    public void OpenCurrentConversation()
    {
        _activityPresenter?.RequestCurrentConversationActivation();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_activityPresenter is not null)
        {
            _activityPresenter.StateChanged -= OnActivityStateChanged;
        }
        _waitingTimer.Stop();
        _waitingTimer.Tick -= OnWaitingTimerTick;
        _ambientTimer.Stop();
        _ambientTimer.Tick -= OnAmbientTimerTick;
        if (_animator is not null)
        {
            _animator.FrameChanged -= OnFrameChanged;
            _animator.Dispose();
            _animator = null;
        }
    }

    private void OnFrameChanged(ImageSource frame)
    {
        CurrentFrame = frame;
    }

    private void OnWaitingTimerTick(object? sender, EventArgs e)
    {
        ApplyBehavior(new PetBehaviorEvent(PetBehaviorEventKind.WaitingElapsed));
    }

    private void OnAmbientTimerTick(object? sender, EventArgs e)
    {
        ApplyBehavior(new PetBehaviorEvent(PetBehaviorEventKind.AmbientElapsed));
    }

    private void SetAnimationRow(string rowId)
    {
        try
        {
            _animator?.SetRow(rowId);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Failed to switch pet animation row to {RowId}.", rowId);
        }
    }

    private void OnActivityStateChanged(object? sender, PetBubbleViewState state)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                _ = dispatcher.BeginInvoke(() => ApplyBubbleState(state));
            }

            return;
        }

        ApplyBubbleState(state);
    }

    private void ApplyBubbleState(PetBubbleViewState state)
    {
        BubbleTitle = state.AgentLabel;
        BubbleText = state.Headline;
        BubbleDetail = state.Detail;
        IsBubblePinned = state.IsPinned;
        CanApprove = state.ApprovalId is not null;
        CanOpenConversation = state.ConversationId is not null;
        SetWorkState(state.WorkState);
        IsBubbleVisible = state.IsVisible;
    }

    private void SetWorkState(PetWorkState workState)
    {
        ApplyBehavior(new PetBehaviorEvent(
            PetBehaviorEventKind.WorkStateChanged,
            WorkState: workState));
    }

    private void ApplyBehavior(PetBehaviorEvent behaviorEvent)
    {
        var result = _behavior.Apply(behaviorEvent);
        SetAnimationRow(result.AnimationRowId);
        ApplyTimerCommand(_waitingTimer, result.WaitingTimer);
        ApplyTimerCommand(_ambientTimer, result.AmbientTimer);
    }

    private static void ApplyTimerCommand(DispatcherTimer timer, PetTimerCommand command)
    {
        if (command.Operation == PetTimerOperation.Unchanged)
        {
            return;
        }

        timer.Stop();
        if (command.Operation == PetTimerOperation.Restart && command.Delay is TimeSpan delay)
        {
            timer.Interval = delay;
            timer.Start();
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

}
