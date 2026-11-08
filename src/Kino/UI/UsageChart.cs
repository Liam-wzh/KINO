using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Kino.Models;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;

namespace Kino.UI
{
    public sealed class UsageChart : UserControl
    {
        private readonly Grid _root = new Grid { Background = Brushes.Transparent };
        private readonly Canvas _overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        private readonly TextBlock _empty = new TextBlock { Text = "暂无消费趋势", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _detail = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, LineHeight = 17 };
        private readonly Border _tooltip;
        private readonly Line _guide = new Line { StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } };
        private readonly Ellipse _marker = new Ellipse { Width = 7, Height = 7, StrokeThickness = 2 };
        private CartesianChart _plot;
        private LineSeries<ObservablePoint> _series;
        private ObservableCollection<ObservablePoint> _values;
        private Axis _xAxis, _yAxis;
        private UsagePoint[] _points = Array.Empty<UsagePoint>();
        private bool _hide, _hourly, _dark;
        private int _hoveredIndex = -1;
        internal CartesianChart Plot => _plot;
        internal string HoverText => _tooltip.Visibility == Visibility.Visible ? _detail.Text : "";
        private bool HasPlotLayout => _plot.CoreChart?.IsLoaded == true &&
            _plot.CoreChart.DrawMarginSize.Width > 0 && _plot.CoreChart.DrawMarginSize.Height > 0;

        public UsageChart()
        {
            Height = 140; Focusable = true; Content = _root;
            _empty.SetResourceReference(TextBlock.ForegroundProperty, "KinoMutedBrush");
            _tooltip = new Border { Child = _detail, Padding = new Thickness(10, 7, 10, 7), Width = 202, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1) };
            _tooltip.SetResourceReference(Border.BackgroundProperty, "KinoPopupBrush");
            _tooltip.SetResourceReference(Border.BorderBrushProperty, "KinoBorderBrush");
            _guide.SetResourceReference(Shape.StrokeProperty, "KinoAccentBrush"); _guide.Opacity = .45;
            _marker.SetResourceReference(Shape.FillProperty, "KinoPopupBrush");
            _marker.SetResourceReference(Shape.StrokeProperty, "KinoAccentBrush");
            _overlay.Children.Add(_guide); _overlay.Children.Add(_marker); _overlay.Children.Add(_tooltip);
            _root.Children.Add(_empty); _root.Children.Add(_overlay);
            CreatePlot(); HideDetail();
            MouseMove += PointerMoved;
            MouseLeave += (_, __) => HideDetail();
            LostKeyboardFocus += (_, __) => HideDetail();
            Unloaded += (_, __) => HideDetail();
            KeyDown += ChartKeyDown;
            System.Windows.Automation.AutomationProperties.SetName(this, "消费趋势，使用左右方向键查看数据点");
        }

        private void CreatePlot()
        {
            if (_plot != null) _root.Children.Remove(_plot);
            _values = new ObservableCollection<ObservablePoint>();
            _series = new LineSeries<ObservablePoint> { Name = "消费", Values = _values, LineSmoothness = .25, GeometrySize = 0 };
            _xAxis = new Axis { TextSize = 10.5, MinStep = 1, ForceStepToMin = true, ShowSeparatorLines = false, Labeler = LabelAt };
            _yAxis = new Axis { LabelsPaint = null, MinLimit = 0, MaxLimit = 1, ShowSeparatorLines = true };
            _plot = new CartesianChart
            {
                Series = new ISeries[] { _series }, XAxes = new[] { _xAxis }, YAxes = new[] { _yAxis },
                DrawMargin = new LiveChartsCore.Measure.Margin(10, 7, 10, 22),
                LegendPosition = LegendPosition.Hidden, TooltipPosition = TooltipPosition.Hidden,
                Background = Brushes.Transparent, Focusable = false,
                AnimationsSpeed = Motion.Enabled ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero,
                EasingFunction = EasingFunctions.CubicOut, UpdaterThrottler = TimeSpan.FromMilliseconds(16)
            };
            _root.Children.Insert(0, _plot); ApplyTheme();
        }

        public void Update(IReadOnlyList<UsagePoint> points, bool hideAmounts, bool resetContext = false)
        {
            var next = (points ?? Array.Empty<UsagePoint>()).Select(p => new UsagePoint { Time = p.Time, Spend = p.Spend, Tokens = p.Tokens, Requests = p.Requests }).ToArray();
            var structureChanged = !_points.Select(p => p.Time).SequenceEqual(next.Select(p => p.Time));
            var reset = resetContext || _hide != hideAmounts || structureChanged;
            HideDetail(); _points = next; _hide = hideAmounts;
            _hourly = next.Length > 1 && next[0].Time.Date == next[next.Length - 1].Time.Date;
            // Replacing the chart also discards cached geometries from a previous account or privacy mode.
            if (reset) CreatePlot();
            if (_dark != ThemeService.IsDark) ApplyTheme();
            _empty.Visibility = next.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _plot.Visibility = next.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _plot.AnimationsSpeed = Motion.Enabled ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero;
            if (next.Length == 0) return;
            var scale = hideAmounts ? Math.Max(.01m, next.Max(p => Math.Abs(p.Spend))) : 1m;
            for (var i = 0; i < next.Length; i++)
            {
                var value = (double)(next[i].Spend / scale);
                if (i < _values.Count) { if (_values[i].Y != value) _values[i].Y = value; }
                else _values.Add(new ObservablePoint(i, value));
            }
            _xAxis.MinLimit = next.Length == 1 ? -.5 : 0;
            _xAxis.MaxLimit = next.Length == 1 ? .5 : next.Length - 1;
            _plot.DrawMargin = new LiveChartsCore.Measure.Margin(_hourly ? 10 : 19, 7, _hourly ? 10 : 19, 22);
            var min = Math.Min(0, _values.Min(p => p.Y ?? 0));
            var max = Math.Max(hideAmounts ? .1 : .01, _values.Max(p => p.Y ?? 0));
            _yAxis.MinLimit = min * 1.15;
            _yAxis.MaxLimit = max * 1.15;
            _yAxis.MinStep = (_yAxis.MaxLimit.Value - _yAxis.MinLimit.Value) / 3;
            _yAxis.ForceStepToMin = true;
            _series.GeometrySize = next.Length == 1 ? 5 : 0;
        }

        private void ApplyTheme()
        {
            _dark = ThemeService.IsDark;
            var accent = ((SolidColorBrush)FindResource("KinoAccentBrush")).Color;
            var muted = ((SolidColorBrush)FindResource("KinoMutedBrush")).Color;
            var grid = ((SolidColorBrush)FindResource("KinoChartGridBrush")).Color;
            var color = new SKColor(accent.R, accent.G, accent.B);
            _series.Stroke = new SolidColorPaint(color, 2.4f);
            _series.Fill = new LinearGradientPaint(new[] { color.WithAlpha(_dark ? (byte)60 : (byte)55), color.WithAlpha(3) }, new SKPoint(0, 0), new SKPoint(0, 1));
            _series.GeometryFill = new SolidColorPaint(color);
            _series.GeometryStroke = null;
            _xAxis.LabelsPaint = new SolidColorPaint(new SKColor(muted.R, muted.G, muted.B));
            _yAxis.SeparatorsPaint = new SolidColorPaint(new SKColor(grid.R, grid.G, grid.B, grid.A), 1);
        }

        private string LabelAt(double value)
        {
            var index = (int)Math.Round(value);
            if (index < 0 || index >= _points.Length || Math.Abs(value - index) > .01) return "";
            var step = _hourly ? 6 : Math.Max(1, _points.Length / 2);
            return index % step == 0 || index == _points.Length - 1 ? _points[index].Time.ToString(_hourly ? "HH" : "MM/dd") : "";
        }

        private void PointerMoved(object sender, MouseEventArgs e)
        {
            if (_points.Length == 0 || !HasPlotLayout) return;
            var position = e.GetPosition(_plot);
            var coordinate = _plot.ScalePixelsToData(new LvcPointD(position.X, position.Y));
            if (double.IsNaN(coordinate.X) || double.IsInfinity(coordinate.X)) return;
            FocusPoint(Math.Max(0, Math.Min(_points.Length - 1, (int)Math.Round(coordinate.X))));
        }

        internal void FocusPoint(int index)
        {
            if (index < 0 || index >= _points.Length || !HasPlotLayout) { HideDetail(); return; }
            _hoveredIndex = index;
            var point = _points[index];
            _detail.Text = point.Time.ToString("MM/dd HH:mm") + "\n消费  " + (_hide ? "••••" : "$" + point.Spend.ToString("0.########", CultureInfo.InvariantCulture)) +
                "\nToken  " + point.Tokens.ToString("N0", CultureInfo.InvariantCulture);
            var position = _plot.ScaleDataToPixels(new LvcPointD(index, _values[index].Y ?? 0));
            if (double.IsNaN(position.X) || double.IsNaN(position.Y) || double.IsInfinity(position.X) || double.IsInfinity(position.Y)) { HideDetail(); return; }
            _guide.X1 = _guide.X2 = position.X; _guide.Y1 = 7; _guide.Y2 = Height - 22;
            Canvas.SetLeft(_marker, position.X - 3.5); Canvas.SetTop(_marker, position.Y - 3.5);
            Canvas.SetLeft(_tooltip, Math.Max(0, Math.Min(ActualWidth - _tooltip.Width, position.X + 12)));
            Canvas.SetTop(_tooltip, 2);
            _overlay.Visibility = _tooltip.Visibility = Visibility.Visible;
            System.Windows.Automation.AutomationProperties.SetHelpText(this, _detail.Text);
        }

        private void HideDetail()
        {
            _hoveredIndex = -1; _overlay.Visibility = _tooltip.Visibility = Visibility.Collapsed; _detail.Text = "";
            System.Windows.Automation.AutomationProperties.SetHelpText(this, "");
        }

        private void ChartKeyDown(object sender, KeyEventArgs e)
        {
            if (_points.Length == 0) return;
            switch (e.Key)
            {
                case Key.Left: FocusPoint(Math.Max(0, _hoveredIndex < 0 ? 0 : _hoveredIndex - 1)); break;
                case Key.Right: FocusPoint(Math.Min(_points.Length - 1, _hoveredIndex + 1)); break;
                case Key.Home: FocusPoint(0); break;
                case Key.End: FocusPoint(_points.Length - 1); break;
                case Key.Escape: HideDetail(); break;
                default: return;
            }
            e.Handled = true;
        }
    }
}
