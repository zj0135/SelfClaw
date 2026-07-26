using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SelfClaw.Desktop.Pet;

/// <summary>
/// Runtime WebP decoder backed by libwebp. It does not rely on system WIC WebP support.
/// </summary>
internal sealed class WebpSpriteLoader : IPetSpriteDecoder
{
    private const string NativeLibraryName = "libwebp";
    private const int MaxDimension = 8192;
    private const int MaxPixels = 32_000_000;

    static WebpSpriteLoader()
    {
        NativeLibrary.SetDllImportResolver(typeof(WebpSpriteLoader).Assembly, ResolveNativeLibrary);
    }

    public BitmapSource Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Pet spritesheet path is empty.", nameof(path));
        }

        var data = File.ReadAllBytes(path);
        if (data.Length == 0)
        {
            throw new InvalidOperationException("Pet spritesheet file is empty.");
        }

        try
        {
            return Decode(data);
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            throw new InvalidOperationException(
                "Unable to load libwebp.dll. Place it under SelfClaw.Desktop/runtimes/win-x64/native/libwebp.dll and rebuild.",
                exception);
        }
    }

    private static BitmapSource Decode(byte[] data)
    {
        var size = new UIntPtr((ulong)data.Length);
        if (WebPGetInfo(data, size, out var infoWidth, out var infoHeight) == 0)
        {
            throw new InvalidOperationException("Pet spritesheet is not a valid WebP image.");
        }

        ValidateDimensions(infoWidth, infoHeight);

        var decoded = WebPDecodeBGRA(data, size, out var width, out var height);
        if (decoded == IntPtr.Zero)
        {
            throw new InvalidOperationException("libwebp failed to decode the pet spritesheet.");
        }

        try
        {
            ValidateDimensions(width, height);
            var stride = checked(width * 4);
            var bufferSize = checked(stride * height);
            var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), decoded, bufferSize, stride);
            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            return bitmap;
        }
        finally
        {
            WebPFree(decoded);
        }
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"Pet spritesheet has invalid dimensions {width}x{height}.");
        }

        if (width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
        {
            throw new InvalidOperationException($"Pet spritesheet dimensions {width}x{height} exceed the safe limit.");
        }
    }

    private static IntPtr ResolveNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, NativeLibraryName, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in EnumerateNativeCandidates())
        {
            if (File.Exists(candidate))
            {
                return NativeLibrary.Load(candidate);
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> EnumerateNativeCandidates()
    {
        var architectureRid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => null,
        };

        if (!string.IsNullOrWhiteSpace(architectureRid))
        {
            yield return Path.Combine(AppContext.BaseDirectory, "runtimes", architectureRid, "native", "libwebp.dll");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "libwebp.dll");
    }

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int WebPGetInfo(
        [In] byte[] data,
        UIntPtr dataSize,
        out int width,
        out int height);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr WebPDecodeBGRA(
        [In] byte[] data,
        UIntPtr dataSize,
        out int width,
        out int height);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void WebPFree(IntPtr ptr);
}
