using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Amber.App.Services;

/// <summary>
/// 把主题包的组件样式（styles.json）翻译成 Avalonia 样式。只生成白名单里的选择器和属性，
/// 结果只加在主窗口上（安全窗口不受影响，见 <see cref="ThemeService.LockToDefault"/>）。
/// </summary>
internal static class ThemeStyleBuilder
{
    private delegate Selector Pick(Selector? x);

    public static Styles Build(ThemePack pack)
    {
        var styles = new Styles();
        foreach (var (component, states) in pack.Styles)
            foreach (var (state, props) in states)
                Add(styles, pack, component, state, props);
        return styles;
    }

    private static void Add(Styles styles, ThemePack pack, string component, string state, StyleProps p)
    {
        if (component.StartsWith("button", StringComparison.Ordinal))
        {
            var cls = component.Length > 6 ? component[7..] : null;
            Selector button(Selector? x)
            {
                var s = x.OfType<Button>();
                if (cls is not null) s = s.Class(cls);
                return state switch { ":hover" => s.Class(":pointerover"), ":pressed" => s.Class(":pressed"), ":disabled" => s.Class(":disabled"), _ => s };
            }
            Selector presenter(Selector? x) => button(x).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
            // 普通状态的颜色设在按钮上（模板绑定过去）；悬停/按下/禁用要直接设在模板的 ContentPresenter 上，才盖得住 Fluent 自带的状态样式。
            if (state == "") Box(styles, button, pack, p, Button.BackgroundProperty, Button.ForegroundProperty, Button.BorderBrushProperty,
                Button.BorderThicknessProperty, Button.CornerRadiusProperty, Button.PaddingProperty, Button.FontWeightProperty);
            else Box(styles, presenter, pack, p, ContentPresenter.BackgroundProperty, ContentPresenter.ForegroundProperty, ContentPresenter.BorderBrushProperty,
                ContentPresenter.BorderThicknessProperty, ContentPresenter.CornerRadiusProperty, ContentPresenter.PaddingProperty, null);
            if (p.Shadow is { } shadow) Set(styles, presenter, ContentPresenter.BoxShadowProperty, BoxShadows.Parse(shadow == "none" ? "0 0 0 0 #00000000" : shadow));
            return;
        }
        switch (component)
        {
            case "card": BorderLike(styles, x => x.OfType<Border>().Class("card"), pack, p); break;
            case "pill":
                BorderLike(styles, x => x.OfType<Border>().Class("pill"), pack, p);
                if (Brush(p.Foreground, pack) is { } pillText) Set(styles, x => x.OfType<Border>().Class("pill").Child().OfType<TextBlock>(), TextBlock.ForegroundProperty, pillText);
                break;
            case "callout": BorderLike(styles, x => x.OfType<Border>().Class("callout"), pack, p); break;
            case "sidebar": BorderLike(styles, x => x.OfType<Border>().Class("sidebar"), pack, p); break;
            case "nav.item":
            {
                Selector item(Selector? x) => x.OfType<ListBox>().Class("nav").Descendant().OfType<ListBoxItem>();
                Selector presenter(Selector? x)
                {
                    var s = item(x);
                    s = state switch { ":hover" => s.Class(":pointerover"), ":selected" => s.Class(":selected"), _ => s };
                    return s.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
                }
                Box(styles, presenter, pack, p, ContentPresenter.BackgroundProperty, ContentPresenter.ForegroundProperty, null,
                    null, ContentPresenter.CornerRadiusProperty, ContentPresenter.PaddingProperty, null);
                if (p.FontWeight is { } w) Set(styles, x => state == ":selected" ? item(x).Class(":selected") : item(x), ListBoxItem.FontWeightProperty, Weight(w));
                break;
            }
            case "input":
            {
                Selector box(Selector? x) => x.OfType<TextBox>();
                if (state == "") Box(styles, box, pack, p, TextBox.BackgroundProperty, TextBox.ForegroundProperty, TextBox.BorderBrushProperty,
                    TextBox.BorderThicknessProperty, TextBox.CornerRadiusProperty, TextBox.PaddingProperty, null);
                else
                {
                    Selector border(Selector? x) => box(x).Class(state == ":hover" ? ":pointerover" : ":focus").Template().OfType<Border>().Name("PART_BorderElement");
                    Box(styles, border, pack, p, Border.BackgroundProperty, null, Border.BorderBrushProperty, Border.BorderThicknessProperty, Border.CornerRadiusProperty, null, null);
                    if (Brush(p.Foreground, pack) is { } fg) Set(styles, x => box(x).Class(state == ":hover" ? ":pointerover" : ":focus"), TextBox.ForegroundProperty, fg);
                }
                break;
            }
            case "combobox":
            {
                Selector combo(Selector? x) => x.OfType<ComboBox>();
                if (state == "") Box(styles, combo, pack, p, ComboBox.BackgroundProperty, ComboBox.ForegroundProperty, ComboBox.BorderBrushProperty,
                    ComboBox.BorderThicknessProperty, ComboBox.CornerRadiusProperty, ComboBox.PaddingProperty, null);
                else Box(styles, x => combo(x).Class(":pointerover").Template().OfType<Border>().Name("Background"), pack, p,
                    Border.BackgroundProperty, null, Border.BorderBrushProperty, Border.BorderThicknessProperty, Border.CornerRadiusProperty, null, null);
                break;
            }
            case "heading":
                foreach (var cls in new[] { "h1", "h2" })
                    Box(styles, x => x.OfType<TextBlock>().Class(cls), pack, p, null, TextBlock.ForegroundProperty, null, null, null, null, TextBlock.FontWeightProperty);
                break;
            case "window":
                if (Brush(p.Background, pack) is { } bg) Set(styles, x => x.OfType<Window>(), Window.BackgroundProperty, bg);
                break;
        }
    }

    private static void BorderLike(Styles styles, Pick pick, ThemePack pack, StyleProps p)
    {
        Box(styles, pick, pack, p, Border.BackgroundProperty, null, Border.BorderBrushProperty, Border.BorderThicknessProperty, Border.CornerRadiusProperty, Border.PaddingProperty, null);
        if (p.Shadow is { } shadow) Set(styles, pick, Border.BoxShadowProperty, BoxShadows.Parse(shadow == "none" ? "0 0 0 0 #00000000" : shadow));
    }

    /// <summary>一组常见属性：哪个属性传了 null，就表示这个控件不支持它。</summary>
    private static void Box(Styles styles, Pick pick, ThemePack pack, StyleProps p,
        AvaloniaProperty? background, AvaloniaProperty? foreground, AvaloniaProperty? border, AvaloniaProperty? borderWidth,
        AvaloniaProperty? radius, AvaloniaProperty? padding, AvaloniaProperty? fontWeight)
    {
        var style = new Style(x => pick(x));
        void add(AvaloniaProperty? prop, object? value)
        {
            if (prop is not null && value is not null) style.Setters.Add(new Setter(prop, value));
        }
        add(background, Value(p.Background, pack));
        add(foreground, Value(p.Foreground, pack));
        add(border, Value(p.Border, pack));
        add(borderWidth, p.BorderWidth is { } bw ? new Thickness(bw) : null);
        add(radius, p.Radius is { } r ? new CornerRadius(r) : null);
        add(padding, p.Padding is { } pad ? new Thickness(pad.H, pad.V) : null);
        add(fontWeight, p.FontWeight is { } w ? Weight(w) : null);
        if (style.Setters.Count > 0) styles.Add(style);
    }

    private static void Set(Styles styles, Pick pick, AvaloniaProperty prop, object value)
    {
        var style = new Style(x => pick(x));
        style.Setters.Add(new Setter(prop, value));
        styles.Add(style);
    }

    private static FontWeight Weight(string w) => w switch
    {
        "medium" => FontWeight.Medium,
        "semibold" => FontWeight.SemiBold,
        "bold" => FontWeight.Bold,
        _ => FontWeight.Normal,
    };

    /// <summary>token 引用用 DynamicResource，深浅色切换时跟着变；其余直接给画刷。</summary>
    private static object? Value(Paint? paint, ThemePack pack) =>
        paint is { Kind: PaintKind.Token, Token: { } key } ? new DynamicResourceExtension(key) : Brush(paint, pack);

    private static object? Brush(Paint? paint, ThemePack pack)
    {
        switch (paint?.Kind)
        {
            case null: return null;
            case PaintKind.Token: return new DynamicResourceExtension(paint.Token!);
            case PaintKind.Color: return new SolidColorBrush(Color.FromUInt32(paint.Colors[0]));
            case PaintKind.Linear:
            {
                // CSS 的角度：0 度从下往上，90 度从左往右。
                var rad = paint.Angle * Math.PI / 180;
                var dx = Math.Sin(rad) / 2;
                var dy = -Math.Cos(rad) / 2;
                var brush = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.5 - dx, 0.5 - dy, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0.5 + dx, 0.5 + dy, RelativeUnit.Relative),
                };
                for (var i = 0; i < paint.Colors.Count; i++)
                    brush.GradientStops.Add(new GradientStop(Color.FromUInt32(paint.Colors[i]), (double)i / (paint.Colors.Count - 1)));
                return brush;
            }
            case PaintKind.Image:
                if (pack.Directory is null) return null;
                try
                {
                    using var stream = File.OpenRead(Path.Combine(pack.Directory, paint.ImagePath!));
                    return new ImageBrush(Bitmap.DecodeToWidth(stream, 1920)) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    pack.Problems.Add(new("bad_image", paint.ImagePath!));
                    return null;
                }
            default: return null;
        }
    }
}
