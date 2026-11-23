using Amber.App.Services;

namespace Amber.Core.Tests;

public sealed class PackSelectionTests
{
    [Fact]
    public async Task ContentDocumentIsCopiedOnceAndRemovedAfterUse()
    {
        var reads = 0;
        var input = new MemoryStream("pack"u8.ToArray());
        string copy;
        using (var selection = new PackSelection("test.mrpack", null, () =>
        {
            reads++;
            return Task.FromResult<Stream>(input);
        }))
        {
            copy = await selection.GetPathAsync();
            Assert.Equal("pack", await File.ReadAllTextAsync(copy));
            Assert.Equal(copy, await selection.GetPathAsync());
            Assert.Equal(1, reads);
        }
        Assert.False(File.Exists(copy));
        Assert.False(input.CanRead);
    }

    [Fact]
    public async Task LocalSelectionPreservesOriginal()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var selection = new PackSelection(path))
                Assert.Equal(path, await selection.GetPathAsync());
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }
}
