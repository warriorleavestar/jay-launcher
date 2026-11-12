using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Localization;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amber.App.ViewModels;

/// <summary>One big drop zone: what happens when the files land here.</summary>
public sealed partial class DropTarget(DropKind kind, string label, string hint, Func<Task> run) : ObservableObject
{
    public DropKind Kind { get; } = kind;
    public string Label { get; } = label;
    public string Hint { get; } = hint;
    internal Func<Task> Run { get; } = run;

    /// <summary>The files are over this zone right now.</summary>
    [ObservableProperty]
    public partial bool IsHovered { get; set; }
}

/// <summary>
/// Drag-and-drop import over the whole window (official design §6.25): Jay recognises what is being dragged and shows
/// one to three big zones. Files of one kind are handled together; different kinds get their own zones. Every zone
/// has a button path elsewhere (import modpack, add mods from a file…) for people who don't drag.
/// </summary>
public sealed partial class DropViewModel(LauncherService service) : ObservableObject
{
    /// <summary>Opens the new-instance panel with a modpack file previewed (MainViewModel).</summary>
    public Func<string, Task>? PreviewPack { get; set; }

    /// <summary>Imports a theme pack (the settings page's import).</summary>
    public Action<string>? ImportTheme { get; set; }

    /// <summary>Opens Settings › Appearance › Make a theme from pictures with this picture in this slot.</summary>
    public Action<string, string>? PictureForTheme { get; set; }

    /// <summary>
    /// A friend's conversation is open: the one zone sends what is dropped to them (any kind of file or folder). Null when
    /// no conversation is open, so the import zones show as usual.
    /// </summary>
    public Func<(string Name, Func<IReadOnlyList<string>, Task> Send)?>? SendTarget { get; set; }

    /// <summary>Something dragged is over the window.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public ObservableCollection<DropTarget> Targets { get; } = [];

    /// <summary>"Jay doesn't recognise this file" or why nothing can take it right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string Note { get; set; } = "";

    public bool HasNote => Note.Length > 0;

    /// <summary>Files entered the window: work out the zones.</summary>
    public void Begin(IReadOnlyList<string> paths)
    {
        Targets.Clear();
        if (paths.Count > 0 && SendTarget?.Invoke() is { } send)
        {
            var all = paths.ToList();
            Targets.Add(new DropTarget(DropKind.Send, L.F("drop.send", send.Name), L.T("drop.send.hint"), () => send.Send(all)));
            Note = "";
            IsActive = true;
            return;
        }
        var notes = new List<string>();
        foreach (var group in DropClassifier.Group(paths))
        {
            if (TargetsFor(group, notes) is { } targets)
                foreach (var t in targets) Targets.Add(t);
        }
        Note = string.Join("\n", notes);
        IsActive = paths.Count > 0;
    }

    public void End()
    {
        IsActive = false;
        foreach (var t in Targets) t.IsHovered = false;
    }

    public void Hover(DropTarget? target)
    {
        foreach (var t in Targets) t.IsHovered = ReferenceEquals(t, target);
    }

    /// <summary>The files landed on a zone (null: outside every zone, nothing happens).</summary>
    public async Task DropAsync(DropTarget? target)
    {
        End();
        if (target is null) return;
        try
        {
            await target.Run();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Toasts.Show(L.F("drop.failed", ErrorText.Of(e)), NotificationType.Error);
        }
    }

    private GameInstance? Current()
    {
        if (service.CurrentInstance is not { } id || !service.InstalledVersions().Contains(id)) return null;
        try { return service.GetInstance(id); }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException) { return null; }
    }

    private IEnumerable<DropTarget>? TargetsFor(DropGroup group, List<string> notes)
    {
        var paths = group.Paths;
        var count = paths.Count;
        switch (group.Kind)
        {
            case DropKind.Unknown:
                notes.Add(L.F("drop.unknown", string.Join(L.T("common.list_separator"), paths.Take(3).Select(Path.GetFileName)) + (count > 3 ? "…" : "")));
                return null;
            case DropKind.Modpack:
                return [new DropTarget(group.Kind, L.T("drop.modpack"), Path.GetFileName(paths[0]), () => PreviewPack?.Invoke(paths[0]) ?? Task.CompletedTask)];
            case DropKind.Theme:
                return [new DropTarget(group.Kind, L.T("drop.theme"), Path.GetFileName(paths[0]), () =>
                {
                    ImportTheme?.Invoke(paths[0]);
                    return Task.CompletedTask;
                })];
            case DropKind.Picture:
                return Amber.Core.Themes.ThemeSlots.All.Select(slot => new DropTarget(group.Kind, L.T(slot switch
                {
                    Amber.Core.Themes.ThemeSlots.Background => "drop.picture.background",
                    Amber.Core.Themes.ThemeSlots.Sidebar => "drop.picture.sidebar",
                    Amber.Core.Themes.ThemeSlots.Mascot => "drop.picture.mascot",
                    _ => "drop.picture.banner",
                }), Path.GetFileName(paths[0]), () =>
                {
                    PictureForTheme?.Invoke(paths[0], slot);
                    return Task.CompletedTask;
                }));
        }
        if (Current() is not { } instance)
        {
            notes.Add(L.T("drop.no_instance"));
            return null;
        }
        switch (group.Kind)
        {
            case DropKind.Mod when instance.Loader is null:
                notes.Add(L.F("drop.vanilla", instance.Id));
                return null;
            case DropKind.Mod:
                return [Copy(L.F("drop.mod", instance.Id), instance.ModsDir)];
            case DropKind.ResourcePack:
                return [Copy(L.F("drop.resourcepack", instance.Id), instance.ResourcePacksDir)];
            case DropKind.Shader:
                return [Copy(L.F("drop.shader", instance.Id), instance.ShaderPacksDir)];
            case DropKind.World:
                return [new DropTarget(group.Kind, L.F("drop.world", instance.Id), Files(), () =>
                {
                    var names = paths.Select(p => WorldManager.Import(p, instance.SavesDir)).ToList();
                    Done(L.P("drop.world.done", names.Count, names.Count, instance.Id));
                    return Task.CompletedTask;
                })];
            case DropKind.Datapack:
                // A datapack belongs to one world: pick it in the instance's Worlds tab.
                notes.Add(L.F("drop.datapack", instance.Id));
                return null;
        }
        return null;

        string Files() => count == 1 ? Path.GetFileName(paths[0]) : L.P("drop.files", count, count);

        DropTarget Copy(string label, string directory) => new(group.Kind, label, Files(), () =>
        {
            foreach (var p in paths) DropClassifier.CopyInto(p, directory);
            Done(L.P("drop.copied", count, count, instance.Id));
            return Task.CompletedTask;
        });
    }

    private void Done(string text)
    {
        service.NotifyContentChanged();
        Toasts.Show(text, NotificationType.Success);
    }
}
