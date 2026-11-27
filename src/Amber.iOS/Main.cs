using Amber.Core.Auth;
using Foundation;
using UIKit;

namespace Amber.iOS;

public static class Program
{
    public static void Main(string[] args)
    {
        // Before anything reads the device identity or saved servers: seal their wrap keys with a
        // Keychain key, and keep the data folder out of backups (its keys can't open on another device).
        LocalKeyProtection.Protector = new KeychainKeyProtector();
        ExcludeDataFromBackup();
        UIApplication.Main(args, null, typeof(AppDelegate));
    }

    private static void ExcludeDataFromBackup()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jay");
        try
        {
            Directory.CreateDirectory(directory);
            using var url = NSUrl.FromFilename(directory);
            url.SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(true), out _);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without the folder there is nothing to back up yet; the next start tries again.
        }
    }
}
