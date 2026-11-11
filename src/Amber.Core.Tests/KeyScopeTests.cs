using Amber.Core.Navigation;

namespace Amber.Core.Tests;

public class KeyScopeTests
{
    [Theory]
    // scope, in text, in list, composing, allowed
    [InlineData(KeyScope.Global, false, false, false, true)]
    [InlineData(KeyScope.Global, true, false, false, true)]
    [InlineData(KeyScope.Global, true, false, true, false)]
    [InlineData(KeyScope.OutsideText, false, false, false, true)]
    [InlineData(KeyScope.OutsideText, true, false, false, false)]
    [InlineData(KeyScope.OutsideText, false, true, false, true)]
    [InlineData(KeyScope.List, false, true, false, true)]
    [InlineData(KeyScope.List, false, false, false, false)]
    [InlineData(KeyScope.List, true, true, false, false)]
    [InlineData(KeyScope.List, false, true, true, false)]
    public void FollowsTheInputMethodRule(KeyScope scope, bool inText, bool inList, bool composing, bool allowed) =>
        Assert.Equal(allowed, KeyScopes.Allows(scope, new KeyContext(inText, inList, composing)));
}
