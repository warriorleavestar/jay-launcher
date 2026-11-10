namespace Amber.Core.Multiplayer;

/// <summary>What the pretend friend asks for in one drill step.</summary>
public enum DrillKind
{
    /// <summary>A knock. <see cref="DrillStep.CodesMatch"/> says whether the code the friend reads out is the one on screen.</summary>
    Knock,
    /// <summary>A file offer; <see cref="DrillStep.FileName"/> is the file.</summary>
    File,
    /// <summary>A request to control this computer.</summary>
    RemoteControl,
}

/// <summary>
/// One step: what happens, what the friend says, and which answer is the safe one. Text is by key (UI localizes);
/// <see cref="Lesson"/> is the key of the one-sentence explanation shown after the answer, right or wrong.
/// </summary>
public sealed record DrillStep(DrillKind Kind, bool SafeAnswerIsYes, string Lesson, string? ShownCode = null, string? SaidCode = null, string? FileName = null)
{
    public bool CodesMatch => ShownCode == SaidCode;
}

/// <summary>An answer that was given: the step, what the player chose, and whether that was the safe choice.</summary>
public sealed record DrillAnswer(DrillStep Step, bool Yes)
{
    public bool Right => Yes == Step.SafeAnswerIsYes;
}

/// <summary>
/// 演练模式 (official §9.5): a pretend friend “小松鸦” plays the risky moments of a multiplayer session in order — a knock
/// whose safety code does not match, a knock whose code does, a program file, and a remote control request — and
/// Jay explains each answer on the spot. Everything is local: there is no room, no connection and no network request,
/// only this script. Answers can't be undone, but the drill can be started over.
/// </summary>
public sealed class SafetyDrill
{
    /// <summary>The script. The codes are made up for the drill and never shown as real codes.</summary>
    public static readonly IReadOnlyList<DrillStep> Script =
    [
        new(DrillKind.Knock, SafeAnswerIsYes: false, "drill.lesson.code_mismatch", ShownCode: "7Q4M-2KXP", SaidCode: "7Q4M-2KXB"),
        new(DrillKind.Knock, SafeAnswerIsYes: true, "drill.lesson.code_match", ShownCode: "H3VT-9RWA", SaidCode: "H3VT-9RWA"),
        new(DrillKind.File, SafeAnswerIsYes: false, "drill.lesson.program_file", FileName: "free-skins.exe"),
        new(DrillKind.RemoteControl, SafeAnswerIsYes: false, "drill.lesson.remote_control"),
    ];

    private readonly List<DrillAnswer> _answers = [];

    public IReadOnlyList<DrillAnswer> Answers => _answers;

    /// <summary>The step waiting for an answer, or null when the drill is over.</summary>
    public DrillStep? Current => _answers.Count < Script.Count ? Script[_answers.Count] : null;

    public int Index => _answers.Count;

    public bool Finished => Current is null;

    public int RightCount => _answers.Count(a => a.Right);

    /// <summary>Answer the current step (yes = let in / accept / allow). Returns the answer with its verdict.</summary>
    public DrillAnswer Answer(bool yes)
    {
        var step = Current ?? throw new InvalidOperationException("The drill is over.");
        var answer = new DrillAnswer(step, yes);
        _answers.Add(answer);
        return answer;
    }

    public void Restart() => _answers.Clear();
}
