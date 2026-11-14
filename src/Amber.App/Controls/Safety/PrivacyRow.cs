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
/// The row has an opaque backing in the default sidebar colour (from the locked tokens), so a theme's sidebar picture
/// can't show through behind the fixed text and shield; the text uses the default sidebar muted colour.
/// </summary>
public class PrivacyRow : SafetySurface
{
    protected override Type StyleKeyOverride => typeof(PrivacyRow);

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _panel;
    private bool _compact;

    public PrivacyRow()
    {
        var shield = new Path { Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center };
        shield.Bind(Shape.StrokeProperty, this.GetResourceObservable("SidebarTextMuted"));
        shield.Bind(Path.DataProperty, this.GetResourceObservable("Icon.Shield"));
        _text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("SidebarTextMuted"));
        _text.Bind(TextBlock.FontSizeProperty, this.GetResourceObservable("FontCaption"));
        _panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { shield, _text } };
        Child = _panel;
        Update();
        Amber.App.Localization.LanguageListeners.Add(this, static o => ((PrivacyRow)o).Update());
    }

    private string FullText => $"{L.T("main.no_cloud_control")} · {L.T("main.no_telemetry")}";

    private void Update()
    {
        // 图标栏里：盾牌在上，下面一行小字“无遥测”；完整的话放进提示。读屏名字总是完整的。
        _text.Text = _compact ? L.T("main.no_telemetry") : FullText;
        Avalonia.Automation.AutomationProperties.SetName(this, FullText);
        ToolTip.SetTip(this, _compact ? FullText : null);
    }

    public string Text => FullText;

    /// <summary>图标栏：盾牌加一行短字“无遥测”，竖着排、居中（§3.1），完整的话在提示里。</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            _panel.Orientation = value ? Orientation.Vertical : Orientation.Horizontal;
            _panel.Spacing = value ? 2 : 6;
            _panel.HorizontalAlignment = value ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            _text.TextAlignment = value ? TextAlignment.Center : TextAlignment.Left;
            _text.MaxWidth = value ? 68 : double.PositiveInfinity;
            foreach (var c in _panel.Children) c.HorizontalAlignment = value ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            Update();
        }
    }
}
