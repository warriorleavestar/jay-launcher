using Amber.Core.Layout;
using Avalonia.Controls;

namespace Amber.App.Services;

/// <summary>
/// Applies the density (standard / compact) to Jay's windows: compact sets a few size tokens on each window's own
/// resources (above the theme pack's values) and the <c>density-compact</c> class; standard removes them so the theme's
/// sizes apply again. Safety surfaces bring their own tokens, so they are never compacted.
/// </summary>
public static class DensityService
{
    private static readonly List<WeakReference<Window>> Windows = [];

    public static string Mode { get; private set; } = Density.Standard;

    public static event Action? Changed;

    public static void Attach(Window window)
    {
        lock (Windows) Windows.Add(new WeakReference<Window>(window));
        ApplyTo(window);
    }

    public static void Apply(string? mode)
    {
        Mode = Density.Normalize(mode);
        List<Window> live;
        lock (Windows)
        {
            Windows.RemoveAll(w => !w.TryGetTarget(out _));
            live = [.. Windows.Select(w => w.TryGetTarget(out var t) ? t : null).OfType<Window>()];
        }
        foreach (var w in live) ApplyTo(w);
        Changed?.Invoke();
    }

    /// <summary>Fluent control paddings compact density tightens (besides the size tokens in <see cref="Density"/>).</summary>
    private static readonly (string Key, object Value)[] CompactControls =
    [
        ("ButtonPadding", new Avalonia.Thickness(8, 2, 8, 3)),
        ("ComboBoxPadding", new Avalonia.Thickness(10, 2, 0, 3)),
        ("ComboBoxMinHeight", 26.0),
        ("TextControlThemePadding", new Avalonia.Thickness(8, 3, 6, 3)),
        ("CheckBoxMinHeight", 26.0),
    ];

    /// <summary>Every resource key density may override (safety surfaces pin these to the standard values).</summary>
    public static IEnumerable<string> Keys => Density.Overrides(Density.Compact).Keys.Concat(CompactControls.Select(c => c.Key));

    private static void ApplyTo(Window window)
    {
        foreach (var key in Keys) window.Resources.Remove(key);
        if (Mode == Density.Compact)
        {
            foreach (var (key, value) in Density.Overrides(Mode)) window.Resources[key] = value;
            foreach (var (key, value) in CompactControls) window.Resources[key] = value;
        }
        window.Classes.Set("density-compact", Mode == Density.Compact);
    }
}
