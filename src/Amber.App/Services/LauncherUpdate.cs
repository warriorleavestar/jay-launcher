using System.Diagnostics;
using System.Runtime.InteropServices;
using Amber.App.ViewModels;
using Amber.Core.Activity;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Threading;

namespace Amber.App.Services;

/// <summary>Install only the native installer explicitly listed in a trustee-signed release.</summary>
public static class LauncherUpdate
{
    public static Action<string>? AndroidInstall { private get; set; }

    internal static string? PackageFor(VerifiedUpdate update) => PackageFor(update, OperatingSystem.IsAndroid() ? "android" : RuntimeInformation.RuntimeIdentifier);

    internal static string? PackageFor(VerifiedUpdate update, string rid)
    {
        string? name = rid switch
        {
            "win-x64" => "Jay-win-x64-setup.exe",
            "osx-arm64" => "Jay-osx-arm64.pkg",
            "osx-x64" => "Jay-osx-x64.pkg",
            "android" => "Jay-android.apk",
            _ => null
        };
        return update.IsVerified && name != null && update.Sums.ContainsKey(name) && update.Assets.ContainsKey(name) ? name : null;
    }

    internal static void Start(LauncherService service, VerifiedUpdate update)
    {
        if (PackageFor(update) is not { } name) return;
        service.RevealUpdateTask();
        service.Tasks.Start("update", L.F("tools.update.task", update.Info.Version), async (task, ct) =>
        {
            CheckCanInstall(service);
            var check = new UpdateCheck(service.Http, AppSettings.DataDir);
            var cache = Path.Combine(AppSettings.DataDir, "launcher-updates", update.Sums[name]);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            var path = await Task.Run(() => check.DownloadAsync(update, name, cache, timeout.Token,
                (bytes, total) => task.Report(total > 0 ? (double)bytes / total : null, bytes: bytes, activeFiles: [name])), ct);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                CheckCanInstall(service);
                if (check.HighestSeq > update.Check.Seq) throw new InvalidOperationException(L.T("tools.update.verdict.rollback"));
                if (OperatingSystem.IsAndroid())
                {
                    (AndroidInstall ?? throw new InvalidOperationException(L.T("tools.update.foreground")))(path);
                }
                else
                {
                    var start = new ProcessStartInfo { UseShellExecute = false };
                    if (OperatingSystem.IsMacOS())
                    {
                        start.FileName = "/usr/bin/open";
                        start.ArgumentList.Add("-b");
                        start.ArgumentList.Add("com.apple.installer");
                        start.ArgumentList.Add(path);
                    }
                    else if (OperatingSystem.IsWindows())
                    {
                        start.FileName = path;
                        start.ArgumentList.Add("/DIR=" + Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
                    }
                    else throw new PlatformNotSupportedException();
                    using var process = Process.Start(start) ?? throw new IOException(L.T("tools.update.foreground"));
                }
            });
            return TaskOutcome.Success(L.T("tools.update.confirm"));
        }, dedupeKey: "jay-launcher-update");
    }

    private static void CheckCanInstall(LauncherService service)
    {
        if (service.AnyRunning || HostServerViewModel.ServerRunning)
            throw new InvalidOperationException(L.T("tools.update.finish_game"));
    }
}
