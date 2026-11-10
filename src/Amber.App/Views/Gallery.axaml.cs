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
        // Every automatic cover as a library card (wide) and a sidebar tile (square), for the §12 design review.
        var initials = new[] { "M", "苍", "S", "あ", "F", "暮", "1", "K", "B", "林", "Z", "晨" };
        CoverGrid.ItemsSource = Enumerable.Range(0, Amber.Core.GameLibrary.CoverArt.Slots).Select(slot => (Control)new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new Border
                {
                    Width = 200, Height = 104, CornerRadius = new Avalonia.CornerRadius(10), ClipToBounds = true, Background = CoverBrush.For(slot),
                    Child = new TextBlock { Text = initials[slot], FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Margin = new Avalonia.Thickness(14, 0, 0, 10),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom },
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new Border { Width = 44, Height = 44, CornerRadius = new Avalonia.CornerRadius(8), ClipToBounds = true, Background = CoverBrush.For(slot),
                            Child = new TextBlock { Text = initials[slot], FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } },
                        new TextBlock { Text = $"{slot} {CoverBrush.Pairs[slot].Name}", Classes = { "caption" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                    },
                },
            },
        }).ToList();
    }

    /// <summary>图标表里一共有多少个（自检用）。</summary>
    public int IconCount => (IconGrid.ItemsSource as List<Control>)?.Count ?? 0;

    /// <summary>How many automatic covers the review grid shows (self-check).</summary>
    public int CoverCount => (CoverGrid.ItemsSource as List<Control>)?.Count ?? 0;
}
