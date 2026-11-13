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
    /// <summary>
    /// This device's certificate from the selected Minecraft account's Jay account (re-signed if the device keys changed),
    /// with the device keys that sign each live proof.
    /// </summary>
    internal JayProof? JayCertificate
    {
        get
        {
            if (_service.Accounts.Store.Selected?.JayAddress is not { } address) return null;
            try
            {
                var device = Identity.Device;
                return _service.JayAccounts.EnsureDeviceCertificate(address, device, JayAccountStore.ThisDeviceName(), DateTimeOffset.UtcNow)?.Device is { } cert
                    ? new JayProof(cert, device)
                    : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// A knock either way proved someone's Jay account live: tell the friends list, which groups the device under the
    /// person or adds it as a known person's new device waiting for my OK.
    /// </summary>
    private void SeenJayDevice(byte[] deviceKey, DeviceCertificate? certificate)
    {
        if (certificate is null) return;
        try { Contacts.SeenLive(deviceKey, certificate); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _diag.Add(Amber.Core.Localization.L.T("multiplayer.invite.diagnostics_category"), e.Message); }
    }

    /// <summary>"Jay account jay1qj0v…xdag", or null.</summary>
    internal static string? JayLine(string? account) => account is null ? null : Amber.Core.Localization.L.F("accounts.jay.bound_line", JayAddress.Short(account));
}
