using System.Globalization;
using Amber.Core.Launch;

namespace Amber.Mobile;

/// <summary>Which part of a mobile setup was running; written into the error log line.</summary>
public enum MobileSetupStage
{
    RuntimeDownload,
    RuntimeExtract,
    RuntimeVerify,
    RuntimeActivate,
    GameInstall,
    GameLaunch
}

public enum MobileSetupResult { Completed, Cancelled, Failed, AlreadyRunning }

/// <summary>What a setup page does while an attempt runs. The Android game screen passes its views in.</summary>
public sealed class MobileSetupPage(
    Action<bool> setBusy,
    Action showPanel,
    Action showCancelled,
    Action<string> showFailed,
    Action<string> logError)
{
    /// <summary>true: start disabled, cancel and progress shown. false: start enabled again, cancel hidden.</summary>
    public Action<bool> SetBusy { get; } = setBusy;
    /// <summary>Puts the setup panel back on screen if the attempt took it away (for example to show the game).</summary>
    public Action ShowPanel { get; } = showPanel;
    public Action ShowCancelled { get; } = showCancelled;
    /// <summary>Shows the failure; the text already has local folders replaced.</summary>
    public Action<string> ShowFailed { get; } = showFailed;
    /// <summary>Writes one line to the platform log (logcat on Android).</summary>
    public Action<string> LogError { get; } = logError;
}

/// <summary>
/// Runs one setup step of the Android game screen (installing the runtime, installing or launching the game) so that
/// any failure or cancel leaves the page usable: the panel stays on screen, the buttons are enabled again and the
/// next press starts a fresh attempt. Errors go to the log only as stage, type and code plus a message with local
/// folders replaced, never as a raw exception with its stack and paths.
/// </summary>
public sealed class MobileSetupAttempt(MobileSetupPage page, IReadOnlyList<(string Directory, string Placeholder)> localFolders)
{
    private const int MaxDetailLength = 300;
    private CancellationTokenSource? _cts;

    public MobileSetupStage Stage { get; set; }
    public bool IsRunning => _cts is not null;

    public void Cancel() => _cts?.Cancel();

    public async Task<MobileSetupResult> RunAsync(
        MobileSetupStage firstStage,
        Func<MobileSetupAttempt, CancellationToken, Task> work)
    {
        if (_cts is not null) return MobileSetupResult.AlreadyRunning;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        Stage = firstStage;
        page.SetBusy(true);
        try
        {
            await work(this, cts.Token);
            return MobileSetupResult.Completed;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            page.ShowCancelled();
            page.ShowPanel();
            return MobileSetupResult.Cancelled;
        }
        catch (Exception exception)
        {
            // Runs from async void click handlers: an exception escaping here would end the game process.
            page.LogError(Describe(Stage, exception, localFolders));
            page.ShowFailed(RedactMessage(exception.Message, localFolders));
            page.ShowPanel();
            return MobileSetupResult.Failed;
        }
        finally
        {
            _cts = null;
            page.SetBusy(false);
        }
    }

    public static MobileSetupStage StageOf(RuntimeInstallPhase phase) => phase switch
    {
        RuntimeInstallPhase.Downloading => MobileSetupStage.RuntimeDownload,
        RuntimeInstallPhase.Extracting => MobileSetupStage.RuntimeExtract,
        RuntimeInstallPhase.Verifying => MobileSetupStage.RuntimeVerify,
        _ => MobileSetupStage.RuntimeActivate
    };

    /// <summary>One log line: stage, exception type, inner type, code and HTTP status, and the cleaned message.</summary>
    public static string Describe(
        MobileSetupStage stage,
        Exception exception,
        IReadOnlyList<(string Directory, string Placeholder)> localFolders)
    {
        var line = $"setup failed: stage={stage} error={exception.GetType().Name}";
        if (exception.InnerException is { } inner) line += " inner=" + inner.GetType().Name;
        line += " code=0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture);
        for (Exception? e = exception; e is not null; e = e.InnerException)
            if (e is HttpRequestException { StatusCode: { } status })
            {
                line += " http=" + ((int)status).ToString(CultureInfo.InvariantCulture);
                break;
            }
        return line + " detail=" + RedactMessage(exception.Message, localFolders);
    }

    /// <summary>The exception message with local folders, user names and secrets replaced, on one short line.</summary>
    public static string RedactMessage(string message, IReadOnlyList<(string Directory, string Placeholder)> localFolders)
    {
        var text = ErrorReport.RedactPaths(message, localFolders);
        text = ErrorReport.Redact(text, Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        text = text.ReplaceLineEndings(" ");
        return text.Length <= MaxDetailLength ? text : text[..MaxDetailLength] + "…";
    }
}
