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
        // Every automatic cover as a library card (wide) and a sidebar tile (square), for the design review.
        var initials = new[] { "M", "苍", "S", "あ", "F", "暮", "1", "K", "B", "林", "Z", "晨" };
        CoverGrid.ItemsSource = Enumerable.Range(0, Amber.Core.GameLibrary.CoverArt.Slots).Select(slot => (Control)new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new Border
                {
                    Width = 184, Height = 104, CornerRadius = new Avalonia.CornerRadius(10), ClipToBounds = true, Background = CoverBrush.For(slot),
                    Child = new TextBlock { Text = initials[slot], FontSize = 44, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Opacity = 0.92,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new Border { Width = 40, Height = 40, CornerRadius = new Avalonia.CornerRadius(8), ClipToBounds = true, Background = CoverBrush.For(slot),
                            Child = new TextBlock { Text = initials[slot], FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } },
                        new TextBlock { Text = $"{slot} {Amber.Core.Themes.PixelCover.Palettes[slot].Name}", Classes = { "caption" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                    },
                },
            },
        }).ToList();
        // Pixel faces at the three sizes lists use (24, 32, 48), for a spread of names in several scripts.
        var names = new[] { "Steve", "Alex", "小松鸦", "Wren", "Finch", "苍蓝", "あかり", "Notch_fan", "林间小屋", "K", "moss_42", "晨", "Juniper", "老白", "Pixel", "夜航" };
        FaceGrid.ItemsSource = names.Select(name => (Control)new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6,
                    Children =
                    {
                        Face(name, 48), Face(name, 32), Face(name, 24),
                    },
                },
                new TextBlock { Text = name, Classes = { "caption" } },
            },
        }).ToList();
    }

    private static Border Face(string name, double size) => new()
    {
        Width = size, Height = size, CornerRadius = new Avalonia.CornerRadius(size / 8), ClipToBounds = true, Background = FaceBrush.For(name),
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
    };

    /// <summary>图标表里一共有多少个（自检用）。</summary>
    public int IconCount => (IconGrid.ItemsSource as List<Control>)?.Count ?? 0;

    /// <summary>How many automatic covers the review grid shows (self-check).</summary>
    public int CoverCount => (CoverGrid.ItemsSource as List<Control>)?.Count ?? 0;

    /// <summary>How many pixel faces the review grid shows (self-check).</summary>
    public int FaceCount => (FaceGrid.ItemsSource as List<Control>)?.Count ?? 0;
}
