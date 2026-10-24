using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace EveUpdateHelper;

internal sealed record LauncherConnection(int PID, string Process, string RemoteIP, int RemotePort);

internal static class LauncherConnections
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sorted, int family, int tableClass, uint reserved);

    internal static bool IsLauncher(string name) => new[] { "evelauncher", "eve-online", "eve-online-launcher", "EVE Launcher", "EVE Online" }
        .Contains(name, StringComparer.OrdinalIgnoreCase);

    internal static Dictionary<int, string> Processes()
    {
        var result = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
            using (p)
                try { if (IsLauncher(p.ProcessName)) result[p.Id] = p.ProcessName; }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
        return result;
    }

    internal static List<LauncherConnection> ParseTable(byte[] data, bool ipv6, IReadOnlyDictionary<int, string> processes)
    {
        int stride = ipv6 ? 56 : 24;
        if (data.Length < 4) throw new IOException("TCP 表头不完整。");
        uint count = BitConverter.ToUInt32(data, 0);
        if (count > (data.Length - 4) / stride) throw new IOException("TCP 表长度异常。");
        var result = new List<LauncherConnection>();
        for (int i = 0; i < count; i++)
        {
            int start = 4 + i * stride;
            int state = BitConverter.ToInt32(data, start + (ipv6 ? 48 : 0));
            int pid = BitConverter.ToInt32(data, start + (ipv6 ? 52 : 20));
            if (state != 5 || !processes.TryGetValue(pid, out var name)) continue;
            int addressOffset = start + (ipv6 ? 24 : 12), portOffset = start + (ipv6 ? 44 : 16);
            var ip = ipv6 ? new IPAddress(data.AsSpan(addressOffset, 16), BitConverter.ToUInt32(data, start + 40))
                : new IPAddress(data.AsSpan(addressOffset, 4));
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            result.Add(new(pid, name, ip.ToString(), (data[portOffset] << 8) | data[portOffset + 1]));
        }
        return result;
    }

    internal static List<LauncherConnection> Snapshot(IReadOnlyDictionary<int, string> processes, bool ipv6)
    {
        if (processes.Count == 0) return [];
        int size = 0;
        uint code = GetExtendedTcpTable(IntPtr.Zero, ref size, false, ipv6 ? 23 : 2, 5, 0);
        if (code != 122 && code != 0) throw new Win32Exception((int)code);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (size < 4 || size > 64 * 1024 * 1024) throw new IOException("TCP 表大小异常。");
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                int capacity = size;
                code = GetExtendedTcpTable(memory, ref size, false, ipv6 ? 23 : 2, 5, 0);
                if (code == 122) continue;
                if (code != 0) throw new Win32Exception((int)code);
                var data = new byte[capacity];
                Marshal.Copy(memory, data, 0, capacity);
                return ParseTable(data, ipv6, processes);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        throw new IOException("TCP 连接表变化过快，请重新检查。");
    }
}
