using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FormsApplication = System.Windows.Forms.Application;
using FormsAutoScaleMode = System.Windows.Forms.AutoScaleMode;
using FormsCursors = System.Windows.Forms.Cursors;
using FormsDialogResult = System.Windows.Forms.DialogResult;
using FormsForm = System.Windows.Forms.Form;
using FormsFormBorderStyle = System.Windows.Forms.FormBorderStyle;
using FormsFormStartPosition = System.Windows.Forms.FormStartPosition;
using FormsKeys = System.Windows.Forms.Keys;
using FormsMouseButtons = System.Windows.Forms.MouseButtons;
using FormsMouseEventArgs = System.Windows.Forms.MouseEventArgs;
using FormsPaintEventArgs = System.Windows.Forms.PaintEventArgs;
using FormsScreen = System.Windows.Forms.Screen;

namespace SelfClaw.Desktop.Services;

public sealed record ScreenshotCaptureResult(
    string FilePath,
    string FileName,
    string MediaType,
    long ByteLength);

public static class ScreenshotCaptureService
{
    private const uint GetWindowHwndNext = 2;
    private const int DwmWindowAttributeExtendedFrameBounds = 9;

    public static ScreenshotCaptureResult? Capture()
    {
        var bounds = GetVirtualScreenBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        using var overlay = new ScreenshotOverlayForm(bounds);
        return overlay.ShowDialog() == FormsDialogResult.OK
            ? overlay.Result
            : null;
    }

    private static Rectangle GetVirtualScreenBounds()
    {
        var screens = FormsScreen.AllScreens;
        if (screens.Length == 0)
        {
            return new Rectangle(0, 0, 1, 1);
        }

        var left = screens.Min(screen => screen.Bounds.Left);
        var top = screens.Min(screen => screen.Bounds.Top);
        var right = screens.Max(screen => screen.Bounds.Right);
        var bottom = screens.Max(screen => screen.Bounds.Bottom);

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static ScreenshotCaptureResult? CaptureBounds(Rectangle bounds)
    {
        var clippedBounds = Rectangle.Intersect(bounds, GetVirtualScreenBounds());
        if (clippedBounds.Width <= 0 || clippedBounds.Height <= 0)
        {
            return null;
        }

        var directory = Path.Combine(Path.GetTempPath(), "SelfClaw", "Screenshots");
        Directory.CreateDirectory(directory);

        var fileName = $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
        var path = Path.Combine(directory, fileName);

        using var bitmap = new Bitmap(clippedBounds.Width, clippedBounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                clippedBounds.Left,
                clippedBounds.Top,
                0,
                0,
                clippedBounds.Size,
                CopyPixelOperation.SourceCopy);
        }

        bitmap.Save(path, ImageFormat.Png);

        var fileInfo = new FileInfo(path);
        return new ScreenshotCaptureResult(fileInfo.FullName, fileInfo.Name, "image/png", fileInfo.Length);
    }

    private static Rectangle? ResolveWindowBounds(Point screenPoint, IntPtr ignoredHwnd)
    {
        for (var hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GetWindowHwndNext))
        {
            if (hwnd == ignoredHwnd || !IsWindowVisible(hwnd))
            {
                continue;
            }

            if (!TryGetWindowBounds(hwnd, out var bounds) || !bounds.Contains(screenPoint))
            {
                continue;
            }

            var clippedBounds = Rectangle.Intersect(bounds, GetVirtualScreenBounds());
            return clippedBounds.Width > 0 && clippedBounds.Height > 0 ? clippedBounds : null;
        }

        return null;
    }

    private static bool TryGetWindowBounds(IntPtr hwnd, out Rectangle bounds)
    {
        if (DwmGetWindowAttribute(
                hwnd,
                DwmWindowAttributeExtendedFrameBounds,
                out var dwmRect,
                Marshal.SizeOf<NativeRect>()) == 0 &&
            dwmRect.HasArea)
        {
            bounds = dwmRect.ToRectangle();
            return true;
        }

        if (GetWindowRect(hwnd, out var windowRect) && windowRect.HasArea)
        {
            bounds = windowRect.ToRectangle();
            return true;
        }

        bounds = Rectangle.Empty;
        return false;
    }

    private static Rectangle NormalizeRectangle(Point first, Point second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        var right = Math.Max(first.X, second.X);
        var bottom = Math.Max(first.Y, second.Y);

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        out NativeRect pvAttribute,
        int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly bool HasArea => Right > Left && Bottom > Top;

        public readonly Rectangle ToRectangle()
            => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    private sealed class ScreenshotOverlayForm : FormsForm
    {
        private const int DragThresholdPixels = 6;

        private readonly Rectangle _virtualBounds;
        private Point _startScreenPoint;
        private Point _currentScreenPoint;
        private Rectangle? _hoveredWindowBounds;
        private bool _hasDragged;
        private bool _isCompleting;
        private bool _isMouseDown;

        public ScreenshotOverlayForm(Rectangle virtualBounds)
        {
            _virtualBounds = virtualBounds;

            AutoScaleMode = FormsAutoScaleMode.None;
            BackColor = Color.Black;
            Bounds = virtualBounds;
            Cursor = FormsCursors.Cross;
            DoubleBuffered = true;
            FormBorderStyle = FormsFormBorderStyle.None;
            KeyPreview = true;
            Opacity = 0.36;
            ShowInTaskbar = false;
            StartPosition = FormsFormStartPosition.Manual;
            TopMost = true;
        }

        public ScreenshotCaptureResult? Result { get; private set; }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            _currentScreenPoint = System.Windows.Forms.Cursor.Position;
            UpdateHoveredWindowBounds(_currentScreenPoint);
        }

        protected override void OnMouseDown(FormsMouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (_isCompleting)
            {
                return;
            }

            if (e.Button == FormsMouseButtons.Right)
            {
                CancelCapture();
                return;
            }

            if (e.Button != FormsMouseButtons.Left)
            {
                return;
            }

            _isMouseDown = true;
            _hasDragged = false;
            _startScreenPoint = PointToScreen(e.Location);
            _currentScreenPoint = _startScreenPoint;
            UpdateHoveredWindowBounds(_currentScreenPoint);
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(FormsMouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_isCompleting)
            {
                return;
            }

            _currentScreenPoint = PointToScreen(e.Location);

            if (!_isMouseDown)
            {
                UpdateHoveredWindowBounds(_currentScreenPoint);
                return;
            }

            _hasDragged =
                Math.Abs(_currentScreenPoint.X - _startScreenPoint.X) >= DragThresholdPixels ||
                Math.Abs(_currentScreenPoint.Y - _startScreenPoint.Y) >= DragThresholdPixels;

            if (_hasDragged)
            {
                if (_hoveredWindowBounds is not null)
                {
                    _hoveredWindowBounds = null;
                }
            }
            else
            {
                UpdateHoveredWindowBounds(_currentScreenPoint);
            }

            Invalidate();
        }

        protected override void OnMouseUp(FormsMouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (!_isMouseDown || _isCompleting || e.Button != FormsMouseButtons.Left)
            {
                return;
            }

            _isMouseDown = false;
            Capture = false;
            _currentScreenPoint = PointToScreen(e.Location);

            CompleteCapture();
        }

        protected override void OnPaint(FormsPaintEventArgs e)
        {
            base.OnPaint(e);

            if (_isMouseDown && _hasDragged)
            {
                var selectionBounds = NormalizeRectangle(_startScreenPoint, _currentScreenPoint);
                if (selectionBounds.Width <= 0 || selectionBounds.Height <= 0)
                {
                    return;
                }

                var clientBounds = ToClientBounds(selectionBounds);
                using var fill = new SolidBrush(Color.FromArgb(72, 118, 181, 255));
                using var border = new Pen(Color.White, 2f);
                e.Graphics.FillRectangle(fill, clientBounds);
                e.Graphics.DrawRectangle(border, clientBounds);
                return;
            }

            if (_hoveredWindowBounds is not Rectangle hoveredBounds || hoveredBounds.Width <= 0 || hoveredBounds.Height <= 0)
            {
                return;
            }

            var hoveredClientBounds = ToClientBounds(hoveredBounds);
            using var hoverFill = new SolidBrush(Color.FromArgb(48, 118, 181, 255));
            using var accentBorder = new Pen(Color.FromArgb(220, 118, 181, 255), 3f);
            using var hoverBorder = new Pen(Color.White, 1.5f);
            e.Graphics.FillRectangle(hoverFill, hoveredClientBounds);
            e.Graphics.DrawRectangle(accentBorder, hoveredClientBounds);
            e.Graphics.DrawRectangle(hoverBorder, hoveredClientBounds);
        }

        protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, FormsKeys keyData)
        {
            if (keyData == FormsKeys.Escape)
            {
                CancelCapture();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CompleteCapture()
        {
            _isCompleting = true;

            var selectionBounds = NormalizeRectangle(_startScreenPoint, _currentScreenPoint);
            var shouldUseSelection =
                _hasDragged &&
                selectionBounds.Width >= DragThresholdPixels &&
                selectionBounds.Height >= DragThresholdPixels;

            var captureBounds = shouldUseSelection
                ? selectionBounds
                : _hoveredWindowBounds ?? ResolveWindowBounds(_currentScreenPoint, Handle);

            // Keep the modal dialog alive until the capture result is ready.
            Hide();
            FormsApplication.DoEvents();
            Thread.Sleep(80);
            FormsApplication.DoEvents();

            try
            {
                if (captureBounds is Rectangle bounds)
                {
                    Result = CaptureBounds(bounds);
                }
            }
            catch
            {
                Result = null;
            }

            DialogResult = Result is null ? FormsDialogResult.Cancel : FormsDialogResult.OK;
            Close();
        }

        private void CancelCapture()
        {
            DialogResult = FormsDialogResult.Cancel;
            Close();
        }

        private void UpdateHoveredWindowBounds(Point screenPoint)
        {
            var nextBounds = ResolveWindowBounds(screenPoint, Handle);
            if (_hoveredWindowBounds == nextBounds)
            {
                return;
            }

            _hoveredWindowBounds = nextBounds;
            Invalidate();
        }

        private Rectangle ToClientBounds(Rectangle screenBounds)
            => new(
                screenBounds.Left - _virtualBounds.Left,
                screenBounds.Top - _virtualBounds.Top,
                screenBounds.Width,
                screenBounds.Height);
    }
}
