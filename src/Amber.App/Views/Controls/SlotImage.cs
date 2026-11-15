using Amber.App.Services;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Amber.App.Views.Controls;

/// <summary>
/// One image slot of the theme (design doc "Image themes"): the window background, the sidebar, the mascot in a corner of
/// the content area, or the banner behind the library's "continue" card and the room banner. Empty and invisible when the
/// theme has no picture for the slot. It never takes clicks, and it sits under everything it decorates, so a picture can't
/// cover the content or the locked safety screens.
/// </summary>
public sealed class SlotImage : Control
{
    public static readonly StyledProperty<string?> SlotProperty = AvaloniaProperty.Register<SlotImage, string?>(nameof(Slot));

    /// <summary>Below this width the mascot steps aside: in a narrow window every pixel is content.</summary>
    public const double MascotMinWidth = 720;

    public string? Slot
    {
        get => GetValue(SlotProperty);
        set => SetValue(SlotProperty, value);
    }

    /// <summary>False: show <see cref="Picture"/> with <see cref="Settings"/> instead of the theme's (the picture theme preview).</summary>
    public static readonly StyledProperty<bool> UseThemeProperty = AvaloniaProperty.Register<SlotImage, bool>(nameof(UseTheme), true);
    public static readonly StyledProperty<Bitmap?> PictureProperty = AvaloniaProperty.Register<SlotImage, Bitmap?>(nameof(Picture));
    public static readonly StyledProperty<ThemeSlot?> SettingsProperty = AvaloniaProperty.Register<SlotImage, ThemeSlot?>(nameof(Settings));
    /// <summary>The colour over the picture when not taken from the theme (the preview's page or sidebar colour).</summary>
    public static readonly StyledProperty<IBrush?> CoverProperty = AvaloniaProperty.Register<SlotImage, IBrush?>(nameof(Cover));

    public bool UseTheme { get => GetValue(UseThemeProperty); set => SetValue(UseThemeProperty, value); }
    public Bitmap? Picture { get => GetValue(PictureProperty); set => SetValue(PictureProperty, value); }
    public ThemeSlot? Settings { get => GetValue(SettingsProperty); set => SetValue(SettingsProperty, value); }
    public IBrush? Cover { get => GetValue(CoverProperty); set => SetValue(CoverProperty, value); }

    private Bitmap? _bitmap;
    private ThemeSlot? _settings;

    static SlotImage()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<SlotImage>(false);
        AffectsRender<SlotImage>(SlotProperty, PictureProperty, SettingsProperty, CoverProperty);
    }

    /// <summary>Whether the slot has a picture now (self-checks and the theme panel's preview ask).</summary>
    public bool HasPicture => _bitmap is not null;

    /// <summary>Where the mascot was drawn last, in this control's coordinates (empty when not drawn).</summary>
    public Rect MascotBounds { get; private set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeService.Changed += Reload;
        ActualThemeVariantChanged += OnVariantChanged;
        Reload();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeService.Changed -= Reload;
        ActualThemeVariantChanged -= OnVariantChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == SlotProperty || change.Property == UseThemeProperty || change.Property == PictureProperty || change.Property == SettingsProperty)
            && VisualRoot is not null) Reload();
    }

    private void OnVariantChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void Reload()
    {
        if (!UseTheme) (_bitmap, _settings) = (Picture, Picture is null ? null : Settings ?? new ThemeSlot { Image = "" });
        else (_bitmap, _settings) = Slot is { } slot && ThemeService.SlotPicture(slot) is { } picture ? (picture.Bitmap, picture.Settings) : (null, null);
        IsVisible = _bitmap is not null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        MascotBounds = default;
        if (_bitmap is not { } bitmap || _settings is not { } s || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var size = bitmap.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        if (Slot == ThemeSlots.Mascot)
        {
            RenderMascot(context, bitmap, s);
            return;
        }
        var area = new Rect(Bounds.Size);
        context.DrawImage(bitmap, Crop(size, area.Size, s.FocusX, s.FocusY), area);
        // The theme's own colour over the picture, so the text on it stays readable.
        var cover = Slot switch { ThemeSlots.Background => "PageBg", ThemeSlots.Sidebar => "SidebarBg", _ => null };
        var brush = UseTheme ? (cover is not null && this.TryFindResource(cover, ActualThemeVariant, out var res) ? res as IBrush : null) : Cover;
        if (s.Dim > 0 && cover is not null && brush is not null)
            using (context.PushOpacity(s.Dim))
                context.FillRectangle(brush, area);
    }

    private void RenderMascot(DrawingContext context, Bitmap bitmap, ThemeSlot s)
    {
        if (UseTheme && Bounds.Width < MascotMinWidth) return;
        var size = bitmap.Size;
        var height = Bounds.Height * s.Size;
        var width = height * size.Width / size.Height;
        // Never wider than a third of the content area.
        if (width > Bounds.Width / 3)
        {
            width = Bounds.Width / 3;
            height = width * size.Height / size.Width;
        }
        const double margin = 12;
        var x = s.Side == "left" ? margin : Bounds.Width - width - margin;
        MascotBounds = new Rect(x, Bounds.Height - height, width, height);
        using (context.PushOpacity(s.Opacity))
            context.DrawImage(bitmap, new Rect(size), MascotBounds);
    }

    /// <summary>
    /// The part of the picture that fills <paramref name="area"/> without stretching (cropped to fill), keeping the focus
    /// point as near the middle as the picture allows.
    /// </summary>
    internal static Rect Crop(Size picture, Size area, double focusX, double focusY)
    {
        var scale = Math.Max(area.Width / picture.Width, area.Height / picture.Height);
        var w = Math.Min(picture.Width, area.Width / scale);
        var h = Math.Min(picture.Height, area.Height / scale);
        var x = Math.Clamp(focusX * picture.Width - w / 2, 0, picture.Width - w);
        var y = Math.Clamp(focusY * picture.Height - h / 2, 0, picture.Height - h);
        return new Rect(x, y, w, h);
    }
}
