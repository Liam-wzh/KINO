using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Kino.Models;
using Kino.Services;
using static Kino.Interop.NativeMethods;

namespace Kino.Interop
{
    internal sealed class TaskbarHost : IDisposable
    {
        private readonly Dispatcher _ui;
        private Thread _thread;
        private Dispatcher _dispatcher;
        private DispatcherTimer _timer;
        private NativeOverlayWindow _source;
        private TaskbarRenderer _renderer;
        private IntPtr _taskbar, _parent, _taskList;
        private Rect _restoreTaskList;
        private int _width, _height;
        private uint _dpi;
        private bool _vertical, _hovered, _dark = true, _stopping, _lastAvailable;
        private volatile AppSettings _settings;
        private volatile UsageSnapshot _snapshot;
        private DateTime _lastError;
        public event Action<Rect> Clicked;
        public event Action<Rect> ContextRequested;
        public event Action<bool> AvailabilityChanged;

        public TaskbarHost(AppSettings settings) { _settings = settings.Clone(); _ui = Dispatcher.CurrentDispatcher; }
        public void Start()
        {
            _thread = new Thread(Run) { Name = "KINO taskbar", IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
        }
        public void Publish(AppSettings settings, UsageSnapshot snapshot)
        {
            _settings = settings.Clone(); _snapshot = snapshot;
            var dispatcher = _dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(new Action(Tick));
        }
        private void Run()
        {
            try
            {
                try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
                _dispatcher = Dispatcher.CurrentDispatcher;
                _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, __) => Tick(), _dispatcher);
                Tick(); Dispatcher.Run();
            }
            catch (Exception ex) { Log.Write("Taskbar thread: " + ex.GetType().Name); }
            finally { _timer?.Stop(); TearDown(); }
        }
        private void Tick()
        {
            if (_stopping) return;
            try
            {
                var taskbar = FindWindow("Shell_TrayWnd", null);
                if (taskbar == IntPtr.Zero) { TearDown(); Available(false); return; }
                if (_source != null && (taskbar != _taskbar || !IsWindow(_source.Handle) || GetAncestor(_source.Handle, 1) != _parent)) TearDown();
                if (!GetWindowRect(taskbar, out var rect) || rect.Width <= 0 || rect.Height <= 0) return;
                var dpi = Dpi(taskbar);
                var vertical = rect.Height > rect.Width;
                var width = vertical ? rect.Width : (int)Math.Ceiling((TaskbarRenderer.HorizontalPadding + Math.Ceiling(_settings.TaskbarMetrics.Count / 2d) * TaskbarRenderer.ColumnWidth) * dpi / 96d);
                var height = vertical ? (int)Math.Ceiling((8 + _settings.TaskbarMetrics.Count * 40) * dpi / 96d) : rect.Height;
                _taskbar = taskbar;
                if (_source == null) CreateSource(width, height);
                if (_source == null) { Available(false); return; }
                if (width != _width || height != _height || dpi != _dpi || vertical != _vertical || _renderer == null)
                {
                    _renderer?.Dispose(); _renderer = null;
                    _width = width; _height = height; _dpi = dpi; _vertical = vertical;
                    _renderer = new TaskbarRenderer(_source.Handle, width, height, dpi, vertical, _parent != _taskbar);
                    Log.Write($"Taskbar renderer ready: {width}x{height}, DPI={dpi}, vertical={vertical}");
                }
                Position(rect);
                ReadTaskbarTheme(); Draw(); Available(_renderer != null);
            }
            catch (Exception ex)
            {
                if ((DateTime.UtcNow - _lastError).TotalSeconds > 30)
                {
                    Log.Write("Taskbar unavailable, will retry: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8")); _lastError = DateTime.UtcNow;
                }
                TearDown(); Available(false);
            }
        }
        private void CreateSource(int width, int height)
        {
            _source = new NativeOverlayWindow(width, height, Hook);
            var rebar = ClassChild(_taskbar, "ReBarWindow32");
            // Win11 retains a hidden classic ReBar for compatibility. Its existence
            // alone does not identify the active taskbar; the XAML bridge takes priority.
            var xaml = ClassChild(_taskbar, "Windows.UI.Composition.DesktopWindowContentBridge");
            if (xaml != IntPtr.Zero) rebar = IntPtr.Zero;
            _parent = rebar == IntPtr.Zero ? _taskbar : rebar;
            // Preserve the native popup style through SetParent, matching TaskMonitor's host.
            SetParent(_source.Handle, _parent);
            // A failed embed must never leave an invisible popup intercepting the desktop.
            if (GetAncestor(_source.Handle, 1) != _parent) { Log.Write("Embed failed: " + Marshal.GetLastWin32Error()); TearDown(); return; }
            if (rebar != IntPtr.Zero)
            {
                _taskList = FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
                if (_taskList != IntPtr.Zero && GetWindowRect(_taskList, out var taskRect)) _restoreTaskList = taskRect;
            }
            Log.Write("Taskbar attached; parent=" + (_parent == _taskbar ? "Shell_TrayWnd" : "ReBarWindow32"));
        }
        private void Position(Rect taskbarRect)
        {
            var tray = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            var trayFound = tray != IntPtr.Zero && GetWindowRect(tray, out _);
            GetWindowRect(tray, out var trayRect);
            var spacing = (int)(4 * _dpi / 96);
            var offset = (int)(_settings.TaskbarOffset * _dpi / 96d);
            var screen = new Point
            {
                X = _vertical ? taskbarRect.Left : Math.Max(taskbarRect.Left, (trayFound ? trayRect.Left : taskbarRect.Right - (int)(160 * _dpi / 96)) - _width - spacing - offset),
                Y = _vertical ? Math.Max(taskbarRect.Top, (trayFound ? trayRect.Top : taskbarRect.Bottom - (int)(100 * _dpi / 96)) - _height - spacing - offset) : taskbarRect.Top
            };
            if (!_vertical && _parent == _taskbar)
            {
                var clusterLeft = int.MaxValue;
                var clusterRight = int.MinValue;
                foreach (var className in new[] { "Start", "ReBarWindow32", "TrayDummySearchControl" })
                {
                    var child = ClassChild(_taskbar, className);
                    if (child == IntPtr.Zero || !GetWindowRect(child, out var bounds) || bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > taskbarRect.Width * .8) continue;
                    clusterLeft = Math.Min(clusterLeft, bounds.Left); clusterRight = Math.Max(clusterRight, bounds.Right);
                }
                if (clusterRight > screen.X && clusterLeft < screen.X + _width)
                {
                    var left = clusterLeft - _width - spacing - offset;
                    var widgetsReserve = (int)(160 * _dpi / 96d);
                    if (left >= taskbarRect.Left + widgetsReserve) screen.X = left;
                }
            }
            ScreenToClient(_parent, ref screen);
            SetWindowPos(_source.Handle, IntPtr.Zero, screen.X, screen.Y, _width, _height, SwpNoActivate | SwpShowWindow);
            if (_taskList != IntPtr.Zero && IsWindow(_taskList) && !_vertical)
            {
                var origin = new Point { X = _restoreTaskList.Left, Y = _restoreTaskList.Top };
                ScreenToClient(_parent, ref origin);
                SetWindowPos(_taskList, IntPtr.Zero, origin.X, origin.Y, Math.Max(0, screen.X - origin.X - spacing), _restoreTaskList.Height, SwpNoActivate | SwpNoZOrder);
            }
        }
        private void ReadTaskbarTheme()
        {
            // Taskbar follows the shell theme, independently of the card's app theme.
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                _dark = Convert.ToInt32(key?.GetValue("SystemUsesLightTheme", 0)) == 0;
        }
        private IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                switch (message)
                {
                    case WmMouseActivate: handled = true; return new IntPtr(3);
                    case WmEraseBackground: handled = true; return new IntPtr(1);
                    case WmLeftButtonUp:
                        if (GetWindowRect(window, out var clickRect)) _ui.BeginInvoke(new Action(() => Clicked?.Invoke(clickRect)));
                        handled = true; break;
                    case WmRightButtonUp:
                        if (GetWindowRect(window, out var menuRect)) _ui.BeginInvoke(new Action(() => ContextRequested?.Invoke(menuRect)));
                        handled = true; break;
                    case WmMouseMove:
                        if (!_hovered)
                        {
                            _hovered = true;
                            var track = new TrackMouse { Size = (uint)Marshal.SizeOf<TrackMouse>(), Flags = 2, Window = window };
                            TrackMouseEvent(ref track); Draw();
                        }
                        break;
                    case WmMouseLeave: _hovered = false; Draw(); break;
                }
            }
            catch (Exception ex) { Log.Write("Taskbar message: " + ex.GetType().Name); }
            return IntPtr.Zero;
        }
        private void Draw()
        {
            try { _renderer?.Draw(_settings, _snapshot, _dark, _hovered); }
            catch (Exception ex) { Log.Write("Taskbar render failure: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8")); _renderer?.Dispose(); _renderer = null; }
        }
        private void Available(bool value)
        {
            if (_lastAvailable == value) return;
            _lastAvailable = value;
            _ui.BeginInvoke(new Action(() => AvailabilityChanged?.Invoke(value)));
        }
        private void TearDown()
        {
            _renderer?.Dispose(); _renderer = null;
            if (_taskList != IntPtr.Zero && IsWindow(_taskList) && IsWindow(_parent))
            {
                var origin = new Point { X = _restoreTaskList.Left, Y = _restoreTaskList.Top };
                ScreenToClient(_parent, ref origin);
                SetWindowPos(_taskList, IntPtr.Zero, origin.X, origin.Y, _restoreTaskList.Width, _restoreTaskList.Height, SwpNoActivate | SwpNoZOrder);
            }
            _taskList = IntPtr.Zero;
            _source?.Dispose(); _source = null; _parent = IntPtr.Zero;
        }
        public void Dispose()
        {
            _stopping = true;
            var dispatcher = _dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(new Action(() => { _timer?.Stop(); TearDown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }));
            _thread?.Join(2500);
        }
    }
}
