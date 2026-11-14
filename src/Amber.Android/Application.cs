using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Amber.Android;

[Application]
public sealed class Application : AvaloniaAndroidApplication<Mobile.App>
{
    public Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { }

    public override void OnCreate()
    {
        // Before anything reads the device identity or saved servers: seal their wrap keys in the Keystore.
        Amber.Core.Auth.LocalKeyProtection.Protector = new AndroidKeystoreKeyProtector();
        Mobile.App.PlatformServices = new AndroidMobilePlatformServices(this);
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}
