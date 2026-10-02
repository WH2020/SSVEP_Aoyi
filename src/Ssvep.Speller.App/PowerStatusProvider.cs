using System.Runtime.InteropServices;

namespace Ssvep.Speller.App;

internal static class PowerStatusProvider
{
    public static bool IsOnAcPower()
    {
        return !GetSystemPowerStatus(out SystemPowerStatus status) || status.AcLineStatus != 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}
