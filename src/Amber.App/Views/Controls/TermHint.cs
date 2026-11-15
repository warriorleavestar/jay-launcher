using Amber.App.Localization;
using Amber.Core.Help;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Amber.App.Views.Controls;

/// <summary>
/// "What is this?" (official design §6.24): a small "?" next to a word a newcomer may not know. Hovering or keyboard
/// focus shows one plain sentence as a tooltip; clicking (or Enter / Space) opens the same sentence with "Learn more ›",
/// which goes to the help article. Screen readers hear "What is X?" as the name and the sentence as help text.
/// Usage: <c>&lt;c:TermHint Term="relay" /&gt;</c>, with an id from <see cref="Terms"/>.
/// </summary>
public sealed class TermHint : Button
{
    public static readonly StyledProperty<string?> TermProperty = AvaloniaProperty.Register<TermHint, string?>(nameof(Term));

    /// <summary>Where "Learn more" goes; the main view model sets it to the shell's router.</summary>
    public static Action<string>? Navigate { get; set; }

    private readonly TextBlock _flyoutTitle = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _flyoutText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _learnMore = new() { Classes = { "ghost" }, Padding = new Thickness(0, 2), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Flyout _flyout;

    static TermHint() => TermProperty.Changed.AddClassHandler<TermHint>((h, _) => h.Update());

    public TermHint()
    {
        Classes.Add("term-hint");
        Content = new Border
        {
            Classes = { "term-hint-dot" },
            Child = new TextBlock { Text = "?", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        _learnMore.Click += (_, _) =>
        {
            _flyout!.Hide();
            if (Terms.Get(Term) is { } t) Navigate?.Invoke("help/" + t.Article);
        };
        _flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new StackPanel { Spacing = 6, MaxWidth = 320, Children = { _flyoutTitle, _flyoutText, _learnMore } },
        };
        Flyout = _flyout;
        ToolTip.SetShowDelay(this, 500);
        // Keyboard focus shows the sentence right away (§5 Tooltip: "hover 500 ms, or at once on keyboard focus").
        GotFocus += (_, e) => { if (e.NavigationMethod == NavigationMethod.Tab) ToolTip.SetIsOpen(this, true); };
        LostFocus += (_, _) => ToolTip.SetIsOpen(this, false);
        LanguageListeners.Add(this, h => ((TermHint)h).Update());
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public string? Term
    {
        get => GetValue(TermProperty);
        set => SetValue(TermProperty, value);
    }

    /// <summary>Opens the click card, as a click would (for the self-check).</summary>
    public void OpenCard() => _flyout.ShowAt(this);

    public bool IsCardOpen => _flyout.IsOpen;

    /// <summary>Follows "Learn more", as a click on it would (for the self-check).</summary>
    public void LearnMore() => _learnMore.RaiseEvent(new RoutedEventArgs(ClickEvent));

    private void Update()
    {
        if (Terms.Get(Term) is not { } t)
        {
            IsVisible = false;
            return;
        }
        IsVisible = true;
        var name = L.T(t.NameKey);
        var hint = L.T(t.HintKey);
        ToolTip.SetTip(this, hint);
        AutomationProperties.SetName(this, L.F("term.what_is", name));
        AutomationProperties.SetHelpText(this, hint);
        _flyoutTitle.Text = name;
        _flyoutText.Text = hint;
        _learnMore.Content = L.T("term.learn_more");
    }
}
