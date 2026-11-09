using Avalonia.Controls;
using Avalonia.Media;

namespace Amber.App.Views;

/// <summary>控件库开发者页：把 Themes/Icons.axaml 里的图标全部排出来，其余格子写在 axaml 里。</summary>
public partial class Gallery : UserControl
{
    public Gallery()
    {
        InitializeComponent();
        var icons = new List<Control>();
        if (Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://Jay/Themes/Icons.axaml")) is IResourceDictionary d)
            foreach (var key in d.Keys.OfType<string>().Where(k => k.StartsWith("Icon.", StringComparison.Ordinal)).Order())
                if (d[key] is Geometry g)
                {
                    var path = new Avalonia.Controls.Shapes.Path { Data = g, Classes = { "icon" } };
                    ToolTip.SetTip(path, key);
                    icons.Add(new StackPanel { Width = 72, Spacing = 4, Children = { path, new TextBlock { Text = key[5..], Classes = { "caption" }, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center } } });
                }
        IconGrid.ItemsSource = icons;
    }

    /// <summary>图标表里一共有多少个（自检用）。</summary>
    public int IconCount => (IconGrid.ItemsSource as List<Control>)?.Count ?? 0;
}
