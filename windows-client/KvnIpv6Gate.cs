using System.Runtime.InteropServices;

namespace VPNSL.Windows;

/// <summary>
/// Temporary process-owned WFP filters blocking IPv6 outside an IPv4-only CSQTT
/// tunnel. Filters belong to a dynamic WFP session and disappear on close/crash.
/// </summary>
internal sealed class KvnIpv6Guard : IDisposable
{
    private static readonly Guid ConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid PacketV6 = new("a3b3ab6b-3564-488c-9117-f34e82142763");
    private IntPtr _engine;
    private KvnIpv6Guard(IntPtr engine) => _engine = engine;

    public static KvnIpv6Guard Enable()
    {
        CheckNativeLayout();
        var session = new Session
        {
            Display = new Display { Name = "KVN CSQTT IPv6 guard",
                Description = "Temporary CSQTT protection; removed on disconnect" },
            Flags = 1 // FWPM_SESSION_FLAG_DYNAMIC
        };
        uint status = FwpmEngineOpen0(null, 10, IntPtr.Zero, ref session, out var handle);
        if (status != 0 || handle == IntPtr.Zero)
            throw Error("Не удалось открыть сессию Windows WFP", status);
        var guard = new KvnIpv6Guard(handle);
        try
        {
            guard.AddBlock(ConnectV6, "KVN CSQTT: IPv6 connections");
            guard.AddBlock(PacketV6, "KVN CSQTT: IPv6 packets");
            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    private void AddBlock(Guid layer, string name)
    {
        var filter = new Filter
        {
            Display = new Display { Name = name,
                Description = "Dynamic IPv6 leak prevention for IPv4 CSQTT" },
            LayerKey = layer, SubLayerKey = Guid.Empty,
            Weight = new Value { Type = 1, Number = 15 }, // FWP_UINT8 max weight index
            Action = new Action { Type = 0x1001 } // FWP_ACTION_BLOCK
        };
        uint status = FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out _);
        if (status != 0) throw Error("Не удалось установить WFP-фильтр IPv6", status);
    }

    public void Dispose()
    {
        var handle = _engine;
        if (handle == IntPtr.Zero) return;
        _engine = IntPtr.Zero;
        uint status = FwpmEngineClose0(handle);
        if (status != 0) throw Error("Не удалось удалить защитные WFP-фильтры", status);
    }

    internal static void CheckNativeLayout()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("CSQTT WFP поддерживается в Windows x64.");
        if (Marshal.SizeOf<Session>() != 72 || Marshal.SizeOf<Value>() != 16 ||
            Marshal.SizeOf<Action>() != 24 || Marshal.SizeOf<Filter>() != 200 ||
            Marshal.OffsetOf<Filter>(nameof(Filter.Action)).ToInt32() != 128 ||
            Marshal.OffsetOf<Filter>(nameof(Filter.FilterId)).ToInt32() != 176)
            throw new InvalidOperationException("Ошибка ABI структур Windows Filtering Platform.");
    }

    private static InvalidOperationException Error(string text, uint status) =>
        new(text + " (0x" + status.ToString("X8") +
            "). CSQTT не запущен во избежание утечки IPv6.");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Display
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Description;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Session
    {
        public Guid Key;
        public Display Display;
        public uint Flags, Timeout, Pid;
        public IntPtr Sid, Username;
        public int KernelMode;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public uint Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Value { public uint Type, Padding; public ulong Number; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Action { public uint Type, Padding; public Guid FilterType; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Filter
    {
        public Guid Key;
        public Display Display;
        public uint Flags;
        public IntPtr Provider;
        public Blob ProviderData;
        public Guid LayerKey, SubLayerKey;
        public Value Weight;
        public uint ConditionsCount;
        public IntPtr Conditions;
        public Action Action;
        public Guid Context;
        public IntPtr Reserved;
        public ulong FilterId;
        public Value EffectiveWeight;
    }
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint FwpmEngineOpen0(
        [MarshalAs(UnmanagedType.LPWStr)] string? serverName, uint authn,
        IntPtr identity, ref Session session, out IntPtr handle);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmFilterAdd0(
        IntPtr handle, ref Filter filter, IntPtr sd, out ulong id);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmEngineClose0(IntPtr handle);
}
