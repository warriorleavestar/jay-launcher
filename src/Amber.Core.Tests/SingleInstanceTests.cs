using Amber.App.Services;

namespace Amber.Core.Tests;

/// <summary>One Jay per data folder: a second start finds the folder taken and brings the first one forward.</summary>
public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-single-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void ASecondStartOnTheSameFolderIsRefusedAndActivatesTheFirst()
    {
        var data = Path.Combine(_root, "a");
        using var first = SingleInstance.TryAcquire(data);
        Assert.NotNull(first);
        using var asked = new ManualResetEventSlim();
        first!.ActivationRequested += asked.Set;

        Assert.Null(SingleInstance.TryAcquire(data));
        Assert.True(SingleInstance.RequestActivation(data, TimeSpan.FromSeconds(5)));
        Assert.True(asked.Wait(TimeSpan.FromSeconds(5)));

        // Another data folder runs side by side.
        using var other = SingleInstance.TryAcquire(Path.Combine(_root, "b"));
        Assert.NotNull(other);
    }

    [Fact]
    public void TheFolderIsFreeOnceTheFirstLetsGo()
    {
        var data = Path.Combine(_root, "c");
        var first = SingleInstance.TryAcquire(data);
        Assert.NotNull(first);
        var release = Task.Run(async () => { await Task.Delay(300); first!.Dispose(); });
        using var second = SingleInstance.Acquire(data, TimeSpan.FromSeconds(5));
        Assert.NotNull(second);
        release.Wait();
    }

    [Fact]
    public void RestartArgumentsCarryOneWaitPid()
    {
        Assert.Equal(1234, SingleInstance.WaitPid(["--theme-dev", "x", "--wait-pid", "1234"]));
        Assert.Null(SingleInstance.WaitPid(["--wait-pid"]));
        Assert.Null(SingleInstance.WaitPid(["--wait-pid", "nope"]));
        Assert.Equal(["--theme-dev", "x"], SingleInstance.WithoutWaitPid(["--wait-pid", "1", "--theme-dev", "x"]));
    }

    [Fact]
    public void PipeNamesFollowTheFolder()
    {
        Assert.Equal(SingleInstance.PipeNameFor(Path.Combine(_root, "a")), SingleInstance.PipeNameFor(Path.Combine(_root, "a") + Path.DirectorySeparatorChar));
        Assert.NotEqual(SingleInstance.PipeNameFor(Path.Combine(_root, "a")), SingleInstance.PipeNameFor(Path.Combine(_root, "b")));
    }
}
