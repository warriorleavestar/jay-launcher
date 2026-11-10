using System.Security.Cryptography;
using Amber.Core.Identity;

namespace Amber.App.ViewModels;

/// <summary>
/// Jay accounts in multiplayer (phase 2): a guest sends the certificate their Jay account signed for this device, the host
/// checks it against the key the guest proved, shows the account, and can trust the account instead of one device.
/// Nothing is sent when the selected Minecraft account has no Jay account bound; unlinking it on the accounts page stops it.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    /// <summary>This device's certificate from the selected Minecraft account's Jay account, re-signed if the multiplayer key changed.</summary>
    internal DeviceCertificate? JayCertificate
    {
        get
        {
            if (_service.Accounts.Store.Selected?.JayAddress is not { } address) return null;
            try
            {
                return _service.JayAccounts.EnsureDeviceCertificate(address, Identity.PublicKey, JayAccountStore.ThisDeviceName(), DateTimeOffset.UtcNow)?.Device;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
            {
                return null;
            }
        }
    }

    /// <summary>"Jay account jay1qj0v…xdag", or null.</summary>
    internal static string? JayLine(string? account) => account is null ? null : Amber.Core.Localization.L.F("accounts.jay.bound_line", JayAddress.Short(account));
}
