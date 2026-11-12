using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Amber.Core.Launch;

/// <summary>
/// Lets the strong sandbox's game accept loopback connections from Jay. The loopback exemption
/// (<c>CheckNetIsolation LoopbackExempt -a</c>) only lets a container connect OUT to loopback; Windows still drops
/// connections INTO a container's listening socket (seen 2026-11-12: Minecraft opened to LAN on :::port inside the
/// container, Jay's 127.0.0.1 and ::1 connects timed out, so opening a room failed). Windows' own server switch
/// (<c>LoopbackExempt -is</c>) only lasts while that admin process keeps running.
/// <para>
/// So the one admin step adds two persistent WFP filters for the Windows user Jay runs as (named on the admin helper's
/// command line before elevation), in a sublayer all Jay users share: permit inbound loopback (ALE receive/accept, IPv4
/// and IPv6) into Jay's game container from that user's processes (remote user condition). Each user's filters have
/// their own keys, derived from the user's SID, so one user's admin step or undo never touches another user's filters.
/// The permit clears the action right, so the lower-weight AppContainer loopback block cannot override it.
/// </para>
/// <para>
/// Trade-offs, accepted on purpose: for that user's loopback into the game this is the standard sandbox's level (any
/// of the user's processes may connect to the game's ports), and it outranks other lower-weight loopback blocks for
/// exactly that traffic. The container's SID is the same for every Windows user on the PC (it is derived from the
/// container name), so a user whose filters exist may also connect into ANOTHER user's sandboxed game over loopback;
/// telling those apart needs a container per user (a separate change). Only loopback is matched: what the game accepts
/// from the network is still up to the firewall.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsLoopbackInbound
{
    /// <summary>Shared by every user's filters; deleted with the last of them.</summary>
    internal static readonly Guid SubLayerKey = new("5a8f3c1e-7d2b-4e69-9a41-6c0b2f7e9d13");
    // The first test builds of PR #267 (never in main) used fixed keys for everybody: …d14/…d16 (no user condition),
    // then …d24/…d26. Every admin step deletes them; Present() never looks at them, so such a machine asks once more.
    private static readonly Guid[] OldFilterKeys =
    [
        new("5a8f3c1e-7d2b-4e69-9a41-6c0b2f7e9d14"), new("5a8f3c1e-7d2b-4e69-9a41-6c0b2f7e9d16"),
        new("5a8f3c1e-7d2b-4e69-9a41-6c0b2f7e9d24"), new("5a8f3c1e-7d2b-4e69-9a41-6c0b2f7e9d26"),
    ];
    /// <summary>A key Jay never creates: its lookup must say "not found" before "access denied" can be read as "exists".</summary>
    internal static readonly Guid ControlKey = new("5a8f3c1e-7d2b-4e69-9a41-6c0b2f7e9dff");

    private static readonly Guid LayerRecvAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    private static readonly Guid LayerRecvAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");
    private static readonly Guid ConditionPackageId = new("71bc78fa-f17c-4997-a602-6abb261f351c");
    private static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private static readonly Guid ConditionRemoteUserId = new("f63073b7-0189-4ab0-95a4-6123cbfab862");

    /// <summary>This user's filter keys: a name-based GUID per user SID and IP version (deterministic, never shared).</summary>
    internal static (Guid V4, Guid V6) KeysFor(SecurityIdentifier user) => (Derive(user, "v4"), Derive(user, "v6"));

    private static Guid Derive(SecurityIdentifier user, string family)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"jay-sandbox-inbound-loopback|{user.Value}|{family}"));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // RFC 4122 version 5 style
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static readonly object CacheLock = new();
    private static (bool? Value, long At, bool Valid) _cache;
    private const long CacheMilliseconds = 5000;

    /// <summary>
    /// True when this Windows user's filters are there, false when they are missing, null when Windows could not say
    /// (the filter engine is not running, an RPC error, a tightened engine). Needs no admin: any user may open the
    /// engine and look objects up by key; once the admin step added them the lookup answers "access denied", which
    /// means they exist. Settings and the launch read this often, so the answer is kept for a few seconds (the lookups
    /// are local RPCs to the filter engine; <see cref="Invalidate"/> after the admin step, <see cref="WarmUp"/> at start).
    /// </summary>
    public static bool? Check()
    {
        lock (CacheLock)
        {
            if (_cache.Valid)
            {
                // A stale answer is still returned at once (settings read this from the UI thread); a fresh one is
                // fetched in the background for the next caller.
                if (Environment.TickCount64 - _cache.At >= CacheMilliseconds) WarmUp();
                return _cache.Value;
            }
        }
        return Refresh();
    }

    /// <summary>Fetches a fresh answer in the background (call at startup so the first read does not wait on the engine).</summary>
    public static void WarmUp()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _ = Task.Run(() => { try { Refresh(); } finally { Volatile.Write(ref _refreshing, 0); } });
    }

    private static int _refreshing;

    private static bool? Refresh()
    {
        // The lookups run outside the lock: two callers may both query, which is harmless.
        using var identity = WindowsIdentity.GetCurrent();
        var value = identity.User is { } user ? Query(user) : null;
        lock (CacheLock) _cache = (value, Environment.TickCount64, true);
        return value;
    }

    /// <summary>Only "definitely there" counts as ready.</summary>
    public static bool Present() => Check() == true;

    /// <summary>Forgets the cached <see cref="Check"/> answer (call after the admin step ran).</summary>
    public static void Invalidate() { lock (CacheLock) _cache = default; }

    /// <summary>Whether this user can open the filter engine at all (tests: a definite answer is expected then).</summary>
    internal static bool EngineReachable()
    {
        if (FwpmEngineOpen0(null, RPC_C_AUTHN_DEFAULT, 0, 0, out var engine) != 0) return false;
        FwpmEngineClose0(engine);
        return true;
    }

    private static bool? Query(SecurityIdentifier user)
    {
        if (FwpmEngineOpen0(null, RPC_C_AUTHN_DEFAULT, 0, 0, out var engine) != 0) return null;
        try
        {
            var (v4, v6) = KeysFor(user);
            return Interpret(
                [Lookup(FwpmSubLayerGetByKey0(engine, ControlKey, out var c1), c1), Lookup(FwpmFilterGetByKey0(engine, ControlKey, out var c2), c2)],
                [
                    Lookup(FwpmSubLayerGetByKey0(engine, SubLayerKey, out var s), s),
                    Lookup(FwpmFilterGetByKey0(engine, v4, out var f4), f4),
                    Lookup(FwpmFilterGetByKey0(engine, v6, out var f6), f6),
                ]);
        }
        finally { FwpmEngineClose0(engine); }
    }

    private static uint Lookup(uint result, nint memory)
    {
        if (memory != 0) FwpmFreeMemory0(ref memory);
        return result;
    }

    /// <summary>
    /// Reads the lookups: the control lookups (keys that never exist) must both say "not found", or nothing can be
    /// told apart (null). Then any "not found" means missing, any other unexpected error means unknown, and found or
    /// "access denied" (Jay's objects are admin-only) means there.
    /// </summary>
    internal static bool? Interpret(IReadOnlyList<uint> controls, IReadOnlyList<uint> lookups)
    {
        if (controls.Any(c => c is not (FWP_E_FILTER_NOT_FOUND or FWP_E_SUBLAYER_NOT_FOUND))) return null;
        bool? all = true;
        foreach (var state in lookups.Select(Classify))
        {
            if (state == false) return false;
            if (state is null) all = null;
        }
        return all;
    }

    /// <summary>One lookup's answer: found or "access denied" = true, "not found" = false, any other error = unknown.</summary>
    internal static bool? Classify(uint result) => result switch
    {
        0 or ERROR_ACCESS_DENIED or E_ACCESSDENIED => true,
        FWP_E_FILTER_NOT_FOUND or FWP_E_SUBLAYER_NOT_FOUND => false,
        _ => null,
    };

    /// <summary>
    /// Adds (or re-adds) <paramref name="user"/>'s filters: loopback into <paramref name="container"/> from that user's
    /// processes only (see the class remarks for what this does and does not separate). Other users' filters are left
    /// alone. Admin only. 0 when done.
    /// </summary>
    public static uint Add(SecurityIdentifier container, SecurityIdentifier user)
    {
        var sid = new byte[container.BinaryLength];
        container.GetBinaryForm(sid, 0);
        var sd = UserDescriptor(user);
        var (v4, v6) = KeysFor(user);
        return InTransaction(engine =>
        {
            var removed = DeleteFilters(engine, [v4, v6, .. OldFilterKeys]);
            if (removed != 0) return removed;
            var name = Marshal.StringToHGlobalUni("Jay game sandbox: loopback into the game");
            var description = Marshal.StringToHGlobalUni("Lets Jay connect into its game sandbox container over loopback (e.g. a world opened to LAN). Added and removed by Jay's sandbox admin step.");
            var sidHandle = GCHandle.Alloc(sid, GCHandleType.Pinned);
            var sdHandle = GCHandle.Alloc(sd, GCHandleType.Pinned);
            var blob = (FWP_BYTE_BLOB*)Marshal.AllocHGlobal(sizeof(FWP_BYTE_BLOB));
            blob->size = (uint)sd.Length;
            blob->data = sdHandle.AddrOfPinnedObject();
            var conditions = (FWPM_FILTER_CONDITION0*)Marshal.AllocHGlobal(sizeof(FWPM_FILTER_CONDITION0) * 3);
            try
            {
                // Highest sublayer weight + CLEAR_ACTION_RIGHT on purpose: the permit must win over the system's AppContainer
                // loopback block, and Windows gives no documented weight just above that sublayer (see the class remarks).
                var subLayer = new FWPM_SUBLAYER0 { subLayerKey = SubLayerKey, name = name, description = description, flags = FWPM_SUBLAYER_FLAG_PERSISTENT, weight = 0xFFFF };
                var r = FwpmSubLayerAdd0(engine, ref subLayer, 0);
                if (r is not (0 or FWP_E_ALREADY_EXISTS)) return r;
                conditions[0] = new FWPM_FILTER_CONDITION0
                {
                    fieldKey = ConditionPackageId, matchType = FWP_MATCH_EQUAL,
                    conditionValue = new FWP_VALUE0 { type = FWP_SID, value = sidHandle.AddrOfPinnedObject() },
                };
                conditions[1] = new FWPM_FILTER_CONDITION0
                {
                    fieldKey = ConditionFlags, matchType = FWP_MATCH_FLAGS_ALL_SET,
                    conditionValue = new FWP_VALUE0 { type = FWP_UINT32, value = (nint)FWP_CONDITION_FLAG_IS_LOOPBACK },
                };
                conditions[2] = new FWPM_FILTER_CONDITION0
                {
                    fieldKey = ConditionRemoteUserId, matchType = FWP_MATCH_EQUAL,
                    conditionValue = new FWP_VALUE0 { type = FWP_SECURITY_DESCRIPTOR_TYPE, value = (nint)blob },
                };
                foreach (var (key, layer) in new[] { (v4, LayerRecvAcceptV4), (v6, LayerRecvAcceptV6) })
                {
                    var filter = new FWPM_FILTER0
                    {
                        filterKey = key, name = name, description = description, layerKey = layer, subLayerKey = SubLayerKey,
                        flags = FWPM_FILTER_FLAG_PERSISTENT | FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT,
                        numFilterConditions = 3, filterCondition = conditions, actionType = FWP_ACTION_PERMIT,
                    };
                    r = FwpmFilterAdd0(engine, ref filter, 0, out _);
                    if (r != 0) return r;
                }
                return 0u;
            }
            finally
            {
                Marshal.FreeHGlobal((nint)conditions);
                Marshal.FreeHGlobal((nint)blob);
                sdHandle.Free();
                sidHandle.Free();
                Marshal.FreeHGlobal(description);
                Marshal.FreeHGlobal(name);
            }
        });
    }

    /// <summary>The security descriptor the remote-user condition checks the connecting process's token against.</summary>
    internal static byte[] UserDescriptor(SecurityIdentifier user)
    {
        // FWP_ACTRL_MATCH_FILTER (CC) for this user only.
        var descriptor = new RawSecurityDescriptor($"O:SYG:SYD:(A;;CC;;;{user.Value})");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    /// <summary>Returned by <see cref="Remove"/> when the filter engine cannot be reached at all (nothing could be checked or removed).</summary>
    public const uint EngineUnavailable = 0xA0000001;

    /// <summary>
    /// Deletes <paramref name="user"/>'s filters (and the first test builds' shared ones), then the shared sublayer
    /// unless another user's filters still use it. Missing objects are fine. Admin only. 0 when done,
    /// <see cref="EngineUnavailable"/> when the engine cannot be opened, else the first real error.
    /// </summary>
    public static uint Remove(SecurityIdentifier user)
    {
        var (v4, v6) = KeysFor(user);
        var r = InTransaction(engine =>
        {
            var deleted = DeleteFilters(engine, [v4, v6, .. OldFilterKeys]);
            if (deleted != 0) return deleted;
            var sub = FwpmSubLayerDeleteByKey0(engine, SubLayerKey);
            return sub is 0 or FWP_E_SUBLAYER_NOT_FOUND or FWP_E_IN_USE ? 0u : sub;
        }, out var opened);
        return opened ? r : EngineUnavailable;
    }

    /// <summary>0, or the first delete error other than "not found".</summary>
    private static uint DeleteFilters(nint engine, IEnumerable<Guid> keys)
    {
        foreach (var key in keys)
        {
            var result = FwpmFilterDeleteByKey0(engine, key);
            if (result is not (0 or FWP_E_FILTER_NOT_FOUND)) return result;
        }
        return 0;
    }

    private static uint InTransaction(Func<nint, uint> work) => InTransaction(work, out _);

    private static uint InTransaction(Func<nint, uint> work, out bool opened)
    {
        var r = FwpmEngineOpen0(null, RPC_C_AUTHN_DEFAULT, 0, 0, out var engine);
        opened = r == 0;
        if (r != 0) return r;
        try
        {
            r = FwpmTransactionBegin0(engine, 0);
            if (r != 0) return r;
            r = work(engine);
            if (r != 0) { FwpmTransactionAbort0(engine); return r; }
            return FwpmTransactionCommit0(engine);
        }
        finally { FwpmEngineClose0(engine); }
    }

    private const uint RPC_C_AUTHN_DEFAULT = 0xFFFFFFFF;
    private const uint ERROR_ACCESS_DENIED = 5;
    private const uint E_ACCESSDENIED = 0x80070005;
    private const uint FWP_E_FILTER_NOT_FOUND = 0x80320003;
    private const uint FWP_E_SUBLAYER_NOT_FOUND = 0x80320007;
    private const uint FWP_E_IN_USE = 0x8032000A;
    private const uint FWP_E_ALREADY_EXISTS = 0x80320009;
    private const uint FWPM_SUBLAYER_FLAG_PERSISTENT = 0x1;
    private const uint FWPM_FILTER_FLAG_PERSISTENT = 0x1;
    private const uint FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT = 0x8;
    private const uint FWP_ACTION_PERMIT = 0x2 | 0x1000;
    private const uint FWP_UINT32 = 3;
    private const uint FWP_SID = 13;
    private const uint FWP_SECURITY_DESCRIPTOR_TYPE = 14;
    private const uint FWP_MATCH_EQUAL = 0;
    private const uint FWP_MATCH_FLAGS_ALL_SET = 6;
    private const uint FWP_CONDITION_FLAG_IS_LOOPBACK = 0x1;

    // 64-bit layouts (Jay ships x64 and arm64 only), offsets as in fwpmtypes.h / fwptypes.h.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_VALUE0 { public uint type; public nint value; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_BYTE_BLOB { public uint size; public nint data; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct FWPM_FILTER_CONDITION0
    {
        [FieldOffset(0)] public Guid fieldKey;
        [FieldOffset(16)] public uint matchType;
        [FieldOffset(24)] public FWP_VALUE0 conditionValue;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    internal struct FWPM_SUBLAYER0
    {
        [FieldOffset(0)] public Guid subLayerKey;
        [FieldOffset(16)] public nint name;
        [FieldOffset(24)] public nint description;
        [FieldOffset(32)] public uint flags;
        [FieldOffset(40)] public nint providerKey;
        [FieldOffset(48)] public uint providerDataSize;
        [FieldOffset(56)] public nint providerData;
        [FieldOffset(64)] public ushort weight;
    }

    [StructLayout(LayoutKind.Explicit, Size = 200)]
    internal unsafe struct FWPM_FILTER0
    {
        [FieldOffset(0)] public Guid filterKey;
        [FieldOffset(16)] public nint name;
        [FieldOffset(24)] public nint description;
        [FieldOffset(32)] public uint flags;
        [FieldOffset(40)] public nint providerKey;
        [FieldOffset(48)] public uint providerDataSize;
        [FieldOffset(56)] public nint providerData;
        [FieldOffset(64)] public Guid layerKey;
        [FieldOffset(80)] public Guid subLayerKey;
        [FieldOffset(96)] public FWP_VALUE0 weight; // FWP_EMPTY: the engine picks the weight
        [FieldOffset(112)] public uint numFilterConditions;
        [FieldOffset(120)] public FWPM_FILTER_CONDITION0* filterCondition;
        [FieldOffset(128)] public uint actionType;
        [FieldOffset(132)] public Guid actionFilterType;
        [FieldOffset(152)] public ulong rawContext;
        [FieldOffset(168)] public nint reserved;
        [FieldOffset(176)] public ulong filterId;
        [FieldOffset(184)] public FWP_VALUE0 effectiveWeight;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmEngineOpen0(string? server, uint authn, nint authIdentity, nint session, out nint engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineClose0(nint engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionBegin0(nint engine, uint flags);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionCommit0(nint engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionAbort0(nint engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerAdd0(nint engine, ref FWPM_SUBLAYER0 subLayer, nint sd);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerDeleteByKey0(nint engine, in Guid key);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerGetByKey0(nint engine, in Guid key, out nint subLayer);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterAdd0(nint engine, ref FWPM_FILTER0 filter, nint sd, out ulong id);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterDeleteByKey0(nint engine, in Guid key);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterGetByKey0(nint engine, in Guid key, out nint filter);
    [DllImport("fwpuclnt.dll")] private static extern void FwpmFreeMemory0(ref nint memory);
}
