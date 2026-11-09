using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>设备验证的要求：关、开（对方支持就验证）、必须（对方不支持就不让进）。</summary>
public enum AttestationMode { Off = 0, On = 1, Required = 2 }

/// <summary>
/// 联机的最低安全要求。本机设置给一份，第三方公开网络的配置文件（policy.security）可以再给一份；
/// 两份用 <see cref="Tighten"/> 合起来，网络只能把要求调高，不能调低。
/// RestartDelaySeconds：熔断后自动重启前的倒计时；SkipRestartDelay：不等倒计时直接重启（只有本机设置能开，网络不能替用户打开）。
/// </summary>
public sealed record SecurityPolicy(AttestationMode Attestation, bool Breaker, int RestartDelaySeconds, bool SkipRestartDelay)
{
    public const int DefaultRestartDelay = 7;
    public const int MaxRestartDelay = 600;

    public static readonly SecurityPolicy Default = new(AttestationMode.On, true, DefaultRestartDelay, false);

    /// <summary>真正要等的秒数（跳过等待时是 0）。</summary>
    public int EffectiveRestartDelay => SkipRestartDelay ? 0 : RestartDelaySeconds;

    /// <summary>
    /// 读网络配置里的 policy.security：{ "attestation": "off"|"on"|"required", "breaker": "off"|"on", "restartDelayMin": 秒 }。
    /// 没有这一段返回 null（不加要求）。写了但值不对就抛 InvalidDataException，让整份配置被拒，免得半懂不懂地放宽。
    /// </summary>
    public static SecurityPolicy? Parse(JsonNode? json)
    {
        if (json is null) return null;
        if (json is not JsonObject o) throw new InvalidDataException("policy.security must be an object");
        try
        {
            var attestation = o["attestation"] is null ? AttestationMode.Off : o["attestation"]!.GetValue<string>() switch
            {
                "off" => AttestationMode.Off,
                "on" => AttestationMode.On,
                "required" => AttestationMode.Required,
                _ => throw new InvalidDataException("policy.security.attestation"),
            };
            var breaker = o["breaker"] is null ? false : o["breaker"]!.GetValue<string>() switch
            {
                "off" => false,
                "on" => true,
                _ => throw new InvalidDataException("policy.security.breaker"),
            };
            var delay = o["restartDelayMin"] is null ? 0 : o["restartDelayMin"]!.GetValue<int>();
            if (delay is < 0 or > MaxRestartDelay) throw new InvalidDataException("policy.security.restartDelayMin");
            // 网络给的是“最低要求”：没写的项按最宽松算，合并时不会把本机的要求拉低。
            return new SecurityPolicy(attestation, breaker, delay, false);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("policy.security", e);
        }
    }

    /// <summary>
    /// 合并本机设置和网络要求：验证取更严的、熔断任一方开就开、倒计时取更长的；
    /// 「跳过等待」只看本机，而且网络要求了最短倒计时时不能跳过。
    /// </summary>
    public SecurityPolicy Tighten(SecurityPolicy? network)
    {
        if (network is null) return this;
        var delay = Math.Max(RestartDelaySeconds, network.RestartDelaySeconds);
        return new SecurityPolicy(
            (AttestationMode)Math.Max((int)Attestation, (int)network.Attestation),
            Breaker || network.Breaker,
            delay,
            SkipRestartDelay && network.RestartDelaySeconds == 0);
    }
}
