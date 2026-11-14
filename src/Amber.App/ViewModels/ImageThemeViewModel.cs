using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Themes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One image slot in "make a theme from pictures": the picture and its settings.</summary>
public sealed partial class ImageSlotItem : ObservableObject
{
    private readonly ImageThemeViewModel _owner;

    internal ImageSlotItem(ImageThemeViewModel owner, string slot)
    {
        _owner = owner;
        Slot = slot;
        Dim = ThemeSlots.DefaultDim(slot);
        // The slot's title and hint follow a language switch while the editor is open.
        Amber.App.Localization.LanguageListeners.Add(this, static o => ((ImageSlotItem)o).OnLanguageChanged());
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Hint));
    }

    public string Slot { get; }
    public string Title => L.T(Slot switch
    {
        ThemeSlots.Background => "theme.image.slot.background",
        ThemeSlots.Sidebar => "theme.image.slot.sidebar",
        ThemeSlots.Mascot => "theme.image.slot.mascot",
        _ => "theme.image.slot.banner",
    });

    public string Hint => L.T(Slot switch
    {
        ThemeSlots.Background => "theme.image.slot.background.hint",
        ThemeSlots.Sidebar => "theme.image.slot.sidebar.hint",
        ThemeSlots.Mascot => "theme.image.slot.mascot.hint",
        _ => "theme.image.slot.banner.hint",
    });

    public bool ShowsDim => Slot is ThemeSlots.Background or ThemeSlots.Sidebar;
    public bool ShowsBlur => Slot == ThemeSlots.Background;
    public bool ShowsFocus => Slot != ThemeSlots.Mascot;
    public bool IsMascot => Slot == ThemeSlots.Mascot;
    public double MaxDim => ThemeSlots.MaxDim;
    public double MaxBlur => ThemeSlots.MaxBlur;
    public double MinSize => ThemeSlots.MinMascotSize;
    public double MaxSize => ThemeSlots.MaxMascotSize;
    public double MinOpacity => ThemeSlots.MinMascotOpacity;

    /// <summary>The picture as it will go in the pack; null while the slot is empty.</summary>
    internal ThemeImages.Prepared? Picture { get; private set; }

    /// <summary>The picture for the preview (decoded from what goes in the pack).</summary>
    [ObservableProperty] public partial Bitmap? Preview { get; private set; }
    [ObservableProperty] public partial string FileName { get; private set; } = "";
    public bool HasPicture => Picture is not null;

    [ObservableProperty] public partial double Dim { get; set; }
    [ObservableProperty] public partial double Blur { get; set; }
    [ObservableProperty] public partial double FocusX { get; set; } = 0.5;
    [ObservableProperty] public partial double FocusY { get; set; } = 0.5;
    [ObservableProperty] public partial double Size { get; set; } = ThemeSlots.DefaultMascotSize;
    [ObservableProperty] public partial bool OnLeft { get; set; }
    [ObservableProperty] public partial double Opacity { get; set; } = 1;

    /// <summary>The settings as the pack stores them (the image path is filled in when the pack is written).</summary>
    public ThemeSlot Settings => new()
    {
        Image = "",
        Dim = ShowsDim ? Dim : 0,
        Blur = ShowsBlur ? Blur : 0,
        FocusX = FocusX,
        FocusY = FocusY,
        Size = Size,
        Side = OnLeft ? "left" : "right",
        Opacity = Opacity,
    };

    /// <summary>For the preview: the blur at the preview's size (the radius is for a 1920-wide window).</summary>
    public double PreviewBlur => ShowsBlur ? Blur * ImageThemeViewModel.PreviewWidth / 1920 : 0;

    internal void Set(ThemeImages.Prepared picture, string fileName)
    {
        Picture = picture;
        var old = Preview;
        using (var stream = new MemoryStream(picture.Bytes)) Preview = Bitmap.DecodeToWidth(stream, 480);
        old?.Dispose();
        FileName = fileName;
        OnPropertyChanged(nameof(HasPicture));
        OnPropertyChanged(nameof(Settings));
    }

    [RelayCommand]
    private void Clear()
    {
        Picture = null;
        var old = Preview;
        Preview = null;
        old?.Dispose();
        FileName = "";
        OnPropertyChanged(nameof(HasPicture));
        OnPropertyChanged(nameof(Settings));
        _owner.PicturesChanged();
    }

    partial void OnDimChanged(double value) => OnPropertyChanged(nameof(Settings));
    partial void OnBlurChanged(double value)
    {
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(PreviewBlur));
    }
    partial void OnFocusXChanged(double value) => OnPropertyChanged(nameof(Settings));
    partial void OnFocusYChanged(double value) => OnPropertyChanged(nameof(Settings));
    partial void OnSizeChanged(double value) => OnPropertyChanged(nameof(Settings));
    partial void OnOnLeftChanged(bool value) => OnPropertyChanged(nameof(Settings));
    partial void OnOpacityChanged(double value) => OnPropertyChanged(nameof(Settings));
}

/// <summary>A colour picked from the pictures that can be the accent.</summary>
public sealed partial class SwatchItem(uint color, ImageThemeViewModel owner) : ObservableObject
{
    public uint Color { get; } = color;
    public IBrush Brush { get; } = new SolidColorBrush(Avalonia.Media.Color.FromUInt32(color));
    public string Name => ThemePackReader.FormatColor(Color);
    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Pick() => owner.PickAccent(this);
}

/// <summary>
/// Settings › Appearance › Make a theme from pictures (design doc "Image themes"): up to four pictures in their slots,
/// colours picked from them, a live preview, then [Use it] (install and switch) or [Save as .jaytheme to share].
/// Everything stays on this computer: the pictures are read from the files the user picked.
/// </summary>
public sealed partial class ImageThemeViewModel : ObservableObject
{
    /// <summary>Width of the preview window, for scaling the blur.</summary>
    public const double PreviewWidth = 360;

    private readonly Func<string, ThemePack> _install;

    /// <param name="install">Installs a written pack and switches to it (the settings page's import).</param>
    public ImageThemeViewModel(Func<string, ThemePack> install)
    {
        _install = install;
        Slots = new(ThemeSlots.All.Select(s => new ImageSlotItem(this, s)));
        foreach (var slot in Slots) slot.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ImageSlotItem.Settings)) OnPropertyChanged(nameof(Slots)); };
        Name = L.T("theme.image.default_name");
        UpdatePalette();
    }

    public ObservableCollection<ImageSlotItem> Slots { get; }
    public ImageSlotItem Background => Slots[0];
    public ImageSlotItem Sidebar => Slots[1];
    public ImageSlotItem Mascot => Slots[2];
    public ImageSlotItem Banner => Slots[3];

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public ObservableCollection<SwatchItem> Swatches { get; } = [];

    /// <summary>The palette from the pictures, with the accent the user picked.</summary>
    public ImagePalette Palette { get; private set; } = ImageTheme.Extract([]);

    public bool HasAnyPicture => Slots.Any(s => s.HasPicture);
    public bool CanFinish => HasAnyPicture && !IsBusy && Name.Trim().Length > 0;

    // Preview colours for the variant Jay shows now.
    [ObservableProperty] public partial IBrush? PreviewPage { get; private set; }
    [ObservableProperty] public partial IBrush? PreviewSidebar { get; private set; }
    [ObservableProperty] public partial IBrush? PreviewSurface { get; private set; }
    [ObservableProperty] public partial IBrush? PreviewAccent { get; private set; }
    [ObservableProperty] public partial IBrush? PreviewText { get; private set; }
    [ObservableProperty] public partial IBrush? PreviewMuted { get; private set; }
    [ObservableProperty] public partial IBrush? PreviewSidebarText { get; private set; }

    [RelayCommand]
    private void Open()
    {
        IsOpen = true;
        UpdatePreview();
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>Puts a picture file in a slot. False (with the reason in Status) when the file can't be used.</summary>
    public bool SetPicture(string slot, string path)
    {
        if (Slots.FirstOrDefault(s => s.Slot == slot) is not { } item) return false;
        if (ThemeImages.Prepare(path, slot, out var error) is not { } picture)
        {
            Status = L.F("theme.image.not_used", Path.GetFileName(path), L.T(error ?? "theme.image.error.unreadable"));
            return false;
        }
        item.Set(picture, Path.GetFileName(path));
        Status = "";
        PicturesChanged();
        return true;
    }

    internal void PicturesChanged()
    {
        UpdatePalette();
        OnPropertyChanged(nameof(HasAnyPicture));
        OnPropertyChanged(nameof(CanFinish));
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanFinish));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanFinish));

    /// <summary>
    /// Picks colours again. The background counts twice (it is what the window looks like); the mascot counts least,
    /// since it is small on screen.
    /// </summary>
    private void UpdatePalette()
    {
        var pixels = new List<uint>();
        foreach (var slot in Slots)
            if (slot.Picture is { } p)
                for (var i = 0; i < (slot.Slot == ThemeSlots.Background ? 2 : 1); i++) pixels.AddRange(p.Thumbnail);
        var keep = Swatches.FirstOrDefault(s => s.IsSelected)?.Color;
        var palette = ImageTheme.Extract(pixels.ToArray());
        Swatches.Clear();
        foreach (var c in palette.Swatches) Swatches.Add(new SwatchItem(c, this));
        var selected = Swatches.FirstOrDefault(s => s.Color == keep) ?? Swatches.FirstOrDefault();
        if (selected is not null) selected.IsSelected = true;
        Palette = palette with { Accent = selected?.Color ?? palette.Accent };
        UpdatePreview();
    }

    internal void PickAccent(SwatchItem swatch)
    {
        foreach (var s in Swatches) s.IsSelected = ReferenceEquals(s, swatch);
        Palette = Palette with { Accent = swatch.Color };
        UpdatePreview();
    }

    /// <summary>Called when Jay switches between dark and light, so the preview shows the variant in use.</summary>
    public void UpdatePreview()
    {
        var dark = Avalonia.Application.Current?.ActualThemeVariant != Avalonia.Styling.ThemeVariant.Light;
        var (darkColors, lightColors) = ImageTheme.Colors(Palette);
        var set = dark ? darkColors : lightColors;
        var defaults = dark ? ThemeDefaults.Dark : ThemeDefaults.Light;
        IBrush brush(string key) => new SolidColorBrush(Color.FromUInt32(set.TryGetValue(key, out var c) ? c : defaults[key]));
        PreviewPage = brush("PageBg");
        PreviewSidebar = brush("SidebarBg");
        PreviewSurface = brush("Surface");
        PreviewAccent = brush("Accent");
        PreviewText = brush("TextPrimary");
        PreviewMuted = brush("TextSecondary");
        PreviewSidebarText = brush("SidebarText");
    }

    private string Write(string path)
    {
        var slots = Slots.Where(s => s.Picture is not null)
            .ToDictionary(s => s.Slot, s => new ImageThemeSlot(s.Settings, s.Picture!.Bytes, s.Picture.Extension));
        var name = Name.Trim();
        ImageTheme.Write(path, ImageTheme.IdFor(name), name, Palette, slots);
        return name;
    }

    /// <summary>[Use it]: writes the pack, installs it and switches to it.</summary>
    [RelayCommand]
    private void Apply()
    {
        if (!CanFinish) return;
        IsBusy = true;
        var temp = Path.Combine(Path.GetTempPath(), "jay-image-theme-" + Guid.NewGuid().ToString("N") + ThemePackReader.Extension);
        try
        {
            var name = Write(temp);
            _install(temp);
            Status = L.F("theme.image.applied", name);
        }
        catch (Exception e) when (e is ThemePackException or IOException or UnauthorizedAccessException)
        {
            Status = L.F("theme.image.failed", e is ThemePackException t ? t.Code : e.Message);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
            IsBusy = false;
        }
    }

    /// <summary>[Save as .jaytheme to share]: the view asks where, then this writes an ordinary theme pack there.</summary>
    public void SaveAs(string path)
    {
        if (!CanFinish) return;
        try
        {
            Write(path);
            Status = L.F("theme.image.saved", Path.GetFileName(path));
        }
        catch (Exception e) when (e is ThemePackException or IOException or UnauthorizedAccessException)
        {
            Status = L.F("theme.image.failed", e is ThemePackException t ? t.Code : e.Message);
        }
    }
}
