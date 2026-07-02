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
    private static readonly TimeSpan AmbientPlayMin = TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan AmbientPlayVariance = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan AmbientRestMin = TimeSpan.FromMilliseconds(9000);
    private static readonly TimeSpan AmbientRestVariance = TimeSpan.FromMilliseconds(9000);
    private static readonly TimeSpan AmbientInitialDelayMin = TimeSpan.FromMilliseconds(4000);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<PetViewModel>? _logger;
    private readonly PetStateMachine _stateMachine = new();
    private readonly DispatcherTimer _waitingTimer;
    private readonly DispatcherTimer _bubbleTimer;
    private readonly DispatcherTimer _ambientTimer;
    private SpriteAnimator? _animator;
    private SpriteSheet? _spriteSheet;
    private ImageSource? _currentFrame;
    private BitmapScalingMode _bitmapScalingMode = BitmapScalingMode.HighQuality;
    private string? _loadError;
    private string _bubbleText = "Ready.";
    private bool _isBubbleVisible;
    private bool _isAnimationRunning;
    private bool _disposed;
    private AmbientPhase _ambientPhase;
    private string? _ambientRowId;
    private string? _lastAmbientRowId;

    public PetViewModel(ILogger<PetViewModel>? logger = null)
    {
        _logger = logger;
        _stateMachine.InteractionChanged += OnInteractionChanged;
        _waitingTimer = new DispatcherTimer { Interval = WaitingAfter };
        _waitingTimer.Tick += OnWaitingTimerTick;
        _bubbleTimer = new DispatcherTimer { Interval = BubbleVisibleFor };
        _bubbleTimer.Tick += OnBubbleTimerTick;
        _ambientTimer = new DispatcherTimer();
        _ambientTimer.Tick += OnAmbientTimerTick;
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
            _spriteSheet = sheet;
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
            _spriteSheet = null;
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
        StartAmbientScheduler(initial: true);
    }

    public void StopAnimation()
    {
        _isAnimationRunning = false;
        _animator?.Stop();
        _waitingTimer.Stop();
        CancelAmbient(resetToBaseRow: false);
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
        CancelAmbient(resetToBaseRow: false);
        SetAnimationRow(PetLayout.GetRowId(interaction));
        if (interaction == PetInteraction.Idle)
        {
            StartAmbientScheduler(initial: true);
        }
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
        CancelAmbient(resetToBaseRow: true);
        RestartWaitingTimer();
        if (_stateMachine.Current == PetInteraction.Idle)
        {
            StartAmbientScheduler(initial: true);
        }
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

    private void OnAmbientTimerTick(object? sender, EventArgs e)
    {
        _ambientTimer.Stop();
        if (!_isAnimationRunning || _stateMachine.Current != PetInteraction.Idle)
        {
            CancelAmbient(resetToBaseRow: false);
            return;
        }

        if (_ambientPhase == AmbientPhase.WaitingToPlay)
        {
            StartAmbientPlay();
            return;
        }

        if (_ambientPhase == AmbientPhase.Playing)
        {
            EndAmbientPlay();
        }
    }

    private void StartAmbientScheduler(bool initial)
    {
        if (!_isAnimationRunning || _stateMachine.Current != PetInteraction.Idle || _animator is null)
        {
            return;
        }

        _ambientPhase = AmbientPhase.WaitingToPlay;
        _ambientTimer.Stop();
        _ambientTimer.Interval = initial
            ? RandomDelay(AmbientInitialDelayMin, AmbientRestVariance)
            : RandomDelay(AmbientRestMin, AmbientRestVariance);
        _ambientTimer.Start();
    }

    private void StartAmbientPlay()
    {
        var rowId = PickAmbientRowId();
        if (rowId is null)
        {
            StartAmbientScheduler(initial: false);
            return;
        }

        _ambientRowId = rowId;
        _lastAmbientRowId = rowId;
        _ambientPhase = AmbientPhase.Playing;
        SetAnimationRow(rowId);
        _ambientTimer.Interval = RandomDelay(AmbientPlayMin, AmbientPlayVariance);
        _ambientTimer.Start();
    }

    private void EndAmbientPlay()
    {
        _ambientRowId = null;
        SetAnimationRow(PetLayout.GetRowId(_stateMachine.Current));
        StartAmbientScheduler(initial: false);
    }

    private void CancelAmbient(bool resetToBaseRow)
    {
        _ambientTimer.Stop();
        _ambientPhase = AmbientPhase.None;
        var hadAmbientRow = _ambientRowId is not null;
        _ambientRowId = null;

        if (hadAmbientRow && resetToBaseRow)
        {
            SetAnimationRow(PetLayout.GetRowId(_stateMachine.Current));
        }
    }

    private string? PickAmbientRowId()
    {
        var sheet = _spriteSheet;
        if (sheet is null)
        {
            return null;
        }

        var candidates = PetLayout.AmbientRowIds
            .Where(sheet.HasRow)
            .Where(rowId => !string.Equals(rowId, _lastAmbientRowId, StringComparison.Ordinal))
            .ToArray();

        if (candidates.Length == 0)
        {
            candidates = PetLayout.AmbientRowIds.Where(sheet.HasRow).ToArray();
        }

        return candidates.Length == 0 ? null : candidates[Random.Shared.Next(candidates.Length)];
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

    private static TimeSpan RandomDelay(TimeSpan minimum, TimeSpan variance)
    {
        return minimum + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * variance.TotalMilliseconds);
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
        _ambientTimer.Stop();
        _ambientTimer.Tick -= OnAmbientTimerTick;
        if (_animator is not null)
        {
            _animator.FrameChanged -= OnFrameChanged;
            _animator.Dispose();
            _animator = null;
        }
    }

    private enum AmbientPhase
    {
        None,
        WaitingToPlay,
        Playing,
    }

    private sealed record PetPackage(string SpriteSheetPath, GridConfig? Grid);

    private sealed record PetPackageManifest
    {
        public string? SpritesheetPath { get; init; }

        public GridConfig? Grid { get; init; }
    }
}
