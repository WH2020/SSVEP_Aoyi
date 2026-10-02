using System.Runtime.InteropServices;

namespace Ssvep.Speller.App;

internal static class DisplayRefreshRateProvider
{
    private const int VerticalRefresh = 116;

    public static double GetPrimaryRefreshRateHz()
    {
        IntPtr deviceContext = GetDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero)
        {
            return 60;
        }

        try
        {
            int refreshRate = GetDeviceCaps(deviceContext, VerticalRefresh);
            return refreshRate > 1 ? refreshRate : 60;
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, deviceContext);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr deviceContext, int index);
}
