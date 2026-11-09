using Amber.Core.Content;

namespace Amber.Core.Tests;

public class ShaderSupportTests
{
    [Theory]
    [InlineData(null, "1.20.1", ShaderReadiness.Vanilla)]
    [InlineData("fabric", "1.20.1", ShaderReadiness.NeedsIris)]
    [InlineData("quilt", "1.21.1", ShaderReadiness.NeedsIris)]
    [InlineData("neoforge", "1.21.1", ShaderReadiness.NeedsIris)]
    [InlineData("neoforge", "1.20.1", ShaderReadiness.NeedsOculus)]
    [InlineData("forge", "1.20.1", ShaderReadiness.NeedsOculus)]
    [InlineData("forge", "1.21.4", ShaderReadiness.Unsupported)]
    [InlineData("forge", "26.1", ShaderReadiness.Unsupported)]
    [InlineData("optifine", "1.12.2", ShaderReadiness.Ready)]
    public void Check_WithoutShaderMod(string? loader, string mc, ShaderReadiness expected) =>
        Assert.Equal(expected, ShaderSupport.Check(loader, mc, ["sodium", null, "fabric-api"]));

    [Theory]
    [InlineData("fabric", "iris")]
    [InlineData("forge", "oculus")]
    [InlineData("forge", "OptiFine")]
    [InlineData("fabric", "optifabric")]
    public void Check_ShaderModMakesItReady(string loader, string modId) =>
        Assert.Equal(ShaderReadiness.Ready, ShaderSupport.Check(loader, "1.20.1", ["sodium", modId]));

    [Theory]
    [InlineData("1.20.1", false)]
    [InlineData("1.21", true)]
    [InlineData("1.21.4", true)]
    [InlineData("1.16.5", false)]
    [InlineData("26.1", true)]
    [InlineData("24w14a", true)]
    public void IsAtLeast121(string mc, bool expected) => Assert.Equal(expected, ShaderSupport.IsAtLeast121(mc));

    [Fact]
    public void ProjectFor_OnlyWhenSomethingCanBeInstalled()
    {
        Assert.Equal("iris", ShaderSupport.ProjectFor(ShaderReadiness.NeedsIris));
        Assert.Equal("oculus", ShaderSupport.ProjectFor(ShaderReadiness.NeedsOculus));
        Assert.Null(ShaderSupport.ProjectFor(ShaderReadiness.Ready));
        Assert.Null(ShaderSupport.ProjectFor(ShaderReadiness.Vanilla));
        Assert.Null(ShaderSupport.ProjectFor(ShaderReadiness.Unsupported));
    }

    [Fact]
    public async Task PlanAsync_PicksIrisForTheInstanceAndPullsInSodium()
    {
        var provider = new Provider(
            File("iris", "iris-old", "1.20.1", "fabric", "beta"),
            File("iris", "iris-rel", "1.20.1", "fabric", "release", new ContentDependency(ContentSource.Modrinth, "sodium", null, DependencyKind.Required)),
            File("iris", "iris-121", "1.21.1", "fabric", "release"),
            File("sodium", "sodium-rel", "1.20.1", "fabric", "release"));
        var installer = new ContentInstaller(null!, new Dictionary<ContentSource, IContentProvider> { [ContentSource.Modrinth] = provider });
        var instance = new GameInstance("i", "1.20.1", "fabric", "0.16", Path.GetTempPath());

        var plan = await ShaderSupport.PlanAsync(instance, ShaderReadiness.NeedsIris, provider, installer);

        Assert.NotNull(plan);
        Assert.Equal(["iris-rel", "sodium-rel"], plan.Files.Select(f => f.FileId));
    }

    [Fact]
    public async Task PlanAsync_NullWhenNothingFitsOrNothingToInstall()
    {
        var provider = new Provider(File("oculus", "oculus-1", "1.20.1", "forge", "release"));
        var installer = new ContentInstaller(null!, new Dictionary<ContentSource, IContentProvider> { [ContentSource.Modrinth] = provider });
        Assert.Null(await ShaderSupport.PlanAsync(new GameInstance("i", "1.16.5", "forge", "1", Path.GetTempPath()), ShaderReadiness.NeedsOculus, provider, installer));
        Assert.Null(await ShaderSupport.PlanAsync(new GameInstance("i", "1.20.1", null, null, Path.GetTempPath()), ShaderReadiness.Vanilla, provider, installer));
    }

    private static ContentFile File(string project, string id, string mc, string loader, string channel, params ContentDependency[] deps) =>
        new(ContentSource.Modrinth, project, id, id, id + ".jar", "https://x/" + id, null, 1, [mc], [loader], channel, default, deps);

    private sealed class Provider(params ContentFile[] files) : IContentProvider
    {
        public ContentSource Source => ContentSource.Modrinth;
        public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentProject> GetProjectAsync(string projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContentFile>>(files.Where(f => f.ProjectId == projectId && f.GameVersions.Contains(gameVersion!)
                && (loader is null || f.Loaders.Contains(loader))).ToList());
        public Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct = default) => Task.FromResult(files.Single(f => f.FileId == fileId));
    }
}
