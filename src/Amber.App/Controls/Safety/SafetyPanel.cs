using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;

namespace Amber.App.Controls.Safety;

/// <summary>
/// 安全面板：2px 安全蓝框 + 盾牌 + “Jay 安全提示” + 标题 + 内容。嵌在页面里（敲门卡、收文件卡、隐私承诺），
/// SafetyDialog 里也用它。标题行的文字主题包写不出来（保留词），所以看到这一行就是 Jay 自己的提示。
/// </summary>
public class SafetyPanel : SafetySurface
{
    protected override Type StyleKeyOverride => typeof(SafetyPanel);

    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Bold, IsVisible = false };
    private readonly ContentControl _body = new();
    private readonly TextBlock _badge = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold };

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SafetyPanel, string?>(nameof(Title));
    public static readonly StyledProperty<object?> BodyProperty = AvaloniaProperty.Register<SafetyPanel, object?>(nameof(Body));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    public SafetyPanel()
    {
        _badge.Text = L.T("safety.title");
        _badge.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("SafetyFrame"));
        _title.Bind(TextBlock.FontSizeProperty, this.GetResourceObservable("FontSubtitle"));
        var shield = new Path { Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.75, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
        shield.Bind(Shape.StrokeProperty, this.GetResourceObservable("SafetyFrame"));
        shield.Bind(Path.DataProperty, this.GetResourceObservable("Icon.ShieldCheck"));
        Child = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { shield, _badge } },
                _title,
                _body,
            },
        };
        L.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => _badge.Text = L.T("safety.title"));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty) { _title.Text = Title; _title.IsVisible = !string.IsNullOrEmpty(Title); }
        else if (change.Property == BodyProperty) _body.Content = Body;
    }

    /// <summary>标题行（盾牌 + “Jay 安全提示”）的文字，自检用。</summary>
    public string BadgeText => _badge.Text ?? "";
}
