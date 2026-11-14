using System.ComponentModel;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// 界面上的码：复制后 60 秒自动从剪贴板清除（剪贴板还是这段文字时才清），
/// 邀请码、直连码、粘贴框和待用邀请列表默认打码，离开联机页时重新打码；每种数据封装有一句说明。
/// </summary>
public sealed class CodeHygieneTests
{
    private sealed class FakeClipboard : IClipboardText
    {
        public string? Text;
        public int Clears;
        public Task SetTextAsync(string text)
        {
            Text = text;
            return Task.CompletedTask;
        }
        public Task<string?> GetTextAsync() => Task.FromResult(Text);
        public Task ClearAsync()
        {
            Text = null;
            Clears++;
            return Task.CompletedTask;
        }
    }

    /// <summary>手动放行的“60 秒”：测试里不真等。</summary>
    private sealed class ManualDelay
    {
        private readonly List<(TimeSpan Wait, TaskCompletionSource Done)> _waits = [];
        public IReadOnlyList<TimeSpan> Requested => [.. _waits.Select(w => w.Wait)];

        public Task Delay(TimeSpan wait, CancellationToken ct)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waits) _waits.Add((wait, done));
            return done.Task;
        }

        public void ReleaseAll()
        {
            lock (_waits) foreach (var w in _waits) w.Done.TrySetResult();
        }
    }

    [Fact]
    public async Task CopiedCodeIsClearedAfterFiveMinutes()
    {
        var board = new FakeClipboard();
        var delay = new ManualDelay();
        var clipboard = new SensitiveClipboard(() => board, delay.Delay);
        Assert.True(await clipboard.CopyAsync("JAY2D-secret"));
        Assert.Equal("JAY2D-secret", board.Text);
        Assert.Equal([TimeSpan.FromMinutes(5)], delay.Requested);
        delay.ReleaseAll();
        await clipboard.PendingClears;
        Assert.Null(board.Text);
        Assert.Equal(1, board.Clears);
    }

    [Fact]
    public async Task ClipboardIsLeftAloneWhenItHoldsSomethingElse()
    {
        var board = new FakeClipboard();
        var delay = new ManualDelay();
        var clipboard = new SensitiveClipboard(() => board, delay.Delay);
        await clipboard.CopyAsync("JAY2-secret");
        board.Text = "the user's own text";
        delay.ReleaseAll();
        await clipboard.PendingClears;
        Assert.Equal("the user's own text", board.Text);
        Assert.Equal(0, board.Clears);
    }

    [Fact]
    public async Task SecondCopyIsNotClearedByTheFirstTimer()
    {
        var board = new FakeClipboard();
        var first = new ManualDelay();
        var clipboard = new SensitiveClipboard(() => board, first.Delay);
        await clipboard.CopyAsync("JAY2D-one");
        await clipboard.CopyAsync("JAY2R-two");
        Assert.Equal(2, first.Requested.Count);
        first.ReleaseAll();
        await clipboard.PendingClears;
        // 第一个定时器到点时剪贴板是第二段，不动；第二个定时器把第二段清掉。
        Assert.Null(board.Text);
        Assert.Equal(1, board.Clears);
    }

    [Fact]
    public async Task NoClipboardMeansNothingCopied()
    {
        var clipboard = new SensitiveClipboard(() => null, new ManualDelay().Delay);
        Assert.False(await clipboard.CopyAsync("JAY2D-secret"));
        Assert.False(await new SensitiveClipboard(() => new FakeClipboard()).CopyAsync(""));
    }

    private sealed class BusyClipboard : IClipboardText
    {
        public Task SetTextAsync(string text) => throw new System.Runtime.InteropServices.COMException("OpenClipboard failed", unchecked((int)0x800401D0));
        public Task<string?> GetTextAsync() => Task.FromResult<string?>(null);
        public Task ClearAsync() => Task.CompletedTask;
    }

    /// <summary>Another program holds the clipboard (CLIPBRD_E_CANT_OPEN): copying reports false instead of throwing into the UI thread and closing Jay.</summary>
    [Fact]
    public async Task BusyClipboardMeansNothingCopied()
    {
        var clipboard = new SensitiveClipboard(() => new BusyClipboard(), new ManualDelay().Delay);
        Assert.False(await clipboard.CopyAsync("JAY2D-secret"));
        await clipboard.PendingClears;
    }

    private static (Amber.App.Services.LauncherService Service, MultiplayerViewModel Vm) Create()
    {
        var service = new Amber.App.Services.LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        return (service, new MultiplayerViewModel(service));
    }

    [Fact]
    public void PasteBoxesAreMaskedByDefaultAndRemaskOnLeavingThePage() => UiTestThread.Run(() =>
    {
        var (service, vm) = Create();
        using var _ = service;
        Assert.False(vm.JoinCodeShown);
        Assert.False(vm.InviteShown);
        vm.IsCurrent = true;
        vm.ToggleJoinCodeShownCommand.Execute(null);
        vm.ToggleInviteShownCommand.Execute(null);
        Assert.True(vm.JoinCodeShown && vm.InviteShown);
        vm.IsCurrent = false;
        Assert.False(vm.JoinCodeShown);
        Assert.False(vm.InviteShown);
    });

    /// <summary>Leaving the page re-masks the thirty-day invite codes too.</summary>
    [Fact]
    public void LongInviteCodesRemaskOnLeavingThePage() => UiTestThread.Run(() =>
    {
        var (service, vm) = Create();
        using var _ = service;
        var dir = Path.Combine(Path.GetTempPath(), "jay-long-mask-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "long-invites.json");
            new LongLivedInvites(file).Create("Robin");
            vm.LongInvitesFile = file;
            var item = Assert.Single(vm.LongInviteItems);
            vm.IsCurrent = true;
            vm.ToggleLongInviteShownCommand.Execute(item);
            Assert.True(item.Shown);
            vm.IsCurrent = false;
            Assert.False(item.Shown);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });

    [Fact]
    public void PendingInviteListIsMaskedWithAToggle()
    {
        var book = new InviteBook();
        var pending = book.Create(1, TimeSpan.FromMinutes(10), false, TunnelSettings.Default);
        var item = new PendingInviteItem(pending);
        var full = pending.Invite.ToString();
        Assert.DoesNotContain(full, item.Label);
        Assert.Contains(pending.Invite.Masked, item.Label);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        item.Shown = true;
        Assert.Contains(full, item.Label);
        Assert.Contains(nameof(PendingInviteItem.Label), changed);
        item.Shown = false;
        Assert.DoesNotContain(full, item.Label);
    }

    [Fact]
    public void LeavingThePageRemasksThePendingInviteList() => UiTestThread.Run(() =>
    {
        var (service, vm) = Create();
        using var _ = service;
        var book = new InviteBook();
        var item = new PendingInviteItem(book.Create(1, TimeSpan.FromMinutes(10), false, TunnelSettings.Default));
        vm.PendingInvites.Add(item);
        vm.IsCurrent = true;
        vm.TogglePendingInviteShownCommand.Execute(item);
        Assert.True(item.Shown);
        vm.IsCurrent = false;
        Assert.False(item.Shown);
    });

    /// <summary>每种数据封装有一句“谁看得到游戏流量”的说明，四种语言都有（直接查语言表，不切全局语言）。</summary>
    [Fact]
    public void EveryTransportModeExplainsWhoCanSeeTheTraffic()
    {
        foreach (var (code, _) in L.Languages)
        {
            var table = L.Load(code);
            Assert.True(table.TryGetValue("multiplayer.transport.privacy.general", out var general) && general.Length > 10, code);
            Assert.True(table.TryGetValue("multiplayer.transport.privacy.member_channel", out var member) && member.Length > 10, code);
            Assert.NotEqual(general, member);
            Assert.True(table.ContainsKey("multiplayer.clipboard.autoclear_hint"), code);
        }
        var generalText = new TransportChoice(TransportMode.General).Privacy;
        Assert.DoesNotContain("multiplayer.transport", generalText, StringComparison.Ordinal);
        foreach (var mode in new[] { TransportMode.Minecraft, TransportMode.High, TransportMode.Extreme })
        {
            var text = new TransportChoice(mode).Privacy;
            Assert.DoesNotContain("multiplayer.transport", text, StringComparison.Ordinal);
            Assert.NotEqual(generalText, text);
        }
    }

    /// <summary>
    /// Every directory call from the multiplayer page goes through DirectoryClient(network), which remembers the
    /// directory's key on first use; a client built by hand would skip that check.
    /// </summary>
    [Fact]
    public void DirectoryClientsComeFromTheOneFactory()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModels = Path.Combine(repo, "src", "Amber.App", "ViewModels");
        var made = new System.Text.RegularExpressions.Regex(@"new\s+(Amber\.Core\.Multiplayer\.Public\.)?RoomDirectoryClient\s*\(");
        foreach (var file in Directory.EnumerateFiles(viewModels, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "MultiplayerViewModel.Contacts.cs") continue;
            Assert.False(made.IsMatch(File.ReadAllText(file)), $"{Path.GetFileName(file)} builds a RoomDirectoryClient without DirectoryClient(network)");
        }
    }
}
