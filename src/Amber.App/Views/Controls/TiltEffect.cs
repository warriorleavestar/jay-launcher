using Amber.App.Controls.Safety;
using Amber.App.Services;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Amber.App.Views.Controls;

/// <summary>
/// A card that leans toward the pointer (pseudo-3D, <see cref="Depth3D"/>): <c>c:TiltEffect.IsOn="True"</c>. The side
/// under the pointer goes back a few degrees and the card settles flat when the pointer leaves. Nothing leans when 3D
/// effects are off or motion is reduced, and nothing inside a safety surface ever leans.
/// </summary>
public static class TiltEffect
{
    public static readonly AttachedProperty<bool> IsOnProperty = AvaloniaProperty.RegisterAttached<Control, bool>("IsOn", typeof(TiltEffect));

    public static bool GetIsOn(Control c) => c.GetValue(IsOnProperty);
    public static void SetIsOn(Control c, bool value) => c.SetValue(IsOnProperty, value);

    static TiltEffect() => IsOnProperty.Changed.AddClassHandler<Control>(OnChanged);

    private static void OnChanged(Control c, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            c.PointerMoved += Moved;
            c.PointerExited += Exited;
        }
        else
        {
            c.PointerMoved -= Moved;
            c.PointerExited -= Exited;
            Flatten(c);
        }
    }

    private static void Moved(object? sender, PointerEventArgs e)
    {
        if (sender is Control c) LeanTo(c, e.GetPosition(c));
    }

    private static void Exited(object? sender, PointerEventArgs e)
    {
        if (sender is Control c) Release(c);
    }

    /// <summary>True for anything inside a safety surface (knock, file offer, privacy row): those stay flat and unthemed.</summary>
    public static bool IsLocked(Control c) => c.GetSelfAndVisualAncestors().Any(v => v is SafetySurface);

    /// <summary>Leans <paramref name="c"/> toward <paramref name="point"/> (in its own coordinates). Returns false when it stays flat.</summary>
    public static bool LeanTo(Control c, Point point)
    {
        var spec = Depth3D.Current;
        if (spec.Tilt <= 0 || IsLocked(c))
        {
            Flatten(c);
            return false;
        }
        Depth3D.Stop(c);
        var (ax, ay) = Motion3D.TiltAngles(point.X, point.Y, c.Bounds.Width, c.Bounds.Height, spec.Tilt);
        var r = Transform(c, spec.Perspective);
        r.AngleX = ax;
        r.AngleY = ay;
        return true;
    }

    /// <summary>The pointer left: the card settles flat (at once when motion is reduced).</summary>
    public static void Release(Control c)
    {
        if (c.RenderTransform is not Rotate3DTransform r) return;
        var (fromX, fromY) = (r.AngleX, r.AngleY);
        var duration = Depth3D.Current.IsFlat ? TimeSpan.Zero
            : Application.Current?.TryGetResource("MotionBase", null, out var d) == true && d is TimeSpan t ? t : TimeSpan.FromMilliseconds(160);
        Depth3D.Animate(c, duration, k =>
        {
            r.AngleX = fromX * (1 - k);
            r.AngleY = fromY * (1 - k);
        }, () => Flatten(c));
    }

    /// <summary>The current lean of <paramref name="c"/> in degrees (0, 0 when flat).</summary>
    public static (double AngleX, double AngleY) Angles(Control c) =>
        c.RenderTransform is Rotate3DTransform r ? (r.AngleX, r.AngleY) : (0, 0);

    private static Rotate3DTransform Transform(Control c, double perspective)
    {
        if (c.RenderTransform is Rotate3DTransform r)
        {
            r.Depth = perspective;
            return r;
        }
        c.RenderTransformOrigin = RelativePoint.Center;
        var created = new Rotate3DTransform { Depth = perspective };
        c.RenderTransform = created;
        return created;
    }

    private static void Flatten(Control c)
    {
        Depth3D.Stop(c);
        if (c.RenderTransform is Rotate3DTransform) c.RenderTransform = null;
    }
}
