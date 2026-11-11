using Amber.Core.Navigation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>Reads the <see cref="KeyContext"/> from the focused control, for the §8.5 input-method rule (<see cref="KeyScopes"/>).</summary>
internal static class KeyboardScope
{
    /// <summary>The context of the element that received the key (its source), which also covers popups and flyouts.</summary>
    public static KeyContext Of(KeyEventArgs e)
    {
        var focused = e.Source as Visual;
        var inText = focused is TextBox || focused?.FindAncestorOfType<TextBox>() is not null;
        var inList = focused is ListBoxItem or ListBox || focused?.FindAncestorOfType<ListBox>() is not null;
        // While an input method composes, the platform reports its keys as ImeProcessed (Windows) or with no key at all.
        var composing = e.Key is Key.None or Key.ImeProcessed or Key.ImeConvert or Key.ImeNonConvert or Key.ImeModeChange;
        return new KeyContext(inText, inList, composing);
    }

    public static bool Allows(KeyScope scope, KeyEventArgs e) => KeyScopes.Allows(scope, Of(e));
}
