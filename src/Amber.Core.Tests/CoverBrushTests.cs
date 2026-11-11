using Amber.App.Views;
using Amber.Core.GameLibrary;

namespace Amber.Core.Tests;

/// <summary>Automatic covers (§6.2, §12 review): one palette per slot, and white initials readable on every colour.</summary>
public sealed class CoverBrushTests
{
    [Fact]
    public void OnePalettePerSlot()
    {
        Assert.Equal(CoverArt.Slots, CoverBrush.Pairs.Length);
        Assert.Equal(CoverBrush.Pairs.Length, CoverBrush.Pairs.Select(p => p.From).Distinct().Count());
    }

    [Fact]
    public void WhiteInitialsMeetAaOnEveryColour()
    {
        foreach (var (name, from, to) in CoverBrush.Pairs)
        {
            // The lit block faces add up to 7% white; leave room for it above 4.5:1.
            Assert.True(CoverBrush.WhiteContrast(from) >= 5.5, $"{name} {from}: {CoverBrush.WhiteContrast(from):0.00}");
            Assert.True(CoverBrush.WhiteContrast(to) >= 5.5, $"{name} {to}: {CoverBrush.WhiteContrast(to):0.00}");
        }
    }
}
