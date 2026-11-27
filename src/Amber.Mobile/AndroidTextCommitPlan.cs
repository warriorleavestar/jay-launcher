using System.Text;

namespace Amber.Mobile;

internal enum AndroidTextCommitKind { Text, Enter, Tab }

internal readonly record struct AndroidTextCommitAction(AndroidTextCommitKind Kind, string Text = "");

/// <summary>
/// Converts an IME CommitText payload into the text and functional-key events Minecraft expects.
/// Some Android keyboards submit chat with a newline instead of PerformEditorAction; forwarding
/// that newline as a character leaves the message in the editor without sending it.
/// </summary>
internal static class AndroidTextCommitPlan
{
    public static IReadOnlyList<AndroidTextCommitAction> Create(IEnumerable<Rune> runes)
    {
        var actions = new List<AndroidTextCommitAction>();
        var text = new StringBuilder();
        var previousWasCarriageReturn = false;

        void FlushText()
        {
            if (text.Length == 0) return;
            actions.Add(new(AndroidTextCommitKind.Text, text.ToString()));
            text.Clear();
        }

        foreach (var rune in runes)
        {
            if (rune.Value == '\n')
            {
                if (!previousWasCarriageReturn)
                {
                    FlushText();
                    actions.Add(new(AndroidTextCommitKind.Enter));
                }
                previousWasCarriageReturn = false;
                continue;
            }

            if (rune.Value == '\r')
            {
                FlushText();
                actions.Add(new(AndroidTextCommitKind.Enter));
                previousWasCarriageReturn = true;
                continue;
            }

            previousWasCarriageReturn = false;
            if (rune.Value == '\t')
            {
                FlushText();
                actions.Add(new(AndroidTextCommitKind.Tab));
                continue;
            }

            text.Append(rune.ToString());
        }

        FlushText();
        return actions;
    }
}
