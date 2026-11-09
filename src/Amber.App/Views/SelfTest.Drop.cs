using System.IO.Compression;
using Amber.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=drop (UI batch 6d): drag-and-drop import over the whole window, with synthetic drag events on
/// the real window. Zones per kind, hover, dropping on a zone or beside it, mods / resource packs / worlds into a
/// throwaway instance in the report folder, the vanilla and "unknown file" notes, and a modpack opening the
/// new-instance preview. No network.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task DropImportAsync()
    {
        var service = main.Service;
        var drop = main.Drop;
        var originalDir = service.Settings.GameDirectory;
        var originalMeta = service.Settings.Instances;
        var game = Path.Combine(reportDir, "dd-game");
        var files = Path.Combine(reportDir, "dd-files");
        const string id = "fabric-1.20.1";
        try
        {
            WriteLibraryVersion(game, "1.20.1", "[]");
            var dir = Path.Combine(game, "versions", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".json"),
                $$"""{"id":"{{id}}","inheritsFrom":"1.20.1","type":"release","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.9"}]}""");
            new Amber.Core.Launch.InstanceSettings { Isolate = true }.Save(new Amber.Core.Game.GameDirectory(game), id);
            service.Settings.Instances = [];
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            service.NotifyInstalledChanged();
            service.SetCurrentInstance(id);
            main.Shell.Navigate("library");

            Directory.CreateDirectory(files);
            var jar = Path.Combine(files, "sodium.jar");
            File.WriteAllText(jar, "jar");
            var pack = MakeZip("faithful.zip", ("pack.mcmeta", "{}"), ("assets/minecraft/a.png", ""));
            var world = MakeZip("castle.zip", ("Castle/level.dat", ""), ("Castle/region/r.0.0.mca", ""));
            var unknown = Path.Combine(files, "notes.txt");
            File.WriteAllText(unknown, "x");
            var mrpack = MakeZip("pack.mrpack", ("modrinth.index.json", """{"formatVersion":1,"game":"minecraft","versionId":"1","name":"Test pack","files":[],"dependencies":{"minecraft":"1.20.1"}}"""));

            window.Width = 1440;
            window.Height = 900;
            await Task.Delay(400);

            // Mods + a resource pack + something unknown: two zones and a note.
            await Enter(jar, pack, unknown);
            Check("拖进来：认出模组和资源包，给两个落点；认不出的写一句", drop.IsActive && drop.Targets.Count == 2
                && drop.Targets[0].Label.Contains(id) && drop.Targets[1].Kind == Amber.Core.Content.DropKind.ResourcePack && drop.HasNote,
                string.Join(" | ", drop.Targets.Select(t => t.Label)) + " / " + drop.Note);
            var modZone = await ZoneCenter(0);
            Over(modZone);
            Check("拖到落点上：这个落点高亮", drop.Targets[0].IsHovered && !drop.Targets[1].IsHovered);
            await ShotAsync("drop-1440");
            window.Width = 600;
            window.Height = 640;
            await Task.Delay(500);
            Check("窄窗口：落点竖着排", Dispatcher.UIThread.Invoke(() =>
            {
                var zones = Zones();
                return zones.Count == 2 && zones[1].TranslatePoint(default, window)!.Value.Y > zones[0].TranslatePoint(default, window)!.Value.Y + 10;
            }));
            await ShotAsync("drop-600");
            window.Width = 1440;
            window.Height = 900;
            await Task.Delay(400);

            // Dropping beside the zones does nothing.
            await Drop(new Point(20, 20), jar, pack, unknown);
            var mods = Path.Combine(dir, "mods");
            Check("放在落点外面：什么都不做，浮层收起", !drop.IsActive && !Directory.Exists(mods));

            await Enter(jar, pack, unknown);
            await Drop(await ZoneCenter(0), jar, pack, unknown);
            Check("放到【装到 实例】：模组复制进实例的 mods", File.Exists(Path.Combine(mods, "sodium.jar")) && !drop.IsActive);
            await Enter(jar);
            await Drop(await ZoneCenter(0), jar);
            Check("同名再放一次：不覆盖，另存一份", File.Exists(Path.Combine(mods, "sodium (2).jar")));
            await Enter(pack);
            await Drop(await ZoneCenter(0), pack);
            Check("资源包进 resourcepacks", File.Exists(Path.Combine(dir, "resourcepacks", "faithful.zip")));
            await Enter(world);
            await Drop(await ZoneCenter(0), world);
            Check("存档 zip：导入为这个实例的世界", Directory.Exists(Path.Combine(dir, "saves", "Castle")) && File.Exists(Path.Combine(dir, "saves", "Castle", "level.dat")));

            // A vanilla instance can't take mods.
            service.SetCurrentInstance("1.20.1");
            await Enter(jar);
            Check("原版实例：模组没有落点，写着要先装加载器", drop.Targets.Count == 0 && drop.Note.Contains("1.20.1"), drop.Note);
            await Drop(new Point(20, 20), jar);
            service.SetCurrentInstance(id);

            // A modpack opens the new-instance panel with the preview.
            await Enter(mrpack);
            Check("整合包：给【导入成新实例】", drop.Targets.Count == 1 && drop.Targets[0].Kind == Amber.Core.Content.DropKind.Modpack);
            await Drop(await ZoneCenter(0), mrpack);
            var download = Page<DownloadViewModel>();
            Check("放下后打开新建实例的整合包预览", await WaitAsync(() => main.CurrentPage is DownloadViewModel && download.IsPackFileSource, 5));
            await ShotAsync("drop-modpack-preview");
        }
        finally
        {
            drop.End();
            service.Settings.Instances = originalMeta;
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
            service.NotifyInstalledChanged();
        }

        string MakeZip(string name, params (string Entry, string Text)[] entries)
        {
            var path = Path.Combine(files, name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (entry, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write(text);
            }
            return path;
        }

        List<Border> Zones() => window.DropTargets.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("drop-target")).ToList();

        async Task<Point> ZoneCenter(int index)
        {
            await Task.Delay(300);
            return Dispatcher.UIThread.Invoke(() =>
            {
                var zone = Zones()[index];
                var origin = zone.TranslatePoint(default, window) ?? default;
                return new Point(origin.X + zone.Bounds.Width / 2, origin.Y + zone.Bounds.Height / 2);
            });
        }

        async Task<DataTransfer> Data(string[] paths)
        {
            var data = new DataTransfer();
            foreach (var p in paths)
                if (await window.StorageProvider.TryGetFileFromPathAsync(new Uri(p)) is { } file) data.Add(DataTransferItem.CreateFile(file));
            return data;
        }

        async Task Enter(params string[] paths)
        {
            var data = await Data(paths);
            Dispatcher.UIThread.Invoke(() => window.RaiseEvent(new DragEventArgs(DragDrop.DragEnterEvent, data, window, new Point(10, 10), KeyModifiers.None)));
            await Task.Delay(200);
        }

        void Over(Point at) => Dispatcher.UIThread.Invoke(() =>
            window.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, new DataTransfer(), window, at, KeyModifiers.None)));

        async Task Drop(Point at, params string[] paths)
        {
            var data = await Data(paths);
            Dispatcher.UIThread.Invoke(() => window.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, window, at, KeyModifiers.None)));
            await Task.Delay(300);
        }
    }
}
