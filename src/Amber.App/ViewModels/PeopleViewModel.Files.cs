using Amber.Core.Multiplayer.Files;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// Files in a friend's conversation (redesign batch 12): the paperclip menu (a file, a world, a mod list, a screenshot,
/// a log, a folder) and dropping files on the conversation. A friend who is not in the room with me gets the file later:
/// it waits on this computer (Settings › Multiplayer › Files can turn that off).
/// </summary>
public sealed partial class PeopleViewModel
{
    /// <summary>The system picker for [Choose a file] / [Folder] (the page sets it; true asks for a folder).</summary>
    internal Func<bool, Task<string?>>? PickPath { get; set; }

    /// <summary>The paperclip is there for every friend: in the same room it sends now, otherwise the file waits.</summary>
    public bool CanAttach => HasSelected && Selected is { NeedsLook: false };

    private FileRecipient? SelectedRecipient => Selected is { } row ? new FileRecipient(row.Name, row.Friend, null) : null;

    /// <summary>A paperclip menu item: "file" and "folder" pick and send at once; the rest open the panel with that kind.</summary>
    [RelayCommand]
    private async Task AttachAsync(string? kind)
    {
        if (!CanAttach || SelectedRecipient is not { } to) return;
        switch (kind)
        {
            case "file" or "folder":
                if (PickPath is { } pick && await pick(kind == "folder") is { } path)
                    await _multiplayer.SendFileThingAsync(to, new FileSource(kind == "folder" ? FileKind.Folder : FileKind.File, path));
                break;
            default:
                _multiplayer.OpenSendPanel(to, kind switch
                {
                    "world" => FileKind.World,
                    "mods" => FileKind.ModList,
                    "screenshot" => FileKind.Screenshot,
                    "log" => FileKind.Log,
                    _ => FileKind.File,
                });
                break;
        }
    }

    /// <summary>Files dropped on the open conversation go to this friend; null when no friend is open.</summary>
    internal (string Name, Func<IReadOnlyList<string>, Task> Send)? DropRecipient()
    {
        if (!CanAttach || SelectedRecipient is not { } to) return null;
        return (to.DisplayName, paths => SendDroppedAsync(to, paths));
    }

    internal async Task SendDroppedAsync(FileRecipient to, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            var kind = Directory.Exists(path) ? FileKind.Folder : FileKind.File;
            await _multiplayer.SendFileThingAsync(to, new FileSource(kind, path));
        }
    }
}
