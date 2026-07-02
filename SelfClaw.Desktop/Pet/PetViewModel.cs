using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace SelfClaw.Desktop.Pet;

public sealed class PetViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan WaitingAfter = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BubbleVisibleFor = TimeSpan.FromSeconds(4);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<PetViewModel>? _logger;
    private readonly PetStateMachine _stateMachine = new();
    private readonly DispatcherTimer _waitingTimer;
    private readonly DispatcherTimer _bubbleTimer;
    private SpriteAnimator? _animator;
    private ImageSource? _currentFrame;
    private BitmapScalingMode _bitmapScalingMode = BitmapScalingMode.HighQuality;
    private string? _loadError;
    private string _bubbleText = "Ready.";
    private bool _isBubbleVisible;
    private bool _isAnimationRunning;
    private bool _disposed;

    public PetViewModel(ILogger<PetViewModel>? logger = null)
    {
        _logger = logger;
        _stateMachine.InteractionChanged += OnInteractionChanged;
        _waitingTimer = new DispatcherTimer { Interval = WaitingAfter };
        _waitingTimer.Tick += OnWaitingTimerTick;
        _bubbleTimer = new DispatcherTimer { Interval = BubbleVisibleFor };
        _bubbleTimer.Tick += OnBubbleTimerTick;
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

    public string? LoadError
    {
        get => _loadError;
        private set => SetField(ref _loadError, value);
    }

    public string BubbleText
    {
        get => _bubbleText;
        private set => SetField(ref _bubbleText, value);
    }

    public bool IsBubbleVisible
    {
        get => _isBubbleVisible;
        private set => SetField(ref _isBubbleVisible, value);
    }

    public void Load(PetSettings settings)
    {
        ThrowIfDisposed();
        BitmapScalingMode = settings.PixelArt ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;

        try
        {
            var package = ResolvePackage(settings);
            var bitmap = new WebpSpriteLoader().Load(package.SpriteSheetPath);
            var sheet = SpriteSheet.Create(bitmap, package.Grid ?? settings.Grid ?? PetLayout.CreateDefaultGrid());

            _animator?.Dispose();
            _animator = new SpriteAnimator(sheet, PetLayout.IdleRowId);
            _animator.FrameChanged += OnFrameChanged;
            CurrentFrame = sheet.GetFrame(PetLayout.IdleRowId, 0);
            _stateMachine.Reset();
            LoadError = null;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Failed to load pet spritesheet.");
            _animator?.Dispose();
            _animator = null;
            CurrentFrame = null;
            LoadError = exception.Message;
        }
    }

    public void StartAnimation()
    {
        ThrowIfDisposed();
        _isAnimationRunning = true;
        _animator?.Start();
        RestartWaitingTimer();
    }

    public void StopAnimation()
    {
        _isAnimationRunning = false;
        _animator?.Stop();
        _waitingTimer.Stop();
    }

    public void PointerEntered()
    {
        RegisterUserInteraction();
        _stateMachine.PointerEntered();
    }

    public void PointerExited()
    {
        RegisterUserInteraction();
        _stateMachine.PointerExited();
    }

    public void PointerPressed()
    {
        RegisterUserInteraction();
        _stateMachine.PointerPressed();
    }

    public void DismissBubble()
    {
        _bubbleTimer.Stop();
        IsBubbleVisible = false;
    }

    public void DragDirectionChanged(PetInteraction dragInteraction)
    {
        RegisterUserInteraction();
        DismissBubble();
        _stateMachine.DragDirectionChanged(dragInteraction);
    }

    public void PointerReleased(bool isHovering)
    {
        RegisterUserInteraction();
        _stateMachine.PointerReleased(isHovering);
    }

    public void ToggleBubble()
    {
        RegisterUserInteraction();
        if (IsBubbleVisible)
        {
            DismissBubble();
            return;
        }

        BubbleText = "Ready.";
        IsBubbleVisible = true;
        _bubbleTimer.Stop();
        _bubbleTimer.Start();
    }

    private static PetPackage ResolvePackage(PetSettings settings)
    {
        var configuredPath = settings.SpriteSheetPath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return LoadPackage(Path.Combine(AppContext.BaseDirectory, "Assets", "pets", "yorha-sit-2b"));
        }

        var fullPath = Path.GetFullPath(configuredPath);
        if (Directory.Exists(fullPath))
        {
            return LoadPackage(fullPath);
        }

        return new PetPackage(fullPath, null);
    }

    private static PetPackage LoadPackage(string packageDirectory)
    {
        var manifestPath = Path.Combine(packageDirectory, "pet.json");
        if (!File.Exists(manifestPath))
        {
            return new PetPackage(Path.Combine(packageDirectory, "spritesheet.webp"), null);
        }

        var manifest = JsonSerializer.Deserialize<PetPackageManifest>(
            File.ReadAllText(manifestPath),
            JsonOptions);

        var spriteSheetPath = manifest?.SpritesheetPath;
        if (string.IsNullOrWhiteSpace(spriteSheetPath))
        {
            spriteSheetPath = "spritesheet.webp";
        }

        return new PetPackage(Path.GetFullPath(Path.Combine(packageDirectory, spriteSheetPath)), manifest?.Grid);
    }

    private void OnFrameChanged(ImageSource frame)
    {
        CurrentFrame = frame;
    }

    private void OnInteractionChanged(PetInteraction interaction)
    {
        _animator?.SetRow(PetLayout.GetRowId(interaction));
    }

    private void OnWaitingTimerTick(object? sender, EventArgs e)
    {
        _waitingTimer.Stop();
        _stateMachine.WaitingElapsed();
    }

    private void OnBubbleTimerTick(object? sender, EventArgs e)
    {
        DismissBubble();
    }

    private void RegisterUserInteraction()
    {
        RestartWaitingTimer();
    }

    private void RestartWaitingTimer()
    {
        if (!_isAnimationRunning)
        {
            return;
        }

        _waitingTimer.Stop();
        _waitingTimer.Start();
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stateMachine.InteractionChanged -= OnInteractionChanged;
        _waitingTimer.Stop();
        _waitingTimer.Tick -= OnWaitingTimerTick;
        _bubbleTimer.Stop();
        _bubbleTimer.Tick -= OnBubbleTimerTick;
        if (_animator is not null)
        {
            _animator.FrameChanged -= OnFrameChanged;
            _animator.Dispose();
            _animator = null;
        }
    }

    private sealed record PetPackage(string SpriteSheetPath, GridConfig? Grid);

    private sealed record PetPackageManifest
    {
        public string? SpritesheetPath { get; init; }

        public GridConfig? Grid { get; init; }
    }
}
