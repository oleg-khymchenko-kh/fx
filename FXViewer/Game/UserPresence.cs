using System.Runtime.InteropServices;

namespace FXViewer.Game;

public static class UserPresence
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInput info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public static double Seconds() => Environment.TickCount64 / 1000.0;

    public static double IdleSeconds()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref info)) return 0;
        unchecked
        {
            int passed = Environment.TickCount - (int)info.Time;
            return passed <= 0 ? 0 : passed / 1000.0;
        }
    }

    public static bool AppInFront()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        return GetWindowThreadProcessId(window, out uint processId) != 0
            && processId == (uint)Environment.ProcessId;
    }
}
