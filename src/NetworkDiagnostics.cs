using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace IPCountryWatcher
{
    internal interface INetworkFingerprint
    {
        string Read();
    }

    // Call on a worker: these are local, read-only OS queries, not connectivity probes.
    // The opaque digest is only a change detector. It does not certify reachability,
    // inspect WFP policy, or download/evaluate PAC scripts.
    internal sealed class NetworkFingerprint : INetworkFingerprint
    {
        private readonly Func<IEnumerable<string>> interfaces;
        private readonly Func<IEnumerable<string>> routes;
        private readonly Func<IEnumerable<string>> proxy;

        internal NetworkFingerprint() : this(ReadInterfaces, ReadRoutes, ReadProxy) { }

        internal NetworkFingerprint(Func<IEnumerable<string>> interfaces, Func<IEnumerable<string>> routes,
            Func<IEnumerable<string>> proxy)
        { this.interfaces = interfaces; this.routes = routes; this.proxy = proxy; }

        public string Read()
        {
            var values = new List<string>();
            AddSection(values, "interfaces", interfaces);
            AddSection(values, "routes", routes);
            AddSection(values, "proxy", proxy);
            values.Sort(StringComparer.Ordinal);
            var text = new StringBuilder();
            foreach (string value in values)
                text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }

        private static void AddSection(List<string> target, string name, Func<IEnumerable<string>> reader)
        {
            // A partially enumerated section must not produce an unstable fingerprint.
            var section = new List<string>();
            try
            {
                foreach (string value in reader()) section.Add(name + ":" + value);
                target.Add(name + ":available");
                target.AddRange(section);
            }
            catch (Exception) { target.Add(name + ":unavailable"); }
        }

        private static IEnumerable<string> ReadInterfaces()
        {
            var result = new List<string>();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                string id = adapter.Id;
                result.Add(id + ":status=" + adapter.OperationalStatus);
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                IPInterfaceProperties properties = adapter.GetIPProperties();
                // Negotiated Wi-Fi link speed changes without a routing/configuration change.
                foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                    result.Add(id + ":address=" + address.Address + ":state=" + address.DuplicateAddressDetectionState);
                foreach (GatewayIPAddressInformation address in properties.GatewayAddresses)
                    result.Add(id + ":gateway=" + address.Address);
                // DNS priority is meaningful, unlike the enumeration order of adapters/routes.
                int order = 0;
                foreach (IPAddress address in properties.DnsAddresses)
                    result.Add(id + ":dns[" + (order++).ToString(CultureInfo.InvariantCulture) + "]=" + address);
                result.Add(id + ":suffix=" + properties.DnsSuffix);
            }
            return result;
        }

        private static IEnumerable<string> ReadProxy()
        {
            var result = new List<string>();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
            {
                if (key == null) return new[] { "missing" };
                foreach (string name in new[] { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL", "AutoDetect" })
                    result.Add(name + "=" + Convert.ToString(key.GetValue(name), CultureInfo.InvariantCulture));
                using (RegistryKey connections = key.OpenSubKey("Connections"))
                {
                    if (connections == null) result.Add("connections=missing");
                    else
                    {
                        // Named connection settings can differ from the LAN default.
                        foreach (string name in connections.GetValueNames())
                        {
                            if (name == "SavedLegacySettings") continue;
                            byte[] settings = connections.GetValue(name) as byte[];
                            if (settings != null) result.Add("connection:" + name + "=" + ConnectionSettingsSignature(settings));
                        }
                    }
                }
            }
            return result;
        }

        internal static string ConnectionSettingsSignature(byte[] value)
        {
            if (value == null || value.Length < 12) return "unavailable";
            // Skip version/change counter, retain flags plus proxy/bypass/PAC strings;
            // ignore trailing timestamps/reserved data which are not effective settings.
            var result = new StringBuilder(BitConverter.ToUInt32(value, 8).ToString(CultureInfo.InvariantCulture));
            int offset = 12;
            for (int i = 0; i < 3; i++)
            {
                if (offset > value.Length - 4) return "unavailable";
                uint length = BitConverter.ToUInt32(value, offset);
                offset += 4;
                if (length > value.Length - offset) return "unavailable";
                result.Append(':').Append(Convert.ToBase64String(value, offset, (int)length));
                offset += (int)length;
            }
            return result.ToString();
        }

        // Windows SDK MIB_IPFORWARD_ROW2: preserve native alignment rather than
        // guessing the first-row offset. AF_UNSPEC returns IPv4 and IPv6 routes.
        [StructLayout(LayoutKind.Explicit, Size = 28)]
        private struct SockaddrInet
        {
            [FieldOffset(0)] public ushort Family;
            [FieldOffset(4)] public uint Ipv4;
            [FieldOffset(8)] public uint V6a;
            [FieldOffset(12)] public uint V6b;
            [FieldOffset(16)] public uint V6c;
            [FieldOffset(20)] public uint V6d;
            [FieldOffset(24)] public uint Scope;

            public override string ToString()
            {
                if (Family == 2) return "4:" + Ipv4.ToString("x8", CultureInfo.InvariantCulture);
                if (Family == 23) return "6:" + V6a.ToString("x8", CultureInfo.InvariantCulture) +
                    V6b.ToString("x8", CultureInfo.InvariantCulture) + V6c.ToString("x8", CultureInfo.InvariantCulture) +
                    V6d.ToString("x8", CultureInfo.InvariantCulture) + ":" + Scope.ToString(CultureInfo.InvariantCulture);
                return "family:" + Family.ToString(CultureInfo.InvariantCulture);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AddressPrefix { public SockaddrInet Address; public byte Length; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RouteRow
        {
            public ulong InterfaceLuid;
            public uint InterfaceIndex;
            public AddressPrefix Destination;
            public SockaddrInet NextHop;
            public byte SitePrefixLength;
            public uint ValidLifetime, PreferredLifetime, Metric, Protocol;
            public byte Loopback, AutoconfigureAddress, Publish, Immortal;
            public uint Age, Origin;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RouteTable { public uint Count; public RouteRow First; }

        [DllImport("iphlpapi.dll")]
        private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);
        [DllImport("iphlpapi.dll")]
        private static extern void FreeMibTable(IntPtr table);

        private static IEnumerable<string> ReadRoutes()
        {
            IntPtr table = IntPtr.Zero;
            try
            {
                if (GetIpForwardTable2(0, out table) != 0 || table == IntPtr.Zero)
                    return new[] { "unavailable" };
                int count = Marshal.ReadInt32(table);
                if (count < 0 || count > 65536) return new[] { "unavailable" };
                int offset = Marshal.OffsetOf(typeof(RouteTable), "First").ToInt32();
                int stride = Marshal.SizeOf(typeof(RouteRow));
                var result = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    var row = (RouteRow)Marshal.PtrToStructure(IntPtr.Add(table, offset + i * stride), typeof(RouteRow));
                    // Ignore Age and lifetime countdowns: they change while the route does not.
                    result.Add(row.InterfaceLuid.ToString(CultureInfo.InvariantCulture) + ":" +
                        row.InterfaceIndex.ToString(CultureInfo.InvariantCulture) + ":" + row.Destination.Address + "/" +
                        row.Destination.Length.ToString(CultureInfo.InvariantCulture) + ":" + row.NextHop + ":" +
                        row.Metric.ToString(CultureInfo.InvariantCulture));
                }
                return result;
            }
            finally { if (table != IntPtr.Zero) FreeMibTable(table); }
        }
    }

    internal sealed class DiagnosticLog
    {
        internal const int Capacity = 120;
        private readonly object gate = new object();
        private readonly Queue<string> records = new Queue<string>();
        private readonly Func<DateTime> clock;

        internal DiagnosticLog() : this(() => DateTime.UtcNow) { }
        internal DiagnosticLog(Func<DateTime> clock) { this.clock = clock; }

        // Callers supply safe categories, never raw exceptions, configuration or payloads.
        // Length/control-character limits bound memory and keep one event per line;
        // they are not a general-purpose secret scrubber.
        internal void Record(string eventName, string detail)
        {
            string value = clock().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + " | " +
                OneLine(eventName, 64) + " | " + OneLine(detail, 768);
            lock (gate)
            {
                while (records.Count >= Capacity) records.Dequeue();
                records.Enqueue(value);
            }
        }

        internal void RecordResult(Snapshot snapshot)
        {
            if (snapshot == null || snapshot.Connectivity == null) { Record("measurement", "health=unmeasured"); return; }
            var health = snapshot.Connectivity;
            Record("measurement", "health=" + health.Health + "; path=" + (health.SystemProxy ? "system-proxy" : "system-route") +
                "; checkedUtc=" + health.CheckedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) +
                "; ipObserved=" + snapshot.HasIp + "; countryKnown=" + snapshot.HasCountry + "; countryStale=" + snapshot.CountryIsStale);
            if (health.Results == null) return;
            foreach (var probe in health.Results)
            {
                if (probe == null) continue;
                Record("probe", "source=" + SafeSource(probe.Source) + "; success=" + probe.Success +
                    "; failure=" + SafeCategory(probe.Failure) + "; elapsedMs=" + probe.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            }
        }

        internal string ExportText()
        {
            string[] copy;
            lock (gate) copy = records.ToArray();
            return "IPCountryWatcher network diagnostics\r\nVersion: " + typeof(DiagnosticLog).Assembly.GetName().Version +
                "\r\nUTC times; last 120 session events; export requested by user.\r\n" +
                "Probe results describe the tested path, not every application.\r\n\r\n" + String.Join("\r\n", copy) + "\r\n";
        }

        private static string OneLine(string value, int maximum)
        {
            value = value ?? "";
            if (value.Length > maximum) value = value.Substring(0, maximum);
            var result = new StringBuilder(value.Length);
            foreach (char c in value) result.Append(Char.IsControl(c) ? ' ' : c);
            return result.ToString();
        }

        private static string SafeSource(string value)
        {
            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri) && (uri.Scheme == "https" || uri.Scheme == "http"))
                value = uri.Host;
            else if (!String.IsNullOrEmpty(value) && Uri.TryCreate("https://" + value, UriKind.Absolute, out uri) &&
                uri.HostNameType == UriHostNameType.Dns)
                value = uri.Host;
            IPAddress address;
            if (IPAddress.TryParse(value ?? "", out address)) return "ip-endpoint";
            return SafeCategory(value);
        }

        private static string SafeCategory(string value)
        {
            if (String.IsNullOrEmpty(value)) return "none";
            if (value.Length > 80) return "other";
            foreach (char c in value)
                if (!(Char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')) return "other";
            return value;
        }
    }
}
