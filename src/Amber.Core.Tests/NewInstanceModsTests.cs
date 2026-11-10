using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>"Add mods while you're at it" in the new-instance panel (§6.11, journey J3).</summary>
public sealed class NewInstanceModsTests
{
    private static ContentProject Project(string id) => new(ContentSource.Modrinth, id, id + " mod", "", null, null, 0, null);

    private static ContentFile File_(string project, string id, string mc, params ContentDependency[] deps) =>
        new(ContentSource.Modrinth, project, id, id, id + ".jar", "https://x/" + id, null, 1, [mc], ["fabric"], "release", default, deps);

    private sealed class Provider(params ContentFile[] files) : IContentProvider
    {
        public ContentSource Source => ContentSource.Modrinth;
        public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentProject> GetProjectAsync(string projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContentFile>>(files.Where(f => f.ProjectId == projectId && f.GameVersions.Contains(gameVersion!)).ToList());
        public Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct = default) => Task.FromResult(files.Single(f => f.FileId == fileId));
    }

    [Fact]
    public void LoaderIdsMatchTheContentSites()
    {
        Assert.Equal("fabric", NewInstanceMods.LoaderId(LoaderKind.Fabric));
        Assert.Equal("quilt", NewInstanceMods.LoaderId(LoaderKind.Quilt));
        Assert.Equal("forge", NewInstanceMods.LoaderId(LoaderKind.Forge));
        Assert.Equal("neoforge", NewInstanceMods.LoaderId(LoaderKind.NeoForge));
        Assert.Null(NewInstanceMods.LoaderId(LoaderKind.OptiFine));
        Assert.Null(NewInstanceMods.LoaderId(null));
    }

    [Fact]
    public async Task OnePlanForAllPicksWithSharedDependenciesOnce()
    {
        var dep = new ContentDependency(ContentSource.Modrinth, "API", null, DependencyKind.Required);
        var provider = new Provider(
            File_("A", "a1", "1.21.1", dep),
            File_("B", "b1", "1.21.1", dep),
            File_("API", "api1", "1.21.1"),
            File_("OLD", "old1", "1.16.5"));
        var installer = new ContentInstaller(null!, new Dictionary<ContentSource, IContentProvider> { [ContentSource.Modrinth] = provider });
        var instance = new GameInstance("fabric-1.21.1", "1.21.1", "fabric", "0.16", Path.GetTempPath());

        var plan = await NewInstanceMods.PlanAsync(provider, installer, [Project("A"), Project("B"), Project("OLD")], instance);

        Assert.Equal(["a1", "api1", "b1"], plan.Files.Select(f => f.FileId).Order());
        var problem = Assert.Single(plan.Problems);
        Assert.Contains("OLD mod", problem);
    }

    [Fact]
    public void PanelKeepsTicksAcrossSearchesAndDropsThemForAnotherLoader() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new DownloadViewModel(service) { IsCurrent = true };
        Assert.False(vm.CanAddMods);
        vm.SelectedLoader = vm.LoaderChoices.Single(c => c.Kind == LoaderKind.Fabric);
        vm.Target = new RemoteVersionItem(new ManifestEntry { Id = "1.21.1", Type = "release" }, installed: true);
        Assert.True(vm.CanAddMods);
        Assert.Equal(L.T("newinstance.mods.header"), vm.ModsHeader);

        vm.ShowModResults([Project("sodium"), Project("iris")]);
        vm.TogglePickCommand.Execute(vm.ModResults[0]);
        Assert.True(vm.ModResults[0].IsPicked);
        Assert.Single(vm.PickedMods);
        Assert.Contains("sodium mod", vm.ModsHeader);

        vm.ShowModResults([Project("iris"), Project("sodium")]);
        Assert.True(vm.ModResults.Single(r => r.Project.Id == "sodium").IsPicked);
        vm.TogglePickCommand.Execute(vm.ModResults.Single(r => r.Project.Id == "sodium"));
        Assert.Empty(vm.PickedMods);

        vm.TogglePickCommand.Execute(vm.ModResults[0]);
        vm.SelectedLoader = vm.LoaderChoices[0];
        Assert.False(vm.CanAddMods);
        Assert.Empty(vm.PickedMods);
        Assert.Empty(vm.ModResults);
    });
}
