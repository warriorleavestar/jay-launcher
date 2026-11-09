using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// The lobby's two short lessons (official §9.5): 联机小课堂 and 安全小课, five illustrated cards each, in the same
/// drawer shape as "Connection problems?". The last card links to the full help article and to the next step
/// (the diagnostics drawer, or the drill).
/// </summary>
public sealed partial class MultiplayerViewModel
{
    [ObservableProperty] public partial bool LessonOpen { get; set; }

    [ObservableProperty] public partial LessonKind LessonKind { get; set; }

    [ObservableProperty] public partial int LessonIndex { get; set; }

    private IReadOnlyList<LessonCard> LessonCards => Lessons.Cards(LessonKind);

    private LessonCard LessonCard => LessonCards[Math.Clamp(LessonIndex, 0, LessonCards.Count - 1)];

    public string LessonHeading => L.T(LessonKind == LessonKind.Safety ? "lesson.safety.heading" : "lesson.multiplayer.heading");
    public string LessonTitle => L.T(LessonCard.Title);
    public string LessonBody => L.T(LessonCard.Body);
    public string LessonPicture => LessonCard.Picture;
    public string LessonStep => L.F("lesson.step", LessonIndex + 1, LessonCards.Count);
    public bool LessonHasPrevious => LessonIndex > 0;
    public bool LessonIsLast => LessonIndex >= LessonCards.Count - 1;
    public bool LessonHasNext => !LessonIsLast;
    public bool LessonOffersDiagnostics => LessonIsLast && LessonCard.Action == LessonAction.Diagnostics;
    public bool LessonOffersDrill => LessonIsLast && LessonCard.Action == LessonAction.Drill;

    /// <summary>Called when a card appears, so the view can move the focus to its main button.</summary>
    public Action? LessonFocusRequested { get; set; }

    /// <summary>Opens a lesson from its first card ("multiplayer" or "safety").</summary>
    [RelayCommand]
    private void OpenLesson(string? kind)
    {
        DiagnosticsOpen = false;
        DrillOpen = false;
        LessonKind = kind == "safety" ? LessonKind.Safety : LessonKind.Multiplayer;
        LessonIndex = 0;
        LessonOpen = true;
        RefreshLesson();
    }

    [RelayCommand]
    private void CloseLesson() => LessonOpen = false;

    [RelayCommand]
    private void LessonNext()
    {
        if (LessonIsLast) { LessonOpen = false; return; }
        LessonIndex++;
        RefreshLesson();
    }

    [RelayCommand]
    private void LessonPrevious()
    {
        if (LessonIndex == 0) return;
        LessonIndex--;
        RefreshLesson();
    }

    [RelayCommand]
    private void LessonArticle()
    {
        LessonOpen = false;
        OpenHelp(Lessons.Article(LessonKind));
    }

    [RelayCommand]
    private void LessonDiagnostics()
    {
        LessonOpen = false;
        DiagnosticsOpen = true;
    }

    [RelayCommand]
    private void LessonDrill()
    {
        LessonOpen = false;
        OpenDrill();
    }

    private void RefreshLesson()
    {
        foreach (var name in (string[])[nameof(LessonHeading), nameof(LessonTitle), nameof(LessonBody), nameof(LessonPicture), nameof(LessonStep),
                     nameof(LessonHasPrevious), nameof(LessonIsLast), nameof(LessonHasNext), nameof(LessonOffersDiagnostics), nameof(LessonOffersDrill)])
            OnPropertyChanged(name);
        LessonFocusRequested?.Invoke();
    }
}
