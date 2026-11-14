using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Amber.App.Services;

/// <summary>
/// One Jay per data folder. Two copies on the same folder would each write settings.json and the account files from
/// their own memory, so the last one to save wins and the other's changes are lost. The first Jay holds an exclusive
/// lock on a file in the data folder (the system lets go of it when the process ends, even after a crash); a second
/// start finds it held, asks the running Jay to come to the front, and exits.
/// The request travels over a local named pipe that only this user account can open, and carries no data: it can do
/// nothing but bring the window forward. Separate data folders (AMBER_DATA_DIR) run side by side as before.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string LockFileName = "jay.lock";
    public const string WaitPidArgument = "--wait-pid";

    private readonly FileStream _lock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(FileStream held, string pipeName)
    {
        _lock = held;
        _pipeName = pipeName;
    }

    /// <summary>Another start of Jay on this data folder asked for the window (raised on a background thread).</summary>
    public event Action? ActivationRequested;

    /// <summary>The pipe name for a data folder: the same for every Jay on it, unrelated folders never share one.</summary>
    internal static string PipeNameFor(string dataDir)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        if (!OperatingSystem.IsLinux()) full = full.ToUpperInvariant();
        return "jay-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..24];
    }

    /// <summary>Takes the data folder for this process, or returns null when another Jay already holds it.</summary>
    public static SingleInstance? TryAcquire(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        try
        {
            var held = new FileStream(Path.Combine(dataDir, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var instance = new SingleInstance(held, PipeNameFor(dataDir));
            instance.Listen();
            return instance;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Takes the data folder, waiting up to <paramref name="patience"/> for a Jay that is closing to let go of it.</summary>
    public static SingleInstance? Acquire(string dataDir, TimeSpan patience)
    {
        var until = DateTime.UtcNow + patience;
        while (true)
        {
            if (TryAcquire(dataDir) is { } instance) return instance;
            if (DateTime.UtcNow >= until) return null;
            Thread.Sleep(200);
        }
    }

    /// <summary>Asks the Jay that holds this data folder to show its window. False when it could not be reached.</summary>
    public static bool RequestActivation(string dataDir, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeNameFor(dataDir), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A restart: the new Jay waits for the old one to finish closing (it still saves on the way out).</summary>
    public static void WaitForExit(string[] args, TimeSpan timeout)
    {
        if (WaitPid(args) is not { } pid) return;
        try
        {
            using var old = Process.GetProcessById(pid);
            old.WaitForExit(timeout);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    internal static int? WaitPid(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == WaitPidArgument && int.TryParse(args[i + 1], out var pid) && pid > 0) return pid;
        return null;
    }

    /// <summary>The arguments without a <c>--wait-pid N</c> pair (a restart passes on the rest unchanged).</summary>
    internal static List<string> WithoutWaitPid(IEnumerable<string> args)
    {
        var list = new List<string>();
        var skipNext = false;
        foreach (var arg in args)
        {
            if (skipNext) { skipNext = false; continue; }
            if (arg == WaitPidArgument) { skipNext = true; continue; }
            list.Add(arg);
        }
        return list;
    }

    private void Listen() => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                ActivationRequested?.Invoke();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A broken connection or a name in use: try again shortly; activation is a convenience.
                try { await Task.Delay(1000, _stop.Token); } catch (OperationCanceledException) { return; }
            }
        }
    });

    public void Dispose()
    {
        _stop.Cancel();
        _lock.Dispose();
    }
}
