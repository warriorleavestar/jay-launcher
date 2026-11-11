using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// A gate that takes the connection and then says nothing (an invite can point anywhere) must fail like any other
/// connection problem, not with a cancellation the apps don't expect (UI security review I, F31).
/// </summary>
public sealed class GateTimeoutTests
{
    /// <summary>Accepts writes; every read ends the way the handshake timeout ends it.</summary>
    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new OperationCanceledException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new OperationCanceledException());
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private static readonly GateInvite Invite = new("gate.example", 25565, new byte[GateInvite.KeyHashLength], new byte[16], "Steve");

    [Fact]
    public async Task ASilentGateIsAConnectionError()
    {
        using var device = X25519KeyPair.Generate();
        await Assert.ThrowsAsync<IOException>(() => GateProtocol.EnrollAsync(new SilentStream(), Invite, device));
        var membership = new GateMembership("gate.example", 25565, new byte[32], new byte[32], "Steve");
        await Assert.ThrowsAsync<IOException>(() => GateProtocol.JoinAsync(new SilentStream(), membership, device));
        await Assert.ThrowsAsync<IOException>(() => GateProtocol.RelayConnectAsync(new SilentStream(), new GateRelayCode(new byte[GateInvite.KeyHashLength], new byte[32]), device, default));
    }

    [Fact]
    public async Task TheCallersOwnCancelStaysACancel()
    {
        using var device = X25519KeyPair.Generate();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GateProtocol.EnrollAsync(new SilentStream(), Invite, device, cts.Token));
    }
}
