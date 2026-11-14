using Amber.App.Services;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Amber.App.Views.Controls;

/// <summary>
/// The theme's scene (tokens.json "slots" → "scene"): up to four pictures stacked far to near behind the pages. With 3D
/// effects on, each layer moves against the pointer by its depth, so the window looks deep (pseudo-3D parallax). The
/// theme's page colour covers the scene by its "dim", like the background picture. Takes no clicks and sits under
/// everything, so it can't cover the content or the locked safety screens. Empty and invisible without a scene.
/// </summary>
public sealed class SceneView : Control
{
    private Point _pointer;
    private TopLevel? _top;

    static SceneView() => IsHitTestVisibleProperty.OverrideDefaultValue<SceneView>(false);

    /// <summary>How far each layer is drawn from its resting place now (self-checks read it).</summary>
    public IReadOnlyList<Vector> Offsets { get; private set; } = [];

    public bool HasScene => Depth3D.SceneLayers.Count > 0;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeService.Changed += Reload;
        Depth3D.Changed += Reload;
        // The dim over the scene is the page colour: a light/dark switch repaints it even when nothing moves.
        ActualThemeVariantChanged += OnThemeVariantChanged;
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(PointerMovedEvent, OnPointer, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        Reload();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeService.Changed -= Reload;
        Depth3D.Changed -= Reload;
        ActualThemeVariantChanged -= OnThemeVariantChanged;
        _top?.RemoveHandler(PointerMovedEvent, OnPointer);
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void Reload()
    {
        IsVisible = HasScene;
        InvalidateVisual();
    }

    private void OnPointer(object? sender, PointerEventArgs e)
    {
        if (!HasScene || Depth3D.Current.Parallax <= 0) return;
        PointTo(e.GetPosition(this));
    }

    /// <summary>Moves the layers as if the pointer were at <paramref name="point"/> (in this control's coordinates).</summary>
    public void PointTo(Point point)
    {
        _pointer = point;
        InvalidateVisual();
    }

    /// <summary>Self-check: how many times the scene was drawn.</summary>
    internal int Renders { get; private set; }

    public override void Render(DrawingContext context)
    {
        Renders++;
        var layers = Depth3D.SceneLayers;
        if (layers.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0) { Offsets = []; return; }
        var scene = Depth3D.Scene!;
        var max = Depth3D.Current.Parallax;
        var area = new Rect(Bounds.Size);
        var offsets = new List<Vector>();
        foreach (var (bitmap, depth) in layers)
        {
            // Every layer is drawn a little larger than the window, so moving it never shows an edge.
            var margin = max * depth;
            var (dx, dy) = Motion3D.ParallaxOffset(_pointer.X, _pointer.Y, Bounds.Width, Bounds.Height, depth, max);
            offsets.Add(new Vector(dx, dy));
            var target = area.Inflate(margin).Translate(new Vector(dx, dy));
            context.DrawImage(bitmap, SlotImage.Crop(bitmap.Size, target.Size, scene.FocusX, scene.FocusY), target);
        }
        Offsets = offsets;
        if (scene.Dim > 0 && this.TryFindResource("PageBg", ActualThemeVariant, out var res) && res is IBrush brush)
            using (context.PushOpacity(scene.Dim))
                context.FillRectangle(brush, area);
    }
}
