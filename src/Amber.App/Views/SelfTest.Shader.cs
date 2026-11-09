using Amber.App.ViewModels;
using Amber.Core.Content;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=shader (old-spec gap N3): the instance's shader tab and the Discover page's shader target
/// say when Iris / Oculus / OptiFine is missing, offer [Install Iris] for Fabric, and block vanilla.
/// Uses a throwaway game directory inside the report folder (two hand-written version files); no network.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task ShaderAsync()
    {
        var service = Page<HomeViewModel>().Service;
        var originalDir = service.Settings.GameDirectory;
        var game = Path.Combine(reportDir, "game");
        try
        {
            WriteVersion(game, "shader-fabric", """[{"name":"net.fabricmc:fabric-loader:0.16.0"}]""");
            WriteVersion(game, "shader-vanilla", "[]");
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            await WaitAsync(() => service.InstalledVersions().Contains("shader-fabric"), 10);

            var instance = main.Pages.OfType<InstanceViewModel>().First();
            main.CurrentPage = instance;
            instance.SelectedId = "shader-fabric";
            await WaitAsync(() => instance.Current?.Id == "shader-fabric", 10);
            instance.TabIndex = (int)InstanceTab.ShaderPacks;
            await Task.Delay(500);
            var page = window.PageHost.GetVisualDescendants().OfType<InstancePage>().FirstOrDefault();
            Check("光影页（Fabric 没装 Iris）：提示光影不会生效", instance.ShaderState == ShaderReadiness.NeedsIris && page?.ShaderNoteBox.IsEffectivelyVisible == true,
                $"{instance.ShaderState} {instance.ShaderNote}");
            Check("光影页：有【安装 Iris】按钮", page?.InstallRendererButton.IsEffectivelyVisible == true && Equals(page.InstallRendererButton.Content, "安装 Iris"),
                $"{page?.InstallRendererButton.Content}");
            await ShotAsync("shader-fabric-needs-iris");

            var mods = instance.Current!.ModsDir;
            Directory.CreateDirectory(mods);
            using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(mods, "iris-selftest.jar"), System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()))
                writer.Write("""{"schemaVersion":1,"id":"iris","version":"1.8.0","name":"Iris"}""");
            service.NotifyContentChanged();
            await WaitAsync(() => instance.ShaderState == ShaderReadiness.Ready, 5);
            Check("光影页：装了 Iris 后提示消失", instance.ShaderState == ShaderReadiness.Ready && page?.ShaderNoteBox.IsVisible == false);

            instance.SelectedId = "shader-vanilla";
            await WaitAsync(() => instance.Current?.Id == "shader-vanilla", 10);
            instance.TabIndex = (int)InstanceTab.ShaderPacks;
            await Task.Delay(500);
            page = window.PageHost.GetVisualDescendants().OfType<InstancePage>().FirstOrDefault();
            Check("光影页（原版）：提示要换带加载器或 OptiFine 的实例，不给安装按钮",
                instance.ShaderState == ShaderReadiness.Vanilla && page?.ShaderNoteBox.IsEffectivelyVisible == true && page.InstallRendererButton.IsVisible == false,
                $"{instance.ShaderState}");
            await ShotAsync("shader-vanilla");

            var content = main.Pages.OfType<ContentViewModel>().First();
            content.TargetInstance = "shader-vanilla";
            content.TypeIndex = (int)ContentType.Shader;
            Check("发现页：往原版实例装光影时说明原因", content.ShaderState == ShaderReadiness.Vanilla && content.HasShaderNote && !content.CanInstallRenderer);
            File.Delete(Path.Combine(mods, "iris-selftest.jar"));
            content.TargetInstance = "shader-fabric";
            Check("发现页：Fabric 缺 Iris 时默认勾上“一并安装 Iris”",
                content.ShaderState == ShaderReadiness.NeedsIris && content.CanInstallRenderer && content.AlsoInstallRenderer && content.AlsoInstallRendererLabel == "一并安装 Iris",
                $"{content.ShaderState} {content.AlsoInstallRendererLabel}");
            content.TypeIndex = (int)ContentType.Mod;
            Check("发现页：不是光影时没有这条提示", content.ShaderState is null && !content.HasShaderNote);
        }
        catch (Exception e)
        {
            Check("自检没有异常", false, e.ToString());
        }
        finally
        {
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
        }
    }

    private static void WriteVersion(string root, string id, string libraries)
    {
        var dir = Path.Combine(root, "versions", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"),
            $$"""{"id":"{{id}}","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":{{libraries}}}""");
    }
}
