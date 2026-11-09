using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

public class ConsoleHistoryTests
{
    [Fact]
    public void UpAndDownWalkTheHistory()
    {
        var h = new ConsoleHistory();
        Assert.Null(h.Previous());
        h.Add("list");
        h.Add("  ");
        h.Add("say hi");
        h.Add("say hi");
        Assert.Equal(["list", "say hi"], h.Lines);
        Assert.Equal("say hi", h.Previous());
        Assert.Equal("list", h.Previous());
        Assert.Equal("list", h.Previous());
        Assert.Equal("say hi", h.Next());
        Assert.Equal("", h.Next());
        Assert.Null(h.Next());
    }

    [Fact]
    public void KeepsOnlyTheNewest()
    {
        var h = new ConsoleHistory(2);
        h.Add("a");
        h.Add("b");
        h.Add("c");
        Assert.Equal(["b", "c"], h.Lines);
    }
}
