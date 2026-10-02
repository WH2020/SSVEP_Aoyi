using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Ssvep.Studio.App;

public sealed record StudioScreenSnapshot(Rect WorkAreaPixels, Rect WindowPixels, double DpiScale);
public sealed record StudioWindowBounds(Rect Pixels, Size Dips, Size MinimumDips);

public static class StudioWindowPlacement
{
    // WPF dimensions are DIPs; monitor work areas and SetWindowPos use screen coordinates.
    // Keep the complete outer window (including its title bar) inside the taskbar-free work area.
    public static StudioWindowBounds Calculate(Rect workArea, double dpiScale, Size preferred, Point? position = null)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0 || !double.IsFinite(dpiScale) || dpiScale <= 0)
            throw new ArgumentException("屏幕尺寸或缩放比例无效。");
        double margin = Math.Min(16 * dpiScale, Math.Min(workArea.Width, workArea.Height) / 20);
        double width = Math.Max(1, Math.Floor(Math.Min(preferred.Width * dpiScale, workArea.Width - 2 * margin)));
        double height = Math.Max(1, Math.Floor(Math.Min(preferred.Height * dpiScale, workArea.Height - 2 * margin)));
        double left = position?.X ?? workArea.Left + (workArea.Width - width) / 2;
        double top = position?.Y ?? workArea.Top + (workArea.Height - height) / 2;
        left = Math.Round(Math.Clamp(left, workArea.Left + margin, workArea.Right - margin - width));
        top = Math.Round(Math.Clamp(top, workArea.Top + margin, workArea.Bottom - margin - height));
        var size = new Size(width / dpiScale, height / dpiScale);
        return new(new Rect(left, top, width, height), size,
            new Size(Math.Min(1000, size.Width), Math.Min(680, size.Height)));
    }

    public static void Attach(Window window)
    {
        bool fitting = false;
        void Fit(bool center)
        {
            if (fitting || new WindowInteropHelper(window).Handle == IntPtr.Zero) return;
            fitting = true;
            try
            {
                var screen = ReadScreen(window);
                var fitted = Calculate(screen.WorkAreaPixels, screen.DpiScale,
                    new Size(window.Width, window.Height), center ? null : screen.WindowPixels.TopLeft);
                window.MinWidth = fitted.MinimumDips.Width;
                window.MinHeight = fitted.MinimumDips.Height;
                if (window.WindowState != WindowState.Normal) return;
                window.Width = fitted.Dips.Width;
                window.Height = fitted.Dips.Height;
                var bounds = fitted.Pixels;
                if (!SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero,
                    (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, 0x0014)) // NOZORDER | NOACTIVATE
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { fitting = false; }
        }
        window.SourceInitialized += (_, _) => Fit(center: true);
        window.Loaded += (_, _) => Fit(center: true);
        window.DpiChanged += (_, _) =>
        {
            if (fitting || !window.IsLoaded) return;
            _ = window.Dispatcher.BeginInvoke(() => { if (window.IsLoaded) Fit(center: false); }, DispatcherPriority.Loaded);
        };
    }

    public static StudioScreenSnapshot ReadScreen(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) throw new InvalidOperationException("窗口句柄尚未创建。");
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info) || !GetWindowRect(handle, out var rectangle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        uint dpi = GetDpiForWindow(handle);
        return new(info.Work.ToRect(), rectangle.ToRect(), (dpi == 0 ? 96 : dpi) / 96.0);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
