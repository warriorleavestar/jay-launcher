using Amber.App.Views.Controls;
using Avalonia.Input;

namespace Amber.Core.Tests;

public class EnterSubmitTests
{
    [Fact]
    public void PlainEnterInSingleLineBoxSubmits() =>
        Assert.True(EnterSubmit.ShouldSubmit(Key.Enter, KeyModifiers.None, acceptsReturn: false));

    [Theory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Shift)]
    [InlineData(KeyModifiers.Alt)]
    public void EnterWithModifierDoesNotSubmit(KeyModifiers modifiers) =>
        Assert.False(EnterSubmit.ShouldSubmit(Key.Enter, modifiers, acceptsReturn: false));

    [Fact]
    public void MultiLineBoxKeepsEnterForNewLines() =>
        Assert.False(EnterSubmit.ShouldSubmit(Key.Enter, KeyModifiers.None, acceptsReturn: true));

    [Theory]
    [InlineData(Key.ImeProcessed)]
    [InlineData(Key.Tab)]
    [InlineData(Key.Space)]
    public void OtherKeysDoNotSubmit(Key key) =>
        Assert.False(EnterSubmit.ShouldSubmit(key, KeyModifiers.None, acceptsReturn: false));
}
