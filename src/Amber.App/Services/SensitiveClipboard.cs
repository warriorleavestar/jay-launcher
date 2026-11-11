using Avalonia.Threading;

namespace Amber.App.Services;

/// <summary>剪贴板的最小接口：放文字、读文字、清空（真窗口用 Avalonia 的剪贴板，测试里换成假的）。</summary>
public interface IClipboardText
{
    Task SetTextAsync(string text);
    Task<string?> GetTextAsync();
    Task ClearAsync();
}

/// <summary>
/// 复制邀请码、直连码这类敏感文字：放进剪贴板，5 分钟后如果剪贴板里还是这段文字就清掉；
/// 那时已经换成别的内容（用户自己又复制了别的）就不动。
/// 只用 Avalonia 的剪贴板接口，Windows、macOS、Linux 一样。
/// </summary>
public sealed class SensitiveClipboard
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly Func<IClipboardText?> _clipboard;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly List<Task> _pending = [];

    /// <param name="clipboard">取当前的剪贴板（没有窗口时返回 null）。</param>
    /// <param name="delay">等待用的函数（测试里换成手动放行的）。</param>
    public SensitiveClipboard(Func<IClipboardText?> clipboard, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _clipboard = clipboard;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>主窗口的剪贴板。</summary>
    public static SensitiveClipboard Shared { get; } = new(AvaloniaClipboard.FromMainWindow);

    /// <summary>还没跑完的定时清除（测试用）。</summary>
    internal Task PendingClears
    {
        get { lock (_gate) return Task.WhenAll([.. _pending]); }
    }

    /// <summary>复制并安排 5 分钟后清除。没有剪贴板时返回 false。</summary>
    public async Task<bool> CopyAsync(string text)
    {
        if (string.IsNullOrEmpty(text) || _clipboard() is not { } clipboard) return false;
        try { await clipboard.SetTextAsync(text); }
        catch (Exception e) when (ClipboardErrors.IsBusy(e)) { return false; }
        var task = ClearLaterAsync(clipboard, text, _cts.Token);
        lock (_gate)
        {
            _pending.RemoveAll(t => t.IsCompleted);
            _pending.Add(task);
        }
        return true;
    }

    private async Task ClearLaterAsync(IClipboardText clipboard, string text, CancellationToken ct)
    {
        try
        {
            await _delay(Lifetime, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (string.Equals(await clipboard.GetTextAsync(), text, StringComparison.Ordinal)) await clipboard.ClearAsync();
        }
        catch (Exception e) when (ClipboardErrors.IsBusy(e))
        {
            // 剪贴板被别的程序占着：这次清不了，不影响别的。
        }
    }
}

/// <summary>The clipboard is held by another program (Windows: CLIPBRD_E_CANT_OPEN) or not usable right now.</summary>
internal static class ClipboardErrors
{
    public static bool IsBusy(Exception e) =>
        e is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException or UnauthorizedAccessException;
}

/// <summary>Avalonia 的剪贴板（都在界面线程上调用）。</summary>
internal sealed class AvaloniaClipboard(Avalonia.Input.Platform.IClipboard clipboard) : IClipboardText
{
    public static IClipboardText? FromMainWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard }
            ? new AvaloniaClipboard(clipboard)
            : null;

    /// <summary>Copies plain text to the main window's clipboard; false when there is none or another program holds it (a busy clipboard must not close Jay).</summary>
    public static async Task<bool> TrySetTextAsync(string text)
    {
        if (FromMainWindow() is not { } clipboard) return false;
        try
        {
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception e) when (ClipboardErrors.IsBusy(e))
        {
            return false;
        }
    }

    public Task SetTextAsync(string text) => OnUi(() => Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text));

    public Task<string?> GetTextAsync() => OnUi(() => Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard));

    public Task ClearAsync() => OnUi(() => clipboard.ClearAsync());

    private static Task OnUi(Func<Task> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);

    private static Task<T> OnUi<T>(Func<Task<T>> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);
}
