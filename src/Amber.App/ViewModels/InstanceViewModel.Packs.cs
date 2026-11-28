using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Launch;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>A resource pack or shader pack file (zip or folder) in an instance.</summary>
public sealed partial class PackFileItem(string path, PackAge age = PackAge.Unknown) : ObservableObject
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public PackAge Age { get; } = age;

    public string Size
    {
        get
        {
            var bytes = File.Exists(Path) ? new FileInfo(Path).Length : Toolbox.SizeOf([Path]);
            return Fmt.Size(bytes);
        }
    }

    /// <summary>"Made for an older game" / "newer game"; shown, never blocking (official design §6.7).</summary>
    public string? AgeText => Age switch
    {
        PackAge.Older => L.T("instance.packs.older"),
        PackAge.Newer => L.T("instance.packs.newer"),
        _ => null,
    };

    public bool HasAge => AgeText is not null;

    /// <summary>On (resource packs) or the pack in use (shaders).</summary>
    [ObservableProperty]
    public partial bool IsOn { get; set; }
}

/// <summary>
/// Instance › Resource packs and Shaders (official design §6.7). Resource packs come in two lists: on (top = highest
/// priority, the order the game applies them) and off. The game's own options.txt holds the list, so the in-game screen
/// and Jay always agree. Shaders: pick the one Iris / Oculus / OptiFine uses, or none.
/// </summary>
public sealed partial class InstanceViewModel
{
    /// <summary>Packs that are on, highest priority first.</summary>
    public ObservableCollection<PackFileItem> EnabledPacks { get; } = [];

    /// <summary>Packs in the folder that are off, by name.</summary>
    public ObservableCollection<PackFileItem> DisabledPacks { get; } = [];

    public ObservableCollection<PackFileItem> ShaderPacks { get; } = [];

    public bool HasResourcePacks => EnabledPacks.Count + DisabledPacks.Count > 0;
    public bool HasEnabledPacks => EnabledPacks.Count > 0;
    public bool HasDisabledPacks => DisabledPacks.Count > 0;
    public bool HasShaderPacks => ShaderPacks.Count > 0;

    /// <summary>False when nothing that runs shaders is installed: the tab shows what to install first.</summary>
    [ObservableProperty]
    public partial bool HasShaderLoader { get; set; } = true;

    /// <summary>"Shaders off" is the current choice.</summary>
    public bool ShadersOff => ShaderPacks.All(p => !p.IsOn);

    private static IEnumerable<string> PackEntries(string dir) =>
        !System.IO.Directory.Exists(dir) ? [] :
        System.IO.Directory.EnumerateFileSystemEntries(dir)
            .Where(p => System.IO.Directory.Exists(p) || p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);

    internal void LoadResourcePacks()
    {
        EnabledPacks.Clear();
        DisabledPacks.Clear();
        if (Current is { } c)
        {
            var present = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var path in PackEntries(c.ResourcePacksDir)) present[Path.GetFileName(path)] = path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            var on = ResourcePackOptions.Enabled(c.WorkDir);
            PackFileItem Item(string path) => new(path, PackFormat.Age(PackFormat.Read(path), c.McVersion));
            foreach (var name in on)
                if (present.Remove(name, out var path))
                {
                    var item = Item(path);
                    item.IsOn = true;
                    EnabledPacks.Add(item);
                }
            foreach (var path in present.Values.Order(StringComparer.OrdinalIgnoreCase)) DisabledPacks.Add(Item(path));
        }
        OnPacksChanged();
    }

    private void OnPacksChanged()
    {
        OnPropertyChanged(nameof(HasResourcePacks));
        OnPropertyChanged(nameof(HasEnabledPacks));
        OnPropertyChanged(nameof(HasDisabledPacks));
    }

    /// <summary>Writes the "on" list back to options.txt in its current order.</summary>
    private void SavePackOrder()
    {
        if (Current is not { } c) return;
        try
        {
            // With the game version, packs made for another version are also marked as accepted, or the game drops them.
            ResourcePackOptions.SetEnabled(c.WorkDir, EnabledPacks.Select(p => p.Name).ToList(), c.McVersion);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusMessage.Failure("instance.packs.save_failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>On ↔ off. A pack turned on goes to the top (highest priority), like the game's own screen.</summary>
    [RelayCommand]
    private void TogglePack(PackFileItem? item)
    {
        if (item is null) return;
        if (EnabledPacks.Remove(item))
        {
            item.IsOn = false;
            var at = 0;
            while (at < DisabledPacks.Count && string.Compare(DisabledPacks[at].Name, item.Name, StringComparison.OrdinalIgnoreCase) < 0) at++;
            DisabledPacks.Insert(at, item);
        }
        else if (DisabledPacks.Remove(item))
        {
            item.IsOn = true;
            EnabledPacks.Insert(0, item);
        }
        else return;
        SavePackOrder();
        OnPacksChanged();
    }

    [RelayCommand]
    private void MovePackUp(PackFileItem? item) => MovePack(item, -1);

    [RelayCommand]
    private void MovePackDown(PackFileItem? item) => MovePack(item, 1);

    /// <summary>Up = higher priority. Stops at the ends.</summary>
    public bool MovePack(PackFileItem? item, int delta)
    {
        if (item is null) return false;
        var i = EnabledPacks.IndexOf(item);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= EnabledPacks.Count) return false;
        EnabledPacks.Move(i, j);
        SavePackOrder();
        return true;
    }

    internal void LoadShaderPacks()
    {
        ShaderPacks.Clear();
        if (Current is { } c)
        {
            HasShaderLoader = ShaderSelection.HasLoader(c.WorkDir);
            var current = ShaderSelection.Current(c.WorkDir);
            try
            {
                foreach (var path in PackEntries(c.ShaderPacksDir))
                    ShaderPacks.Add(new PackFileItem(path) { IsOn = Path.GetFileName(path) == current });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        OnPropertyChanged(nameof(HasShaderPacks));
        OnPropertyChanged(nameof(ShadersOff));
    }

    /// <summary>Uses this shader pack (null: shaders off). Takes effect the next time the game starts.</summary>
    [RelayCommand]
    private void UseShader(PackFileItem? item)
    {
        if (Current is not { } c) return;
        try
        {
            ShaderSelection.Select(c.WorkDir, item?.Name);
            foreach (var p in ShaderPacks) p.IsOn = p == item;
            OnPropertyChanged(nameof(ShadersOff));
            SetStatus(item is null ? StatusMessage.Success("instance.shaders.off_done") : StatusMessage.Success("instance.shaders.used", item.Name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusMessage.Failure("instance.packs.save_failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>[Install Iris]: Discover on mods for this instance, searching for Iris (Oculus on Forge / NeoForge).</summary>
    [RelayCommand]
    private void InstallShaderLoader()
    {
        if (Current is { } c) NavigateToContent?.Invoke(c.Id, ContentType.Mod, ShaderLoaderQuery(c.Loader));
    }

    internal static string ShaderLoaderQuery(string? loader) => loader is "forge" or "neoforge" ? "Oculus" : "Iris";

    private void ReloadPackFiles()
    {
        LoadResourcePacks();
        LoadShaderPacks();
    }

    [RelayCommand]
    private void OpenResourcePacksFolder()
    {
        if (Current is { } c) OpenPath(c.ResourcePacksDir);
    }

    [RelayCommand]
    private void OpenShaderPacksFolder()
    {
        if (Current is { } c) OpenPath(c.ShaderPacksDir);
    }

    [RelayCommand]
    private void DeletePack(PackFileItem? item)
    {
        if (item is null || !Trash(item.Path, ReloadPackFiles)) return;
        if (EnabledPacks.Remove(item)) SavePackOrder();
        DisabledPacks.Remove(item);
        ShaderPacks.Remove(item);
        OnPacksChanged();
        OnPropertyChanged(nameof(HasShaderPacks));
    }
}
