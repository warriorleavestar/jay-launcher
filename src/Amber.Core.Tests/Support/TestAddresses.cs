using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Addresses for tests that need a "public" one, without writing any real host's address into the repository.
/// Tests use the documentation ranges (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24, 2001:db8::/32) and, where the
/// code under test refuses those, hand it <see cref="IsRoutable"/> in place of the real check.
/// </summary>
internal static class TestAddresses
{
    /// <summary>
    /// The real <see cref="ExitPolicy.IsGloballyRoutable"/>, except that documentation addresses count as routable.
    /// Nothing else is widened: private, loopback, link-local, benchmark and multicast addresses stay refused.
    /// </summary>
    public static bool IsRoutable(IPAddress address) => ExitPolicy.IsGloballyRoutable(address) || IsDocumentation(address);

    public static bool IsDocumentation(IPAddress address)
    {
        address = ExitPolicy.Normalize(address);
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 192 && b[1] == 0 && b[2] == 2 || b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113;
        return address.AddressFamily == AddressFamily.InterNetworkV6 && b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8;
    }

    /// <summary>
    /// The address right after <paramref name="lastOfRange"/>, the last address of a reserved range. Checks of the real
    /// classifiers use it to test the edge of a range instead of naming some real host.
    /// </summary>
    public static IPAddress After(string lastOfRange) => Step(IPAddress.Parse(lastOfRange), 1);

    /// <summary>The address right before <paramref name="firstOfRange"/>, the first address of a reserved range.</summary>
    public static IPAddress Before(string firstOfRange) => Step(IPAddress.Parse(firstOfRange), -1);

    private static IPAddress Step(IPAddress address, int by)
    {
        var bytes = address.GetAddressBytes();
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true) + by;
        var result = new byte[bytes.Length];
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        raw.CopyTo(result, result.Length - raw.Length);
        return new IPAddress(result);
    }
}
