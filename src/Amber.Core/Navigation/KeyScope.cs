namespace Amber.Core.Navigation;

/// <summary>Where a shortcut works (official design §8.5, "范围" column).</summary>
public enum KeyScope
{
    /// <summary>Anywhere, even while typing: it has a modifier no text box uses (Ctrl+K, Ctrl+J, F1, F6, Ctrl+1…).</summary>
    Global,
    /// <summary>Anywhere except while typing, where the text box owns the key (Ctrl+Z, Ctrl+V, Ctrl+Enter, Ctrl+N).</summary>
    OutsideText,
    /// <summary>Only while a table or list has focus, and never while typing (Space, E, U, Delete, B, Ctrl+A).</summary>
    List,
}

/// <summary>What has the keyboard right now.</summary>
/// <param name="InText">Focus is in something you type into (a text box, an editable combo box, a number box).</param>
/// <param name="InList">Focus is on a table or list row.</param>
/// <param name="Composing">An input method is composing (pinyin, kana…): its keys belong to the input method.</param>
public readonly record struct KeyContext(bool InText, bool InList, bool Composing);

/// <summary>
/// The input-method rule from §8.5: no shortcut takes a key from a text box (Ctrl+Z there is the text box's undo), single
/// keys work only on a focused table or list, and nothing fires while an input method is composing, so Enter or Space
/// that confirms a pinyin word never submits or ticks anything.
/// </summary>
public static class KeyScopes
{
    public static bool Allows(KeyScope scope, KeyContext context) => !context.Composing && scope switch
    {
        KeyScope.Global => true,
        KeyScope.OutsideText => !context.InText,
        KeyScope.List => context.InList && !context.InText,
        _ => false,
    };
}
