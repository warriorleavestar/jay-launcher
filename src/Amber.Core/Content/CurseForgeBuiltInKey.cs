using System.Reflection;

namespace Amber.Core.Content;

/// <summary>
/// 官方发布版构建时注入的 CurseForge 第三方 API Key（CI 的仓库 secret CURSEFORGE_API_KEY，见 Amber.Core.csproj）。
/// 仓库里没有 Key；自己编译的版本这里是 null。不要把它写进日志、诊断报告或界面。
/// </summary>
public static class CurseForgeBuiltInKey
{
    internal const string MetadataName = "Jay.CurseForgeApiKey";

    public static string? Value { get; } = Read(typeof(CurseForgeBuiltInKey).Assembly);

    internal static string? Read(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == MetadataName)?.Value is { } v
        && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    /// <summary>先用用户在设置里填的 Key，再用环境变量 AMBER_CF_KEY，最后用内置 Key。</summary>
    public static string? Resolve(string? userKey, string? environmentKey) => Resolve(userKey, environmentKey, Value);

    internal static string? Resolve(string? userKey, string? environmentKey, string? builtInKey) =>
        !string.IsNullOrWhiteSpace(userKey) ? userKey
        : !string.IsNullOrWhiteSpace(environmentKey) ? environmentKey
        : builtInKey;
}
