using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Kino.Models;

namespace Kino.UI
{
    internal static class ThemeService
    {
        public static bool IsDark { get; private set; }
        public static void Apply(ThemeKind theme)
        {
            var systemDark = false;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                systemDark = Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)) == 0;
            IsDark = theme == ThemeKind.Dark || (theme == ThemeKind.System && systemDark);
            ApplicationThemeManager.Apply(IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.None, false);
            Set("KinoAccentBrush", IsDark ? "#63D9C3" : "#137E70");
            Set("KinoAccentSoftBrush", IsDark ? "#1663D9C3" : "#10137E70");
            Set("KinoSurfaceBrush", IsDark ? "#28FFFFFF" : "#75FFFFFF");
            Set("KinoPopupBrush", IsDark ? "#303535" : "#FCFCFC");
            Set("KinoBorderBrush", IsDark ? "#18FFFFFF" : "#16000000");
            Set("KinoMutedBrush", IsDark ? "#ACACAC" : "#696969");
            Set("KinoChartGridBrush", IsDark ? "#18FFFFFF" : "#10000000");
            Set("KinoWarningBrush", IsDark ? "#F5CA83" : "#9C6500");
        }
        private static void Set(string name, string color) => Application.Current.Resources[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
}
