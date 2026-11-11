using System.Net;
using System.Net.Sockets;
using System.Text;
using Amber.App.ViewModels;

namespace Amber.Core.Tests;

/// <summary>
/// A port taken from the game's output (which can carry server messages) only becomes the room's game port when it
/// answers like a Minecraft world; a typed port only has to be open (UI security review K, F19 and F21).
/// </summary>
public sealed class LanPortTrustTests
{
    private static async Task<bool> Check(byte[] reply, bool manual)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                await stream.WriteAsync(reply);
                await Task.Delay(200);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
        });
        var ok = await MultiplayerViewModel.GamePortReadyAsync(port, manual);
        listener.Stop();
        await serve;
        return ok;
    }

    private static byte[] StatusPacket(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        byte[] body = [0, (byte)bytes.Length, .. bytes];
        return [(byte)body.Length, .. body];
    }

    [Fact]
    public async Task AnotherLocalServiceIsNotAGameWorld()
    {
        var redis = "-ERR unknown command\r\n"u8.ToArray();
        Assert.False(await Check(redis, manual: false));
        // Typed by the user: their choice, it only has to be listening.
        Assert.True(await Check(redis, manual: true));
    }

    [Fact]
    public async Task AWorldThatAnswersTheStatusPingIsUsed() =>
        Assert.True(await Check(StatusPacket("""{"description":"A LAN world","version":{"name":"1.21.1"}}"""), manual: false));
}
