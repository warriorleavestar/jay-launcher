using System.ComponentModel;
using System.Runtime.CompilerServices;
using Amber.App.Controls.Safety;
using Amber.App.Localization;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>换语言的通知：下拉框选项跟着换文字，绑定被丢掉后能被回收（不会因为静态事件一直留在内存里）。</summary>
[Collection("GlobalLanguage")]
public class LanguageListenerTests
{
    private sealed class Recorder : IObserver<string>
    {
        public readonly List<string> Values = [];
        public void OnNext(string value) => Values.Add(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    // 不切换全局语言，直接触发“换语言”的通知；放在 GlobalLanguage 里，别的测试换语言时发的通知不会算进这里的次数。
    [Fact]
    public void OptionItemRaisesLabelChanged()
    {
        var item = OptionItem.Key("common.all");
        var changed = 0;
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OptionItem.Label)) changed++; };
        Assert.Equal(L.T("common.all"), item.Label);
        LanguageListeners.NotifyOnly(item);
        Assert.Equal(1, changed);
        Assert.Equal("日本語", OptionItem.Fixed("日本語").Label);
        GC.KeepAlive(item);
    }

    [Fact]
    public void LocalizedTextPushesAndUnsubscribes()
    {
        var recorder = new Recorder();
        var subscription = new LocalizedText("common.all").Subscribe(recorder);
        Assert.Single(recorder.Values);
        LanguageListeners.NotifyOnly(recorder);
        Assert.Equal(2, recorder.Values.Count);
        subscription.Dispose();
        LanguageListeners.NotifyOnly(recorder);
        Assert.Equal(2, recorder.Values.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SubscribeAndDrop(int count)
    {
        for (var i = 0; i < count; i++) _ = new LocalizedText("common.all").Subscribe(new Recorder());
    }

    [Fact]
    public void DroppedBindingsAreCollected()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var baseline = LanguageListeners.LiveCount;
        SubscribeAndDrop(500);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.True(LanguageListeners.LiveCount < baseline + 50, $"还活着 {LanguageListeners.LiveCount}，开始时 {baseline}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] MakeSafetySurfaces() => [new(new SafetyPanel { Title = "t" }), new(new PrivacyRow())];

    /// <summary>
    /// Safety panels are built for every safety dialog, request popup and knock card: listening for language changes
    /// must not keep them alive after they are closed.
    /// </summary>
    [Fact]
    public void ClosedSafetySurfacesAreCollected()
    {
        WeakReference[] weak = [];
        UiTestThread.Run(() => weak = MakeSafetySurfaces());
        for (var i = 0; i < 3 && weak.Any(w => w.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak[0].IsAlive, "SafetyPanel is still alive");
        Assert.False(weak[1].IsAlive, "PrivacyRow is still alive");
    }

    [Fact]
    public void SafetySurfacesFollowTheLanguage() => UiTestThread.Run(() =>
    {
        var before = L.Language;
        try
        {
            L.SetLanguage("en");
            var panel = new SafetyPanel();
            var row = new PrivacyRow();
            L.SetLanguage("zh-Hans");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(L.T("safety.title"), panel.BadgeText);
            Assert.Equal($"{L.T("main.no_cloud_control")} · {L.T("main.no_telemetry")}", row.Text);
            Assert.Equal(row.Text, Avalonia.Automation.AutomationProperties.GetName(row));
        }
        finally
        {
            L.SetLanguage(before);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    });
}
