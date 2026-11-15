using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Amber.App.Views.Controls;

/// <summary>
/// The account's skin as a standing figure that turns (pseudo-3D, <see cref="SkinTurntable"/>): drag it, or focus it and
/// press ← / →, Home goes back to the resting angle. While the pointer is on it and 3D effects are on, it turns slowly by
/// itself; when the pointer leaves it settles back. With reduced motion nothing turns by itself, but dragging still works
/// (the person moves it, nothing animates).
/// </summary>
public sealed class SkinFigure : Control
{
    public static readonly StyledProperty<SkinTexture?> SkinProperty = AvaloniaProperty.Register<SkinFigure, SkinTexture?>(nameof(Skin));
    public static readonly StyledProperty<double> AngleProperty = AvaloniaProperty.Register<SkinFigure, double>(nameof(Angle), SkinTurntable.RestAngle);

    /// <summary>Degrees a second while turning by itself.</summary>
    public const double SpinSpeed = 40;
    public const double KeyStep = 15;

    public SkinTexture? Skin { get => GetValue(SkinProperty); set => SetValue(SkinProperty, value); }
    public double Angle { get => GetValue(AngleProperty); set => SetValue(AngleProperty, value); }

    private WriteableBitmap? _bitmap;
    private (SkinTexture? Skin, int Angle, int Scale) _drawn;
    private DispatcherTimer? _spin;
    private DateTime _spinLast;
    private Point? _drag;

    static SkinFigure()
    {
        FocusableProperty.OverrideDefaultValue<SkinFigure>(true);
        AffectsRender<SkinFigure>(SkinProperty, AngleProperty, IsFocusedProperty);
    }

    public SkinFigure()
    {
        AutomationProperties.SetName(this, L.T("me.skin.figure"));
        ToolTip.SetTip(this, L.T("me.skin.figure.tip"));
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
    }

    /// <summary>Whether it is turning by itself now (self-checks read it).</summary>
    public bool Spinning => _spin?.IsEnabled == true;

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(availableSize.Width, SkinTurntable.UnitsWide * 7), Math.Min(availableSize.Height, SkinTurntable.UnitsTall * 7));

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        StartSpin();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_drag is null) Settle();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        StopSpin();
        _drag = e.GetPosition(this);
        e.Pointer.Capture(this);
        Focus(NavigationMethod.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } from) return;
        var at = e.GetPosition(this);
        TurnBy((at.X - from.X) * 1.2);
        _drag = at;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = null;
        e.Pointer.Capture(null);
    }

    /// <summary>The system or another control took the pointer mid-drag: the drag ends here, no release will come.</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: StopSpin(); TurnBy(-KeyStep); e.Handled = true; break;
            case Key.Right: StopSpin(); TurnBy(KeyStep); e.Handled = true; break;
            case Key.Home: StopSpin(); Depth3D.Stop(this); Angle = SkinTurntable.RestAngle; e.Handled = true; break;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Depth3D.Changed += OnDepthChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Depth3D.Changed -= OnDepthChanged;
        StopSpin();
        Depth3D.Stop(this);
        _drag = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The theme or the settings turned the turntable off (or reduced motion): a figure turning by itself stops.</summary>
    internal void OnDepthChanged()
    {
        if (Spinning && !Depth3D.Current.Turntable) Settle();
    }

    /// <summary>Turns the figure by <paramref name="degrees"/>, kept within one turn.</summary>
    public void TurnBy(double degrees)
    {
        Depth3D.Stop(this);
        var a = (Angle + degrees) % 360;
        if (a > 180) a -= 360;
        if (a <= -180) a += 360;
        Angle = a;
    }

    /// <summary>Starts turning by itself, when the theme and the settings allow it.</summary>
    public bool StartSpin()
    {
        if (!Depth3D.Current.Turntable || Skin is null) return false;
        Depth3D.Stop(this);
        _spin ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            // Checked every frame too, so a change that came in any other way still stops it.
            if (!Depth3D.Current.Turntable)
            {
                Settle();
                return;
            }
            var now = DateTime.UtcNow;
            TurnBy(SpinSpeed * (now - _spinLast).TotalSeconds);
            _spinLast = now;
        });
        _spinLast = DateTime.UtcNow;
        _spin.Start();
        return true;
    }

    private void StopSpin() => _spin?.Stop();

    /// <summary>Back to the resting angle: eased with 3D effects on, at once with them off.</summary>
    public void Settle()
    {
        StopSpin();
        var from = Angle;
        var to = SkinTurntable.RestAngle;
        var delta = (to - from) % 360;
        if (delta > 180) delta -= 360;
        if (delta < -180) delta += 360;
        var duration = Depth3D.Current.IsFlat ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Clamp(Math.Abs(delta) * 3, 160, 600));
        Depth3D.Animate(this, duration, k => Angle = from + delta * k);
    }

    public override void Render(DrawingContext context)
    {
        if (Skin is not { } skin || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var scale = Math.Max(1, (int)Math.Min(Bounds.Width / SkinTurntable.UnitsWide, Bounds.Height / SkinTurntable.UnitsTall));
        var angle = (int)Math.Round(Angle);
        if (_bitmap is null || _drawn != (skin, angle, scale))
        {
            var pixels = SkinTurntable.Render(skin, angle, scale, out var w, out var h);
            if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }
            using (var buffer = _bitmap.Lock())
            {
                // Pixels are opaque or fully transparent, so premultiplied and straight are the same.
                var row = new int[w];
                for (var y = 0; y < h; y++)
                {
                    Buffer.BlockCopy(pixels, y * w * 4, row, 0, w * 4);
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, w);
                }
            }
            _drawn = (skin, angle, scale);
        }
        var size = _bitmap.Size;
        var x = (Bounds.Width - size.Width) / 2;
        var y0 = (Bounds.Height - size.Height) / 2;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            context.DrawImage(_bitmap, new Rect(x, y0, size.Width, size.Height));
        if (IsFocused && this.TryFindResource("Focus", ActualThemeVariant, out var res) && res is IBrush focus)
            context.DrawRectangle(new Pen(focus, 2), new Rect(Bounds.Size).Deflate(1), 4);
    }
}
