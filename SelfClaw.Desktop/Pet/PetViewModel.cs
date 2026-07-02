using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace SelfClaw.Desktop.Pet;

public sealed class PetViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<PetViewModel>? _logger;
    private SpriteAnimator? _animator;
    private ImageSource? _currentFrame;
    private BitmapScalingMode _bitmapScalingMode = BitmapScalingMode.HighQuality;
    private string? _loadError;
    private bool _disposed;

    public PetViewModel(ILogger<PetViewModel>? logger = null)
    {
        _logger = logger;
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
        _animator?.Start();
    }

    public void StopAnimation()
    {
        _animator?.Stop();
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
