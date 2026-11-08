// The DirectN device setup follows TaskMonitor (Apache-2.0). See licenses/THIRD-PARTY-NOTICES.txt.
using System.Runtime.InteropServices;
using DirectN;
using Kino.Models;

namespace Kino.Interop
{
    internal sealed class TaskbarRenderer : IDisposable
    {
        internal const float HorizontalPadding = 18f;
        internal const float ColumnWidth = 96f;
        private const float LabelValueGap = 6f;
        private readonly Dictionary<MetricKind, float> _labelWidths = new Dictionary<MetricKind, float>();
        private IComObject<ID3D11Device> _d3d;
        private IDXGIDevice _dxgi;
        private IDXGIDevice1 _dxgi1;
        private IComObject<IDXGIFactory2> _factory;
        private IComObject<IDXGISwapChain1> _swap;
        private IComObject<ID2D1Factory1> _d2dFactory;
        private IComObject<ID2D1Device> _d2d;
        private IComObject<ID2D1DeviceContext> _context;
        private IComObject<IDXGISurface> _surface;
        private IComObject<ID2D1Bitmap1> _bitmap;
        private IComObject<IDWriteFactory> _write;
        private IComObject<IDWriteTextFormat> _labelFormat, _valueFormat;
        private IComObject<IDWriteInlineObject> _valueEllipsis;
        private IComObject<ID2D1SolidColorBrush> _text, _label, _accent, _hover, _background;
        private IComObject<IDCompositionDevice> _composition;
        private IDCompositionTarget _target;
        private IDCompositionVisual _visual;
        private readonly float _width, _height;
        private readonly bool _vertical, _opaque;

        public TaskbarRenderer(IntPtr window, int width, int height, uint dpi, bool vertical, bool opaque)
        {
            _width = width * 96f / dpi; _height = height * 96f / dpi;
            _vertical = vertical; _opaque = opaque;
            try { Initialize(window, width, height, dpi); }
            catch { Dispose(); throw; }
        }

        private void Initialize(IntPtr window, int width, int height, uint dpi)
        {
            _d3d = D3D11Functions.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, out _);
            _dxgi = _d3d.As<IDXGIDevice>(); _dxgi1 = _d3d.As<IDXGIDevice1>();
            _factory = DXGIFunctions.CreateDXGIFactory2<IDXGIFactory2>();
            _swap = _factory.Object.CreateSwapChainForComposition<IDXGISwapChain1>(_dxgi1, new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)width, Height = (uint)height, Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, BufferUsage = Constants.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2, Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL, AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED
            });
            _d2dFactory = D2D1Functions.D2D1CreateFactory1(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED);
            _d2d = _d2dFactory.Object.CreateDevice<ID2D1Device>(_dxgi);
            _context = _d2d.CreateDeviceContext<ID2D1DeviceContext>(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE);
            _context.Object.SetDpi(dpi, dpi);
            _surface = _swap.GetBuffer<IDXGISurface>(0);
            _bitmap = _context.CreateBitmapFromDxgiSurface<ID2D1Bitmap1>(_surface, new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX = dpi, dpiY = dpi, bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW
            });
            _context.SetTarget(_bitmap);
            _text = Brush(1, 1, 1, 1); _label = Brush(1, 1, 1, .65f);
            _accent = Brush(.22f, .76f, .65f, 1); _hover = Brush(1, 1, 1, .08f);
            _background = Brush(.12f, .12f, .12f, 1);
            _write = DWriteFunctions.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_ISOLATED);
            _labelFormat = _write.CreateTextFormat("Microsoft YaHei UI", 11f);
            _valueFormat = _write.CreateTextFormat("Segoe UI", 12.5f);
            foreach (var format in new[] { _labelFormat, _valueFormat })
            {
                format.Object.SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);
                format.Object.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            }
            _valueFormat.Object.SetTextAlignment(_vertical
                ? DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING
                : DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING);
            _write.Object.CreateEllipsisTrimmingSign(_valueFormat.Object, out var ellipsis).ThrowOnError();
            _valueEllipsis = new ComObject<IDWriteInlineObject>(ellipsis);
            var trimming = new DWRITE_TRIMMING { granularity = DWRITE_TRIMMING_GRANULARITY.DWRITE_TRIMMING_GRANULARITY_CHARACTER };
            _valueFormat.Object.SetTrimming(ref trimming, _valueEllipsis.Object).ThrowOnError();
            foreach (var metric in Metrics.All)
            {
                var label = Metrics.ShortName(metric);
                using (var layout = _write.CreateTextLayout(_labelFormat, label, label.Length, ColumnWidth, 20f))
                {
                    layout.Object.GetMetrics(out var measured).ThrowOnError();
                    _labelWidths[metric] = (float)Math.Ceiling(measured.widthIncludingTrailingWhitespace);
                }
            }
            Functions.DCompositionCreateDevice(_dxgi, typeof(IDCompositionDevice).GUID, out var pointer).ThrowOnError();
            try { _composition = new ComObject<IDCompositionDevice>((IDCompositionDevice)Marshal.GetTypedObjectForIUnknown(pointer, typeof(IDCompositionDevice))); }
            finally { Marshal.Release(pointer); }
            _composition.Object.CreateTargetForHwnd(window, true, out _target).ThrowOnError();
            _composition.Object.CreateVisual(out _visual).ThrowOnError();
            _visual.SetContent(_swap.Object).ThrowOnError(); _target.SetRoot(_visual).ThrowOnError();
            _composition.Object.Commit().ThrowOnError();
        }

        private IComObject<ID2D1SolidColorBrush> Brush(float r, float g, float b, float a)
        {
            _context.Object.CreateSolidColorBrush(new _D3DCOLORVALUE { r = r, g = g, b = b, a = a }, IntPtr.Zero, out var brush).ThrowOnError();
            return new ComObject<ID2D1SolidColorBrush>(brush);
        }

        public void Draw(AppSettings settings, UsageSnapshot snapshot, bool dark, bool hovered)
        {
            var foreground = dark ? 1f : .08f;
            _text.Object.SetColor(new _D3DCOLORVALUE { r = foreground, g = foreground, b = foreground, a = 1 });
            _label.Object.SetColor(new _D3DCOLORVALUE { r = foreground, g = foreground, b = foreground, a = .63f });
            _hover.Object.SetColor(new _D3DCOLORVALUE { r = foreground, g = foreground, b = foreground, a = .07f });
            _background.Object.SetColor(new _D3DCOLORVALUE { r = dark ? .12f : .94f, g = dark ? .12f : .94f, b = dark ? .12f : .94f, a = 1 });
            _context.BeginDraw(); _context.Clear();
            if (_opaque) _context.FillRectangle(new D2D_RECT_F(0, 0, _width, _height), _background);
            if (hovered) _context.FillRoundedRectangle(new D2D1_ROUNDED_RECT { rect = new D2D_RECT_F(0, 2, _width, _height - 2), radiusX = 5, radiusY = 5 }, _hover);
            var metrics = settings.TaskbarMetrics;
            if (_vertical)
            {
                for (var i = 0; i < metrics.Count; i++)
                {
                    var y = 4 + i * 40f;
                    _context.DrawText(Metrics.ShortName(metrics[i]), _labelFormat, new D2D_RECT_F(4, y, _width - 4, y + 17), _label);
                    _context.DrawText(Metrics.Value(metrics[i], snapshot, settings.HideAmounts), _valueFormat, new D2D_RECT_F(4, y + 17, _width - 4, y + 36), _text);
                }
            }
            else
            {
                var rowHeight = Math.Min(20f, (_height - 4) / 2f);
                var top = (_height - rowHeight * 2) / 2;
                _context.FillRoundedRectangle(new D2D1_ROUNDED_RECT { rect = new D2D_RECT_F(5, top + 3, 8, _height - top - 3), radiusX = 1.5f, radiusY = 1.5f }, _accent);
                for (var i = 0; i < metrics.Count; i++)
                {
                    var x = 17 + (i / 2) * ColumnWidth;
                    var y = top + (i % 2) * rowHeight;
                    var labelWidth = _labelWidths[metrics[i]];
                    var valueX = x + labelWidth + LabelValueGap;
                    _context.DrawText(Metrics.ShortName(metrics[i]), _labelFormat, new D2D_RECT_F(x, y, x + labelWidth, y + rowHeight), _label);
                    _context.DrawText(Metrics.Value(metrics[i], snapshot, settings.HideAmounts), _valueFormat, new D2D_RECT_F(valueX, y, x + ColumnWidth - 10, y + rowHeight), _text);
                }
            }
            _context.Object.EndDraw(IntPtr.Zero, IntPtr.Zero).ThrowOnError();
            _swap.Object.Present(0, 0).ThrowOnError();
        }

        public void Dispose()
        {
            try { _visual?.SetContent(null); _target?.SetRoot(null); _composition?.Object.Commit(); } catch { }
            Release(_target); Release(_visual); _target = null; _visual = null;
            _composition?.Dispose(); _composition = null;
            try { _context?.SetTarget(null); } catch { }
            _bitmap?.Dispose(); _surface?.Dispose();
            _text?.Dispose(); _label?.Dispose(); _accent?.Dispose(); _hover?.Dispose(); _background?.Dispose();
            _labelFormat?.Dispose(); _valueFormat?.Dispose(); _valueEllipsis?.Dispose(); _write?.Dispose();
            _context?.Dispose(); _swap?.Dispose(); _d2d?.Dispose(); _d2dFactory?.Dispose(); _factory?.Dispose();
            // As<T>() returns an owned QI RCW for these interfaces.
            Release(_dxgi1); Release(_dxgi); _dxgi1 = null; _dxgi = null;
            _d3d?.Dispose();
        }
        private static void Release(object value) { try { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); } catch { } }
    }
}
