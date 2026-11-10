using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// Drill mode (official §9.5): the lobby's "Practice once ›" opens a drawer where a pretend friend walks through a
/// mismatched knock, a real knock, a program file and a remote control request. Each answer gets its verdict and a
/// one-sentence lesson. Purely local: no room, no connection, no request.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private readonly SafetyDrill _drill = new();

    /// <summary>The answer being explained, until 【下一步】.</summary>
    private DrillAnswer? _drillShown;

    [ObservableProperty] public partial bool DrillOpen { get; set; }

    /// <summary>Called when a step appears, so the view can put the focus on the safe default (No).</summary>
    public Action? DrillFocusRequested { get; set; }

    /// <summary>The step on screen: the one just answered while its lesson shows, else the one waiting.</summary>
    private DrillStep? DrillStepShown => _drillShown?.Step ?? _drill.Current;

    public bool DrillAsking => _drillShown is null && !_drill.Finished;
    public bool DrillExplaining => _drillShown is not null;
    public bool DrillDone => _drillShown is null && _drill.Finished;

    public string DrillProgress => DrillStepShown is null ? "" : L.F("drill.step", Index(DrillStepShown) + 1, SafetyDrill.Script.Count);

    private static int Index(DrillStep step)
    {
        for (var i = 0; i < SafetyDrill.Script.Count; i++) if (ReferenceEquals(SafetyDrill.Script[i], step)) return i;
        return 0;
    }

    public string DrillPrompt => DrillStepShown switch
    {
        { Kind: DrillKind.Knock } => L.T("drill.knock"),
        { Kind: DrillKind.File, FileName: var name } => L.F("drill.file", name),
        { Kind: DrillKind.RemoteControl } => L.T("drill.remote"),
        _ => "",
    };

    public bool DrillShowsCodes => DrillStepShown?.Kind == DrillKind.Knock;
    public string DrillShownCode => DrillStepShown?.ShownCode ?? "";
    public string DrillSaidCode => DrillStepShown?.SaidCode ?? "";

    public string DrillYesText => DrillStepShown?.Kind switch
    {
        DrillKind.Knock => L.T("drill.yes.knock"),
        DrillKind.File => L.T("drill.yes.file"),
        _ => L.T("drill.yes.remote"),
    };

    public string DrillNoText => DrillStepShown?.Kind switch
    {
        DrillKind.Knock => L.T("drill.no.knock"),
        DrillKind.File => L.T("drill.no.file"),
        _ => L.T("drill.no.remote"),
    };

    public bool DrillRight => _drillShown?.Right == true;
    public string DrillVerdict => _drillShown is null ? "" : L.T(_drillShown.Right ? "drill.right" : "drill.wrong");
    public string DrillLesson => _drillShown is null ? "" : L.T(_drillShown.Step.Lesson);

    public string DrillScore => _drill.RightCount == SafetyDrill.Script.Count
        ? L.F("drill.done.all", SafetyDrill.Script.Count)
        : L.F("drill.done", _drill.RightCount, SafetyDrill.Script.Count);

    [RelayCommand]
    private void OpenDrill()
    {
        // Starting from the lobby always starts over: a half-finished drill from earlier isn't worth resuming.
        _drill.Restart();
        _drillShown = null;
        DiagnosticsOpen = false;
        DrillOpen = true;
        RefreshDrill();
    }

    [RelayCommand]
    private void CloseDrill() => DrillOpen = false;

    [RelayCommand]
    private void DrillAnswerYes() => AnswerDrill(true);

    [RelayCommand]
    private void DrillAnswerNo() => AnswerDrill(false);

    private void AnswerDrill(bool yes)
    {
        if (!DrillAsking) return;
        _drillShown = _drill.Answer(yes);
        RefreshDrill();
    }

    [RelayCommand]
    private void DrillNext()
    {
        _drillShown = null;
        RefreshDrill();
    }

    [RelayCommand]
    private void DrillRestart()
    {
        _drill.Restart();
        _drillShown = null;
        RefreshDrill();
    }

    private void RefreshDrill()
    {
        foreach (var name in (string[])[nameof(DrillAsking), nameof(DrillExplaining), nameof(DrillDone), nameof(DrillProgress), nameof(DrillPrompt),
                     nameof(DrillShowsCodes), nameof(DrillShownCode), nameof(DrillSaidCode), nameof(DrillYesText), nameof(DrillNoText),
                     nameof(DrillRight), nameof(DrillVerdict), nameof(DrillLesson), nameof(DrillScore)])
            OnPropertyChanged(name);
        DrillFocusRequested?.Invoke();
    }
}
