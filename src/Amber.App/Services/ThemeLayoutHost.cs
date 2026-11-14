using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;

namespace Amber.App.Services;

/// <summary>页面里可以被主题挪动的区块：在 XAML 里写 <c>svc:ThemeRegion.Name="home.launch"</c>。</summary>
public sealed class ThemeRegion : AvaloniaObject
{
    public static readonly AttachedProperty<string?> NameProperty =
        AvaloniaProperty.RegisterAttached<ThemeRegion, Control, string?>("Name");

    public static string? GetName(Control c) => c.GetValue(NameProperty);
    public static void SetName(Control c, string? value) => c.SetValue(NameProperty, value);
}

/// <summary>
/// 按主题的页面布局重新组装一个页面：把页面里标了 ThemeRegion 的区块从原来的位置取下来，放进主题描述的容器树里。
/// 区块本身（绑定、事件、名字）都不变，只是换了父控件。没有主题布局时把区块原样放回去。
/// 窗口宽度跨过档位（compact / medium / wide）或装饰元素的 hideBelow 时重新组装。
/// </summary>
public sealed class ThemeLayoutHost
{
    public const double CompactBelow = 640;
    public const double WideFrom = 1100;

    private readonly UserControl _view;
    private readonly string _pageId;
    private readonly Control _original;
    private readonly Dictionary<string, Control> _regions = [];
    private readonly Dictionary<Control, Snapshot> _snapshots = [];
    private string? _signature;

    private sealed record Snapshot(Control Parent, int Index, Dictionary<AvaloniaProperty, object?> Values);

    /// <summary>重新组装时会被改动的属性：放回原处时恢复成 XAML 里写的值。</summary>
    private static readonly AvaloniaProperty[] Touched =
    [
        Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty, Grid.ColumnSpanProperty, DockPanel.DockProperty,
        Layoutable.MarginProperty, Layoutable.WidthProperty, Layoutable.HeightProperty, Layoutable.MinWidthProperty, Layoutable.MaxWidthProperty,
        Layoutable.MinHeightProperty, Layoutable.MaxHeightProperty, Layoutable.HorizontalAlignmentProperty, Layoutable.VerticalAlignmentProperty,
    ];

    private ThemeLayoutHost(UserControl view, string pageId)
    {
        _view = view;
        _pageId = pageId;
        _original = (Control)view.Content!;
        foreach (var c in _original.GetSelfAndLogicalDescendants().OfType<Control>())
            if (ThemeRegion.GetName(c) is { } name) _regions[name] = c;
    }

    /// <summary>页面构造函数里 InitializeComponent 之后调用一次。</summary>
    public static ThemeLayoutHost Attach(UserControl view, string pageId)
    {
        var host = new ThemeLayoutHost(view, pageId);
        // 只在页面挂在窗口上时听主题变化，页面被丢掉后不留订阅。
        view.AttachedToVisualTree += (_, _) => { ThemeService.Changed += host.Apply; host.Apply(); };
        view.DetachedFromVisualTree += (_, _) => ThemeService.Changed -= host.Apply;
        view.SizeChanged += (_, _) => host.Apply();
        host.Apply();
        return host;
    }

    /// <summary>当前用的是不是主题的布局（自检用）。</summary>
    public bool UsingThemeLayout => !ReferenceEquals(_view.Content, _original);

    public IReadOnlyDictionary<string, Control> Regions => _regions;

    public void Apply()
    {
        var width = _view.Bounds.Width > 0 ? _view.Bounds.Width : (TopLevel.GetTopLevel(_view)?.Bounds.Width ?? 1280) - 220;
        var layout = ThemeService.Current?.Pages.GetValueOrDefault(_pageId);
        var tree = layout is null ? null : ThemeLayout.TreeFor(layout, Breakpoint(width));
        // 档位、隐藏阈值都没变就不重排（窗口拖动时每帧都会进来）。
        var signature = tree is null ? "original" : $"{RuntimeHelpers(tree)}|{string.Join(',', Thresholds(tree).Where(t => width >= t))}";
        if (signature == _signature) return;
        _signature = signature;
        if (tree is null) { Restore(); return; }
        foreach (var region in _regions.Values) Detach(region);
        try
        {
            _view.Content = Build(tree, width);
        }
        // Image nodes read their files here, so a missing or locked file (edited in --theme-dev mode) also lands in this fallback.
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 组装出错（不该发生，校验已经挡过）：这一页退回 Jay 自带布局，不影响别的页。
            ThemeService.Current?.Problems.Add(new("layout_failed", $"{_pageId}: {e.Message}"));
            _signature = "original";
            Restore();
        }
    }

    private static int RuntimeHelpers(LayoutNode tree) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tree);

    public static string Breakpoint(double width) => width < CompactBelow ? "compact" : width < WideFrom ? "medium" : "wide";

    private static IEnumerable<double> Thresholds(LayoutNode node) =>
        (node.HideBelow is { } h ? [h] : Enumerable.Empty<double>()).Concat(node.Children.SelectMany(Thresholds));

    private void Restore()
    {
        if (ReferenceEquals(_view.Content, _original) && _snapshots.Count == 0) return;
        foreach (var region in _regions.Values) Detach(region);
        _view.Content = _original;
        // 按原来的序号从小到大插回去，序号才对得上。
        foreach (var (region, snap) in _snapshots.OrderBy(p => p.Value.Index))
        {
            Insert(snap.Parent, snap.Index, region);
            foreach (var prop in Touched) region.ClearValue(prop);
            foreach (var (prop, value) in snap.Values) region.SetValue(prop, value);
        }
        _snapshots.Clear();
    }

    /// <summary>把区块从当前父控件上取下来；第一次取的时候记下原来的位置和属性。</summary>
    private void Detach(Control region)
    {
        var parent = region.Parent as Control;
        if (parent is not null && !_snapshots.ContainsKey(region) && ReferenceEquals(_view.Content, _original))
        {
            var values = new Dictionary<AvaloniaProperty, object?>();
            foreach (var prop in Touched)
                if (region.IsSet(prop)) values[prop] = region.GetValue(prop);
            _snapshots[region] = new Snapshot(parent, IndexIn(parent, region), values);
        }
        switch (parent)
        {
            case Panel panel: panel.Children.Remove(region); break;
            case Decorator decorator: decorator.Child = null; break;
            case ContentControl content: content.Content = null; break;
        }
    }

    private static int IndexIn(Control parent, Control child) => parent is Panel p ? p.Children.IndexOf(child) : 0;

    private static void Insert(Control parent, int index, Control child)
    {
        switch (parent)
        {
            case Panel panel: panel.Children.Insert(Math.Min(index, panel.Children.Count), child); break;
            case Decorator decorator: decorator.Child = child; break;
            case ContentControl content: content.Content = child; break;
        }
    }

    private Control? Build(LayoutNode node, double width)
    {
        if (node.HideBelow is { } hide && width < hide) return null;
        Control control;
        switch (node.Type)
        {
            case "region":
                if (!_regions.TryGetValue(node.Region!, out var region)) return null;
                foreach (var prop in new AvaloniaProperty[] { Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty, Grid.ColumnSpanProperty, DockPanel.DockProperty })
                    region.ClearValue(prop);
                control = region;
                break;
            case "stack":
                var stack = new StackPanel { Orientation = node.Horizontal ? Orientation.Horizontal : Orientation.Vertical, Spacing = node.Spacing ?? 0 };
                AddChildren(stack, node, width);
                control = stack;
                break;
            case "wrap":
                var wrap = new WrapPanel { ItemSpacing = node.Spacing ?? 0, LineSpacing = node.Spacing ?? 0 };
                AddChildren(wrap, node, width);
                control = wrap;
                break;
            case "dock":
                var dock = new DockPanel { LastChildFill = true };
                AddChildren(dock, node, width);
                control = dock;
                break;
            case "panel":
                var panel = new Panel();
                AddChildren(panel, node, width);
                control = panel;
                break;
            case "grid":
                var grid = new Grid { ColumnSpacing = node.Spacing ?? 0, RowSpacing = node.Spacing ?? 0 };
                if (node.Columns is { } cols) grid.ColumnDefinitions = new ColumnDefinitions(string.Join(',', cols.Select(Length)));
                if (node.Rows is { } rows) grid.RowDefinitions = new RowDefinitions(string.Join(',', rows.Select(Length)));
                AddChildren(grid, node, width);
                control = grid;
                break;
            case "scroll":
                control = new ScrollViewer { Content = Single(node, width) };
                break;
            case "card":
                var card = new Border { Child = Single(node, width) };
                card.Classes.Add("card");
                control = card;
                break;
            case "spacer":
                control = new Border();
                break;
            case "text":
                var text = new TextBlock { Text = node.Text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                if (node.TextStyle is { } style and not "body") text.Classes.Add(style);
                control = text;
                break;
            case "image":
                var dir = ThemeService.Current?.Directory;
                if (dir is null || node.Image is null) return null;
                using (var stream = File.OpenRead(Path.Combine(dir, node.Image)))
                    control = new Image { Source = Bitmap.DecodeToWidth(stream, 1280), Stretch = Avalonia.Media.Stretch.Uniform };
                break;
            default: return null;
        }
        Common(control, node);
        return control;
    }

    /// <summary>scroll 和 card 只能有一个孩子：多于一个就自动包一层竖排。</summary>
    private Control? Single(LayoutNode node, double width)
    {
        if (node.Children.Count == 1) return Build(node.Children[0], width);
        var stack = new StackPanel { Spacing = node.Spacing ?? 0 };
        AddChildren(stack, node, width);
        return stack;
    }

    private void AddChildren(Panel panel, LayoutNode node, double width)
    {
        foreach (var child in node.Children)
        {
            if (Build(child, width) is not { } c) continue;
            if (child.Row is { } r) Grid.SetRow(c, r);
            if (child.Column is { } col) Grid.SetColumn(c, col);
            if (child.RowSpan is { } rs) Grid.SetRowSpan(c, rs);
            if (child.ColumnSpan is { } cs) Grid.SetColumnSpan(c, cs);
            if (child.Dock is { } d) DockPanel.SetDock(c, d switch { "left" => Dock.Left, "right" => Dock.Right, "top" => Dock.Top, _ => Dock.Bottom });
            panel.Children.Add(c);
        }
    }

    private static void Common(Control c, LayoutNode n)
    {
        if (n.Width is { } w) c.Width = w;
        if (n.Height is { } h) c.Height = h;
        if (n.MinWidth is { } minW) c.MinWidth = minW;
        if (n.MaxWidth is { } maxW) c.MaxWidth = maxW;
        if (n.MinHeight is { } minH) c.MinHeight = minH;
        if (n.MaxHeight is { } maxH) c.MaxHeight = maxH;
        if (n.Margin is { } m) c.Margin = new Thickness(m.L, m.T, m.R, m.B);
        if (n.Padding is { } p)
        {
            var t = new Thickness(p.L, p.T, p.R, p.B);
            if (c is Border b) b.Padding = t;
            else if (c is Avalonia.Controls.Primitives.TemplatedControl tc) tc.Padding = t;
            else if (c is Decorator dec) dec.Padding = t;
        }
        if (n.HAlign is { } ha) c.HorizontalAlignment = ha switch { "start" => HorizontalAlignment.Left, "center" => HorizontalAlignment.Center, "end" => HorizontalAlignment.Right, _ => HorizontalAlignment.Stretch };
        if (n.VAlign is { } va) c.VerticalAlignment = va switch { "start" => VerticalAlignment.Top, "center" => VerticalAlignment.Center, "end" => VerticalAlignment.Bottom, _ => VerticalAlignment.Stretch };
    }

    private static string Length(string s) => s == "auto" ? "Auto" : s;
}
