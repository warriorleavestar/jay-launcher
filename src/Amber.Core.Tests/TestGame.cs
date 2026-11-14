using System.Net;
using System.Net.Sockets;

namespace Amber.Core.Tests;

/// <summary>A stand-in game on loopback: echoes whatever a connection sends; a connection that sends nothing just stays
/// open (a player that never finishes logging in).</summary>
internal sealed class TestGame : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private int _accepted;

    public TestGame()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                Interlocked.Increment(ref _accepted);
                _ = Task.Run(async () =>
                {
                    using var c = client;
                    var s = c.GetStream();
                    var buffer = new byte[256];
                    try
                    {
                        int n;
                        while ((n = await s.ReadAsync(buffer, _cts.Token)) > 0) await s.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
                    }
                    catch (Exception e) when (e is OperationCanceledException or IOException or SocketException) { }
                });
            }
        });
    }

    public int Port { get; }

    /// <summary>Connections the game has taken so far.</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
