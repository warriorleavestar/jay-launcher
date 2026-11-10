using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Net;

namespace Amber.Core.Tests;

public class SafetyDrillTests
{
    [Fact]
    public void ScriptCoversTheFourMomentsInOrder()
    {
        var s = SafetyDrill.Script;
        Assert.Equal([DrillKind.Knock, DrillKind.Knock, DrillKind.File, DrillKind.RemoteControl], s.Select(x => x.Kind));
        Assert.False(s[0].CodesMatch);
        Assert.False(s[0].SafeAnswerIsYes);
        Assert.True(s[1].CodesMatch);
        Assert.True(s[1].SafeAnswerIsYes);
        Assert.EndsWith(".exe", s[2].FileName);
        Assert.False(s[2].SafeAnswerIsYes);
        Assert.False(s[3].SafeAnswerIsYes);
        // A mismatch the player has to look for: same length, one character apart.
        Assert.Equal(s[0].ShownCode!.Length, s[0].SaidCode!.Length);
        Assert.Equal(1, s[0].ShownCode!.Zip(s[0].SaidCode!).Count(p => p.First != p.Second));
    }

    [Fact]
    public void EveryTextExistsInEveryLanguage()
    {
        string[] keys = ["drill.title", "drill.intro", "drill.step", "drill.knock", "drill.file", "drill.remote", "drill.right", "drill.wrong", "drill.next", "drill.done", "drill.done.all", "drill.again"];
        foreach (var code in new[] { "en", "zh-Hans", "zh-Hant", "ja", "lzh" })
        {
            var table = L.Load(code);
            foreach (var key in keys.Concat(SafetyDrill.Script.Select(s => s.Lesson)))
                Assert.True(table.ContainsKey(key), $"{code}: {key}");
        }
    }

    [Fact]
    public void AnswersAreJudgedAndTheDrillEnds()
    {
        var drill = new SafetyDrill();
        Assert.Equal(0, drill.Index);
        Assert.False(drill.Answer(true).Right);   // let the impostor in
        Assert.True(drill.Answer(true).Right);    // let the friend in
        Assert.True(drill.Answer(false).Right);   // decline the program
        Assert.True(drill.Answer(false).Right);   // deny remote control
        Assert.True(drill.Finished);
        Assert.Null(drill.Current);
        Assert.Equal(3, drill.RightCount);
        Assert.Throws<InvalidOperationException>(() => drill.Answer(false));
    }

    [Fact]
    public void RestartStartsFromTheFirstStep()
    {
        var drill = new SafetyDrill();
        drill.Answer(false);
        drill.Answer(true);
        drill.Restart();
        Assert.Equal(0, drill.Index);
        Assert.Same(SafetyDrill.Script[0], drill.Current);
        Assert.Empty(drill.Answers);
    }

    private static readonly AsyncLocal<bool> InDrill = new();

    [Fact]
    public void AFullRunMakesNoNetworkRequest()
    {
        var requests = 0;
        void Handler(NetworkCategory? c, Uri? u) { if (InDrill.Value) Interlocked.Increment(ref requests); }
        NetworkPolicy.Requested += Handler;
        try
        {
            InDrill.Value = true;
            var drill = new SafetyDrill();
            foreach (var step in SafetyDrill.Script) drill.Answer(step.SafeAnswerIsYes);
            drill.Restart();
            while (!drill.Finished) drill.Answer(true);
            Assert.Equal(0, requests);
        }
        finally
        {
            InDrill.Value = false;
            NetworkPolicy.Requested -= Handler;
        }
    }
}
