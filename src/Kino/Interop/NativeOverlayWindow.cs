using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Kino.Services;
using static Kino.Interop.NativeMethods;

namespace Kino.Interop
{
    // This raw HWND owns the taskbar's DirectComposition surface. WPF owns the flyout separately.
    internal sealed class NativeOverlayWindow : IDisposable
    {
        private static readonly WindowProcedure Procedure = WindowProc;
        private static readonly ConcurrentDictionary<IntPtr, NativeOverlayWindow> Windows = new ConcurrentDictionary<IntPtr, NativeOverlayWindow>();
        private readonly HwndSourceHook _hook;
        public IntPtr Handle { get; private set; }
        public NativeOverlayWindow(int width, int height, HwndSourceHook hook)
        {
            _hook = hook;
            var instance = GetModuleHandle(null);
            var windowClass = new WindowClass { ClassName = "Kino.NativeTaskbar", Procedure = Procedure, Instance = instance, Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)) };
            if (RegisterClass(ref windowClass) == 0 && Marshal.GetLastWin32Error() != 1410) throw new Win32Exception(Marshal.GetLastWin32Error());
            Handle = CreateWindowEx(WsExNoActivate | WsExToolWindow | WsExNoRedirection | 8,
                windowClass.ClassName, "KINO.Taskbar", WsPopup, -10000, -10000, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Windows[Handle] = this;
        }
        private static IntPtr WindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (Windows.TryGetValue(window, out var owner))
                {
                    var handled = false;
                    var result = owner._hook(window, message, wParam, lParam, ref handled);
                    if (message == 0x0082) Windows.TryRemove(window, out _);
                    if (handled) return result;
                }
            }
            catch (Exception ex) { Log.Write("Native window message: " + ex.GetType().Name); }
            return DefWindowProc(window, message, wParam, lParam);
        }
        public void Dispose()
        {
            var handle = Handle; Handle = IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            Windows.TryRemove(handle, out _);
            if (IsWindow(handle)) DestroyWindow(handle);
        }
    }
}
