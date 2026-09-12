using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace IPCountryWatcher
{
    internal sealed class ProcessIdentity
    {
        public int Pid;
        public long StartedUtcTicks;
        public string Name;
        public string Path;

        public static ProcessIdentity Read(int pid)
        {
            using (var process = Process.GetProcessById(pid))
            {
                var result = new ProcessIdentity { Pid = pid, Name = process.ProcessName, Path = "" };
                try { result.StartedUtcTicks = process.StartTime.ToUniversalTime().Ticks; }
                catch (Win32Exception) { }
                IntPtr handle = OpenProcess(0x1000, false, pid);
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        var text = new StringBuilder(32768);
                        int size = text.Capacity;
                        if (QueryFullProcessImageName(handle, 0, text, ref size)) result.Path = text.ToString();
                    }
                    finally { CloseHandle(handle); }
                }
                return result;
            }
        }

        public bool IsAlive()
        {
            try
            {
                var current = Read(Pid);
                return StartedUtcTicks != 0 && current.StartedUtcTicks == StartedUtcTicks &&
                    String.Equals(Path, current.Path, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return false; }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }

    internal sealed class TcpConnection
    {
        public int Pid;
        public int State;
        public IPEndPoint Local;
        public IPEndPoint Remote;
    }

    internal static class ProcessNetwork
    {
        // OWNER_PID layouts are parsed explicitly to avoid CLR struct packing assumptions.
        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

        internal static int Port(int native)
        { return ((native & 255) << 8) | ((native >> 8) & 255); }

        internal static List<TcpConnection> ReadTcp()
        {
            var result = new List<TcpConnection>();
            ReadFamily(2, result);
            if (Socket.OSSupportsIPv6) ReadFamily(23, result);
            return result;
        }

        private static void ReadFamily(int family, List<TcpConnection> result)
        {
            int size = 0;
            uint error = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0);
            if (error != 122 && error != 0) throw new Win32Exception((int)error);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (size < 4 || size > 64 * 1024 * 1024) throw new InvalidOperationException("连接表长度无效");
                int capacity = size;
                IntPtr table = Marshal.AllocHGlobal(capacity);
                try
                {
                    error = GetExtendedTcpTable(table, ref size, false, family, 5, 0);
                    if (error == 122) continue;
                    if (error != 0) throw new Win32Exception((int)error);
                    int count = Marshal.ReadInt32(table);
                    int stride = family == 2 ? 24 : 56;
                    if (count < 0 || count > (capacity - 4) / stride) throw new InvalidOperationException("连接表行数无效");
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr row = IntPtr.Add(table, 4 + i * stride);
                        var item = new TcpConnection();
                        if (family == 2)
                        {
                            item.State = Marshal.ReadInt32(row, 0);
                            item.Local = new IPEndPoint(new IPAddress(unchecked((uint)Marshal.ReadInt32(row, 4))), Port(Marshal.ReadInt32(row, 8)));
                            item.Remote = new IPEndPoint(new IPAddress(unchecked((uint)Marshal.ReadInt32(row, 12))), Port(Marshal.ReadInt32(row, 16)));
                            item.Pid = Marshal.ReadInt32(row, 20);
                        }
                        else
                        {
                            var local = new byte[16]; var remote = new byte[16];
                            Marshal.Copy(row, local, 0, 16); Marshal.Copy(IntPtr.Add(row, 24), remote, 0, 16);
                            item.Local = new IPEndPoint(new IPAddress(local, unchecked((uint)Marshal.ReadInt32(row, 16))), Port(Marshal.ReadInt32(row, 20)));
                            item.Remote = new IPEndPoint(new IPAddress(remote, unchecked((uint)Marshal.ReadInt32(row, 40))), Port(Marshal.ReadInt32(row, 44)));
                            item.State = Marshal.ReadInt32(row, 48); item.Pid = Marshal.ReadInt32(row, 52);
                        }
                        result.Add(item);
                    }
                    return;
                }
                finally { Marshal.FreeHGlobal(table); }
            }
            throw new InvalidOperationException("连接变化过快，请刷新重试");
        }

        internal static ProcessIdentity FindOwner(IPEndPoint client, IPEndPoint server)
        {
            var matches = ReadTcp().Where(c => c.State == 5 && c.Local.Equals(client) && c.Remote.Equals(server)).ToArray();
            if (matches.Length != 1) return null;
            try { return ProcessIdentity.Read(matches[0].Pid); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (Win32Exception) { return null; }
        }

        internal static List<ProcessApplication> Applications()
        {
            var apps = new Dictionary<string, ProcessApplication>(StringComparer.OrdinalIgnoreCase);
            var connections = ReadTcp().Where(c => c.State == 5).ToLookup(c => c.Pid);
            var interfaces = new Dictionary<string, string>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                        interfaces[address.Address.ToString()] = adapter.Name;
                }
                catch (NetworkInformationException) { }
            }
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    ProcessIdentity identity;
                    try { identity = ProcessIdentity.Read(process.Id); }
                    catch (ArgumentException) { continue; }
                    catch (InvalidOperationException) { continue; }
                    catch (Win32Exception) { continue; }
                    string key = String.IsNullOrEmpty(identity.Path) ? "pid:" + identity.Pid : identity.Path;
                    ProcessApplication app;
                    if (!apps.TryGetValue(key, out app))
                    {
                        app = new ProcessApplication { Name = identity.Name, Path = identity.Path };
                        apps.Add(key, app);
                    }
                    app.Pids.Add(identity.Pid);
                    app.Identities.Add(identity);
                    foreach (var connection in connections[identity.Pid])
                    {
                        app.TcpCount++;
                        if (!IPAddress.IsLoopback(connection.Remote.Address)) app.NetworkPids.Add(identity.Pid);
                        string address = connection.Local.Address.ToString();
                        string adapter;
                        app.LocalAddresses.Add(address + (interfaces.TryGetValue(address, out adapter) ? " [" + adapter + "]" : ""));
                    }
                }
            }
            return apps.Values.OrderByDescending(a => a.TcpCount).ThenBy(a => a.Name).ToList();
        }
    }

    internal sealed class ProcessApplication
    {
        public string Name;
        public string Path;
        public int TcpCount;
        public readonly List<ProcessIdentity> Identities = new List<ProcessIdentity>();
        public readonly HashSet<int> NetworkPids = new HashSet<int>();
        public readonly List<int> Pids = new List<int>();
        public readonly HashSet<string> LocalAddresses = new HashSet<string>();
    }
}
