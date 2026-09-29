using System.Runtime.InteropServices;

namespace RevitPluginTest.Core
{
    // Makes the overlay window click-through, so mouse input passes to
    // Revit's viewport underneath instead of being captured by the overlay.
    internal static class Win32Interop
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private const int VK_LBUTTON = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        public static void MakeClickThrough(IntPtr hwnd)
        {
            var extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);
        }

        // True only while some window belonging to this process (Revit, since
        // we're loaded in-process) is the foreground window - used to hide a
        // Topmost overlay when the user switches to another application.
        public static bool IsForegroundProcess()
        {
            var hwnd = GetForegroundWindow();

            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            GetWindowThreadProcessId(hwnd, out var processId);

            return processId == (uint)Environment.ProcessId;
        }

        // WPF's Window/Canvas coordinates are device-independent (96 DPI =
        // scale 1.0), but UIView.GetWindowRectangle() returns physical screen
        // pixels. On any display scaled above 100% (125%, 150%, ...), using
        // those numbers directly misaligns the overlay from Revit's real
        // viewport - divide physical-pixel values by this before using them.
        public static double GetDpiScale(IntPtr hwnd)
        {
            return GetDpiForWindow(hwnd) / 96.0;
        }

        // Physical screen pixels, same coordinate space as
        // UIView.GetWindowRectangle() - both are usable directly together
        // without any DPI conversion.
        public static System.Windows.Point GetCursorPosition()
        {
            GetCursorPos(out var point);
            return new System.Windows.Point(point.X, point.Y);
        }

        // Real-time key state (not a queued input event) - polled once per
        // Idling tick by GridTest to detect a fresh press, rather than
        // relying on a mouse event the click-through overlay can't receive.
        public static bool IsLeftButtonDown()
        {
            return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        }
    }
}
