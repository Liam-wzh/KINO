using System.Windows.Controls;
using System.Windows;
using Kino.Models;

namespace Kino.UI
{
    internal static class SiteMenus
    {
        internal static void Populate(ItemsControl menu, AppSettings settings, Action<string> select, Action add, Action manage, bool canSwitch = true)
        {
            foreach (var site in settings.Sites)
            {
                var id = site.Id;
                var title = new TextBlock { Text = site.DisplayName, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis };
                var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = id == settings.ActiveSiteId,
                    IsEnabled = canSwitch, ToolTip = site.Summary, InputGestureText = id == settings.ActiveSiteId ? "当前" : "" };
                item.Click += (_, __) => select(id); menu.Items.Add(item);
            }
            if (settings.Sites.Count > 0) menu.Items.Add(new Separator());
            Add(menu, "新增站点…", add); Add(menu, "管理站点…", manage);
        }
        private static void Add(ItemsControl menu, string text, Action action)
        { var item = new MenuItem { Header = text }; item.Click += (_, __) => action(); menu.Items.Add(item); }
    }
}
