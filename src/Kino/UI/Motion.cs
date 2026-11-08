using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Kino.UI
{
    public static class Motion
    {
        internal static bool? EnabledOverride { get; set; }
        internal static bool Enabled => EnabledOverride ?? SystemParameters.ClientAreaAnimation;

        public static readonly DependencyProperty PressFeedbackProperty = DependencyProperty.RegisterAttached(
            "PressFeedback", typeof(bool), typeof(Motion), new PropertyMetadata(false, PressFeedbackChanged));
        public static bool GetPressFeedback(DependencyObject target) => (bool)target.GetValue(PressFeedbackProperty);
        public static void SetPressFeedback(DependencyObject target, bool value) => target.SetValue(PressFeedbackProperty, value);

        private static void PressFeedbackChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
        {
            if (!(target is ButtonBase button)) return;
            if ((bool)e.NewValue)
            {
                button.RenderTransformOrigin = new Point(.5, .5);
                button.RenderTransform = new ScaleTransform(1, 1);
                button.PreviewMouseLeftButtonDown += Pressed;
                button.PreviewMouseLeftButtonUp += Released;
                button.MouseLeave += Released;
                button.LostMouseCapture += Released;
                button.PreviewKeyDown += KeyPressed;
                button.PreviewKeyUp += KeyReleased;
            }
            else
            {
                button.PreviewMouseLeftButtonDown -= Pressed;
                button.PreviewMouseLeftButtonUp -= Released;
                button.MouseLeave -= Released;
                button.LostMouseCapture -= Released;
                button.PreviewKeyDown -= KeyPressed;
                button.PreviewKeyUp -= KeyReleased;
                Scale(button, 1);
            }
        }

        private static void Pressed(object sender, MouseButtonEventArgs e) => Scale((ButtonBase)sender, .94);
        private static void Released(object sender, MouseEventArgs e) => Scale((ButtonBase)sender, 1);
        private static void KeyPressed(object sender, KeyEventArgs e)
        { if (e.Key == Key.Space || e.Key == Key.Enter) Scale((ButtonBase)sender, .94); }
        private static void KeyReleased(object sender, KeyEventArgs e)
        { if (e.Key == Key.Space || e.Key == Key.Enter) Scale((ButtonBase)sender, 1); }

        private static void Scale(ButtonBase button, double target)
        {
            if (!(button.RenderTransform is ScaleTransform transform)) return;
            Animate(transform, ScaleTransform.ScaleXProperty, target, 85);
            Animate(transform, ScaleTransform.ScaleYProperty, target, 85);
        }

        internal static void SetText(TextBlock target, string text, bool animate)
        {
            if (target.Text == text && animate) return;
            target.BeginAnimation(UIElement.OpacityProperty, null);
            target.Opacity = 1;
            target.Text = text;
            // Replace the value first. No old amount is retained during privacy or account changes.
            if (animate && Enabled && target.IsVisible && target.IsLoaded)
                target.BeginAnimation(UIElement.OpacityProperty, Fade(.5, 1, 150));
        }

        internal static void PrepareReveal(FrameworkElement target)
        {
            target.BeginAnimation(UIElement.OpacityProperty, null);
            // Prepare before Show. Keep the acrylic card's content legible while
            // its native chart is initializing; never move it inside the backdrop.
            target.Opacity = Enabled ? .88 : 1;
        }

        internal static void Reveal(FrameworkElement target)
        {
            var from = target.Opacity;
            target.BeginAnimation(UIElement.OpacityProperty, null);
            if (!Enabled || from >= 1) { target.Opacity = 1; return; }
            // Keep the base at the prepared opacity until the clock starts.
            // Setting it to 1 here can expose an opaque frame before the fade.
            target.Opacity = from;
            var opacity = Fade(from, 1, 160);
            opacity.FillBehavior = FillBehavior.HoldEnd;
            opacity.Completed += (_, __) =>
            {
                target.Opacity = 1;
                target.BeginAnimation(UIElement.OpacityProperty, null);
            };
            target.BeginAnimation(UIElement.OpacityProperty, opacity);
        }

        internal static void Animate(Animatable target, DependencyProperty property, double value, int milliseconds)
        {
            var from = (double)target.GetValue(property);
            target.BeginAnimation(property, null);
            target.SetValue(property, value);
            if (Enabled && Math.Abs(from - value) > .0001)
                target.BeginAnimation(property, Fade(from, value, milliseconds));
        }

        private static DoubleAnimation Fade(double from, double to, int milliseconds) => new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
    }
}
