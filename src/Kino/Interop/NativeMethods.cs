using System.Runtime.InteropServices;
using System.Text;

namespace Kino.Interop
{
    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] public struct TrackMouse { public uint Size, Flags; public IntPtr Window; public uint HoverTime; }
        public const int GwlStyle = -16, GwlExStyle = -20;
        public const int WsChild = 0x40000000, WsPopup = unchecked((int)0x80000000);
        public const int WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExNoRedirection = 0x00200000;
        public const uint SwpNoActivate = 0x10, SwpShowWindow = 0x40, SwpNoZOrder = 4;
        public const int WmPaint = 0x000F, WmMouseActivate = 0x0021, WmLeftButtonUp = 0x0202,
            WmRightButtonUp = 0x0205, WmMouseMove = 0x0200, WmMouseLeave = 0x02A3, WmEraseBackground = 0x0014;
        public delegate IntPtr WindowProcedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WindowClass
        {
            public uint Style; public WindowProcedure Procedure; public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background; public string MenuName, ClassName;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClass(ref WindowClass windowClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DefWindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string name);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr window, ref Point point);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int show);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TrackMouse track);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public static uint Dpi(IntPtr window)
        {
            try { var dpi = GetDpiForWindow(window); return dpi == 0 ? 96u : dpi; }
            catch (EntryPointNotFoundException) { return 96; }
        }
        public static IntPtr ClassChild(IntPtr parent, string className)
        {
            for (var window = GetWindow(parent, 5); window != IntPtr.Zero; window = GetWindow(window, 2))
            {
                var name = new StringBuilder(256);
                GetClassName(window, name, name.Capacity);
                if (name.ToString() == className) return window;
            }
            return IntPtr.Zero;
        }
        public static MonitorInfo MonitorAt(Rect anchor)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfo(MonitorFromRect(ref anchor, 2), ref info);
            return info;
        }
    }
}
