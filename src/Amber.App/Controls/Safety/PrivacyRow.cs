using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;

namespace Amber.App.Controls.Safety;

/// <summary>
/// 侧栏底部的隐私行（§5.9）：盾牌 + “无云控 · 无遥测”，任何尺寸都看得见，主题改不了。
/// 底色透明（跟着侧栏），文字用默认令牌的侧栏次要色。
/// </summary>
public class PrivacyRow : SafetySurface
{
    protected override Type StyleKeyOverride => typeof(PrivacyRow);

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    public PrivacyRow()
    {
        var shield = new Path { Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center };
        shield.Bind(Shape.StrokeProperty, this.GetResourceObservable("SidebarTextMuted"));
        shield.Bind(Path.DataProperty, this.GetResourceObservable("Icon.Shield"));
        _text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("SidebarTextMuted"));
        _text.Bind(TextBlock.FontSizeProperty, this.GetResourceObservable("FontCaption"));
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { shield, _text } };
        Update();
        L.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Update);
    }

    private void Update()
    {
        _text.Text = $"{L.T("main.no_cloud_control")} · {L.T("main.no_telemetry")}";
        Avalonia.Automation.AutomationProperties.SetName(this, _text.Text);
    }

    public string Text => _text.Text ?? "";

    /// <summary>图标栏里只留盾牌，文字放进提示（读屏名字不变）。</summary>
    public bool Compact
    {
        get => !_text.IsVisible;
        set
        {
            _text.IsVisible = !value;
            ToolTip.SetTip(this, value ? _text.Text : null);
        }
    }
}
