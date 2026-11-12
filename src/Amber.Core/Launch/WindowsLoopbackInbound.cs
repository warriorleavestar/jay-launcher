using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Amber.Core.Launch;

/// <summary>
/// Lets the strong sandbox's game accept loopback connections from Jay. The loopback exemption
/// (<c>CheckNetIsolation LoopbackExempt -a</c>) only lets a container connect OUT to loopback; Windows still drops
/// connections INTO a container's listening socket (seen 2026-11-12: Minecraft opened to LAN on :::port inside the
/// container, Jay's 127.0.0.1 and ::1 connects timed out, so opening a room failed). Windows' own server switch
/// (<c>LoopbackExempt -is</c>) only lasts while that admin process keeps running.
/// <para>
/// So the one admin step adds two persistent WFP filters of Jay's own, in Jay's own sublayer: permit inbound loopback
/// (ALE receive/accept, IPv4 and IPv6) for Jay's game container and nothing else. The permit clears the action right,
/// so the lower-weight AppContainer loopback block cannot override it. Only loopback is matched: what the game accepts
/// from the network is still up to the firewall. Undo deletes exactly these three objects by their fixed keys.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsLoopbackInbound
{
    internal static readonly Guid SubLayerKey = new("5a8f3c1e-7d2b-4e69-9a41-6c0b2f7e9d13");
    internal static readonly Guid FilterV4Key = new("5a8f3c1e-7d2b-4e69-9a41-6c0b2f7e9d14");
    internal static readonly Guid FilterV6Key = new("5a8f3c1e-7d2b-4e69-9a41-6c0b2f7e9d16");

    private static readonly Guid LayerRecvAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    private static readonly Guid LayerRecvAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");
    private static readonly Guid ConditionPackageId = new("71bc78fa-f17c-4997-a602-6abb261f351c");
    private static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");

    private static readonly object CacheLock = new();
    private static (bool? Value, long At, bool Valid) _cache;
    private const long CacheMilliseconds = 5000;

    /// <summary>
    /// True when Jay's filters are there, false when they are missing, null when Windows could not say (the filter
    /// engine is not running, an RPC error). Needs no admin: any user may open the engine and look objects up by key;
    /// once the admin step added them the lookup answers "access denied", which means they exist. Settings and the
    /// launch read this often, so the answer is kept for a few seconds (<see cref="Invalidate"/> after the admin step).
    /// </summary>
    public static bool? Check()
    {
        lock (CacheLock)
        {
            var now = Environment.TickCount64;
            if (_cache.Valid && now - _cache.At < CacheMilliseconds) return _cache.Value;
            var value = Query();
            _cache = (value, now, true);
            return value;
        }
    }

    /// <summary>Only "definitely there" counts: an unknown answer is not ready (and not something to undo).</summary>
    public static bool Present() => Check() == true;

    /// <summary>Forgets the cached <see cref="Check"/> answer (call after the admin step ran).</summary>
    public static void Invalidate() { lock (CacheLock) _cache = default; }

    private static bool? Query()
    {
        if (FwpmEngineOpen0(null, RPC_C_AUTHN_DEFAULT, 0, 0, out var engine) != 0) return null;
        try
        {
            bool? all = true;
            foreach (var state in new[]
            {
                Lookup(FwpmSubLayerGetByKey0(engine, SubLayerKey, out var s), s),
                Lookup(FwpmFilterGetByKey0(engine, FilterV4Key, out var f4), f4),
                Lookup(FwpmFilterGetByKey0(engine, FilterV6Key, out var f6), f6),
            })
            {
                if (state == false) return false;
                if (state is null) all = null;
            }
            return all;
        }
        finally { FwpmEngineClose0(engine); }

        static bool? Lookup(uint result, nint memory)
        {
            if (memory != 0) FwpmFreeMemory0(ref memory);
            return result switch
            {
                0 or ERROR_ACCESS_DENIED or E_ACCESSDENIED => true,
                FWP_E_FILTER_NOT_FOUND or FWP_E_SUBLAYER_NOT_FOUND => false,
                _ => null,
            };
        }
    }

    /// <summary>Adds (or re-adds) Jay's sublayer and filters for <paramref name="container"/>. Admin only. 0 when done.</summary>
    public static uint Add(SecurityIdentifier container)
    {
        var sid = new byte[container.BinaryLength];
        container.GetBinaryForm(sid, 0);
        return InTransaction(engine =>
        {
            var removed = RemoveAll(engine);
            if (removed != 0) return removed;
            var name = Marshal.StringToHGlobalUni("Jay game sandbox: loopback into the game");
            var description = Marshal.StringToHGlobalUni("Lets Jay connect into its game sandbox container over loopback (e.g. a world opened to LAN). Added and removed by Jay's sandbox admin step.");
            var sidHandle = GCHandle.Alloc(sid, GCHandleType.Pinned);
            var conditions = (FWPM_FILTER_CONDITION0*)Marshal.AllocHGlobal(sizeof(FWPM_FILTER_CONDITION0) * 2);
            try
            {
                // Highest sublayer weight + CLEAR_ACTION_RIGHT on purpose: the permit must win over the system's AppContainer
                // loopback block, and Windows gives no documented weight just above that sublayer. It also wins over other
                // lower sublayers' blocks, but only for loopback into Jay's own container, which is what an unsandboxed game has.
                var subLayer = new FWPM_SUBLAYER0 { subLayerKey = SubLayerKey, name = name, description = description, flags = FWPM_SUBLAYER_FLAG_PERSISTENT, weight = 0xFFFF };
                var r = FwpmSubLayerAdd0(engine, ref subLayer, 0);
                if (r != 0) return r;
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
                foreach (var (key, layer) in new[] { (FilterV4Key, LayerRecvAcceptV4), (FilterV6Key, LayerRecvAcceptV6) })
                {
                    var filter = new FWPM_FILTER0
                    {
                        filterKey = key, name = name, description = description, layerKey = layer, subLayerKey = SubLayerKey,
                        flags = FWPM_FILTER_FLAG_PERSISTENT | FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT,
                        numFilterConditions = 2, filterCondition = conditions, actionType = FWP_ACTION_PERMIT,
                    };
                    r = FwpmFilterAdd0(engine, ref filter, 0, out _);
                    if (r != 0) return r;
                }
                return 0u;
            }
            finally
            {
                Marshal.FreeHGlobal((nint)conditions);
                sidHandle.Free();
                Marshal.FreeHGlobal(description);
                Marshal.FreeHGlobal(name);
            }
        });
    }

    /// <summary>Deletes Jay's filters and sublayer (missing ones are fine). Admin only. 0 when done, else the first real error.</summary>
    public static uint Remove() => InTransaction(RemoveAll);

    /// <summary>Filters first (the sublayer cannot go while they use it). 0, or the first error other than "not found".</summary>
    private static uint RemoveAll(nint engine)
    {
        foreach (var r in new Func<uint>[]
        {
            () => FwpmFilterDeleteByKey0(engine, FilterV4Key),
            () => FwpmFilterDeleteByKey0(engine, FilterV6Key),
            () => FwpmSubLayerDeleteByKey0(engine, SubLayerKey),
        })
        {
            var result = r();
            if (result is not (0 or FWP_E_FILTER_NOT_FOUND or FWP_E_SUBLAYER_NOT_FOUND)) return result;
        }
        return 0;
    }

    private static uint InTransaction(Func<nint, uint> work)
    {
        var r = FwpmEngineOpen0(null, RPC_C_AUTHN_DEFAULT, 0, 0, out var engine);
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
    private const uint FWPM_SUBLAYER_FLAG_PERSISTENT = 0x1;
    private const uint FWPM_FILTER_FLAG_PERSISTENT = 0x1;
    private const uint FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT = 0x8;
    private const uint FWP_ACTION_PERMIT = 0x2 | 0x1000;
    private const uint FWP_UINT32 = 3;
    private const uint FWP_SID = 13;
    private const uint FWP_MATCH_EQUAL = 0;
    private const uint FWP_MATCH_FLAGS_ALL_SET = 6;
    private const uint FWP_CONDITION_FLAG_IS_LOOPBACK = 0x1;

    // 64-bit layouts (Jay ships x64 and arm64 only), offsets as in fwpmtypes.h / fwptypes.h.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_VALUE0 { public uint type; public nint value; }

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
