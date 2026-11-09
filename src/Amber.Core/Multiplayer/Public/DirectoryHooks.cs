using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Amber.Core.Multiplayer.Public;

/// <summary>房主要发布 / 续期一个房间时，交给审核规则看的东西。</summary>
/// <param name="Listing">已经核对过签名和编号的房间信息。</param>
/// <param name="Listed">房主想不想进公开列表。</param>
/// <param name="IsNew">目录里原来没有这个房间（第一次发布，不是续期）。</param>
/// <param name="HasFingerprintSearch">房主打开了“可以用指纹码找到这个房间”。</param>
public sealed record RoomAnnouncement(RoomListing Listing, bool Listed, bool IsNew, bool HasFingerprintSearch);

/// <summary>审核规则的结论：放行，或者拒绝（带一个错误码和一句给房主看的话）。</summary>
public readonly record struct RoomVerdict(string? Code, string? Reason)
{
    public static readonly RoomVerdict Allow = default;

    public bool Allowed => Code is null;

    /// <summary>拒绝。<paramref name="code"/> 只用小写字母、数字、下划线；<paramref name="reason"/> 会显示给房主（最多 120 字）。</summary>
    public static RoomVerdict Reject(string code, string? reason = null) =>
        new(DirectoryExtensions.IsValidCode(code) ? code : "policy", reason is null ? null : RoomChat.Clean(reason, DirectoryExtensions.MaxReason));
}

/// <summary>
/// 房间审核规则：每次发布 / 续期都按顺序问一遍，任何一条拒绝就不收。
/// 网络运营者用它实现自己的规矩：只收某些房间方式、屏蔽词、封禁房主、限流……
/// 规则在目录的锁里调用，要快，不要做网络请求（要查外部系统就自己缓存）。
/// </summary>
public interface IRoomGate
{
    RoomVerdict Check(RoomAnnouncement announcement);
}

/// <summary>房间为什么离开了目录。</summary>
public enum RoomRemoval
{
    /// <summary>房主撤下。</summary>
    Withdrawn,
    /// <summary>90 秒没续期。</summary>
    Expired,
    /// <summary>运营者从管理接口删掉（或者封禁了房主）。</summary>
    Removed,
}

/// <summary>
/// 目录事件：房间上线、续期、离开，登录成功或失败。适合写日志、统计、推送到自己的系统。
/// 在目录的锁外面调用；抛出的异常会被吞掉，不影响目录。不要在这里长时间阻塞。
/// </summary>
public interface IDirectoryObserver
{
    void OnAnnounced(RoomListing listing, bool listed, bool isNew) { }
    void OnRemoved(string roomId, RoomRemoval reason) { }
    void OnRejected(RoomListing listing, RoomVerdict verdict) { }
    void OnLogin(bool ok, string? error) { }
}

/// <summary>
/// 自定义请求：op 必须以 <c>x-</c> 开头（比如 <c>x-motd</c>、<c>x-ranking</c>），不会和 Jay 以后加的请求撞名。
/// 请求和回复都是一行 JSON（最长 16 KB），回复里要有 <c>ok</c>。Jay 本身不会发这些请求，给你自己的工具、网页或插件用。
/// </summary>
public interface IDirectoryExtension
{
    string Op { get; }
    JsonObject Handle(JsonObject request, DirectoryView directory);
}

/// <summary>给自定义请求和管理接口看的目录快照（只读）。</summary>
public sealed record DirectoryView(int Rooms, int Listed, IReadOnlyList<RoomListing> ListedRooms);

/// <summary>运营者看到的一个房间（管理接口用）。隐藏房间只计数，不给内容：它们不在公开列表里，运营者不需要知道。</summary>
public sealed record DirectoryRoomInfo(string Id, string Name, string Host, string HostFingerprint, RoomVisibility Mode, int Players, string Game, DateTimeOffset Updated);

public static partial class DirectoryExtensions
{
    public const int MaxReason = 120;

    [GeneratedRegex("^[a-z0-9_]{1,32}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^x-[a-z0-9-]{1,32}$")]
    private static partial Regex OpPattern();

    public static bool IsValidCode(string? code) => code is not null && CodePattern().IsMatch(code);

    public static bool IsValidOp(string? op) => op is not null && OpPattern().IsMatch(op);
}
