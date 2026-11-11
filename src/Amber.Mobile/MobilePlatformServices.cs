namespace Amber.Mobile;

public enum MobileGameLauncherResult
{
    Launched,
    InstallPageOpened,
    Unavailable
}

public interface IMobilePlatformServices
{
    bool CanOpenGameLauncher { get; }
    string GameLauncherName { get; }
    Task<MobileGameLauncherResult> OpenGameLauncherAsync(string? serverAddress);
    void SetGatewayActive(bool active);
}

public sealed class NoMobilePlatformServices : IMobilePlatformServices
{
    public static NoMobilePlatformServices Instance { get; } = new();

    private NoMobilePlatformServices() { }

    public bool CanOpenGameLauncher => false;
    public string GameLauncherName => "";
    public Task<MobileGameLauncherResult> OpenGameLauncherAsync(string? serverAddress) =>
        Task.FromResult(MobileGameLauncherResult.Unavailable);
    public void SetGatewayActive(bool active) { }
}
