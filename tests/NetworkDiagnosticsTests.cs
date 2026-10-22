using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace IPCountryWatcher
{
    internal static partial class Tests
    {
        private static void RunNetworkDiagnosticsTests()
        {
            DateTime now = new DateTime(2026, 10, 22, 4, 0, 0, DateTimeKind.Utc);
            var log = new DiagnosticLog(() => now);
            for (int i = 0; i < DiagnosticLog.Capacity + 7; i++)
                log.Record("event-" + i.ToString("D3"), "bounded");
            string exported = log.ExportText();
            Check("Diagnostic ring discards oldest events at capacity",
                !exported.Contains("event-006") && exported.Contains("event-007") && exported.Contains("event-126") &&
                exported.Split(new[] { " | " }, StringSplitOptions.None).Length == DiagnosticLog.Capacity * 2 + 1);
            Check("Diagnostic entries use explicit UTC timestamps", exported.Contains("2026-10-22T04:00:00.0000000Z"));
            Check("Diagnostic version remains available after the start event is evicted",
                exported.Contains("Version: " + typeof(DiagnosticLog).Assembly.GetName().Version));

            var parallel = new DiagnosticLog(() => now);
            Parallel.For(0, 1000, i => parallel.Record("parallel", i.ToString()));
            Check("Concurrent diagnostics remain bounded and exportable",
                parallel.ExportText().Split(new[] { " | parallel | " }, StringSplitOptions.None).Length == DiagnosticLog.Capacity + 1);
            var multiline = new DiagnosticLog(() => now);
            multiline.Record("new\r\nline", new string('a', 2000) + "\nend");
            string bounded = multiline.ExportText();
            Check("Diagnostic event text cannot inject extra lines or grow without bound",
                bounded.Contains("new  line") && !bounded.Contains("\nend") && bounded.Length < 1100);

            var snapshot = new Snapshot {
                Ip = "203.0.113.57", CountryCode = "CN", CountryIsStale = true,
                Error = "secret-exception", Source = "secret-egress-source",
                Connectivity = new ConnectivitySnapshot {
                    Health = ConnectivityHealth.Limited, CheckedUtc = now, SystemProxy = true,
                    Results = new[] {
                        new ProbeResult { Source = "https://private-user:private-password@example.com/private-path?private-token=yes",
                            Success = false, Failure = "Timeout", ElapsedMilliseconds = 4000 },
                        new ProbeResult { Source = "203.0.113.58", Success = false,
                            Failure = "raw exception with credentials", ElapsedMilliseconds = 12 },
                        new ProbeResult { Source = "www.cloudflare.com/cdn-cgi/trace", Success = true,
                            Failure = null, ElapsedMilliseconds = 80 },
                        new ProbeResult { Source = "ifconfig.me/ip", Success = true,
                            Failure = null, ElapsedMilliseconds = 90 }
                    }
                }
            };
            var resultLog = new DiagnosticLog(() => now);
            resultLog.RecordResult(snapshot);
            string details = resultLog.ExportText();
            Check("Diagnostic result preserves useful health and failure categories",
                details.Contains("health=Limited") && details.Contains("system-proxy") && details.Contains("source=example.com") &&
                details.Contains("failure=Timeout") && details.Contains("elapsedMs=4000"));
            Check("Diagnostic result excludes IPs, error text, URL credentials, paths and query strings",
                !details.Contains("203.0.113.") && !details.Contains("secret-") && !details.Contains("private-") &&
                !details.Contains("raw exception") && details.Contains("source=ip-endpoint"));
            Check("Diagnostic source names preserve production probe hosts without paths",
                details.Contains("source=www.cloudflare.com;") && details.Contains("source=ifconfig.me;") &&
                !details.Contains("/cdn-cgi/trace") && !details.Contains("/ip"));
            resultLog.RecordResult(new Snapshot());
            Check("Missing connectivity data is recorded as unmeasured", resultLog.ExportText().Contains("health=unmeasured"));

            string route = "v4:default:gateway-a:metric-10";
            string proxy = "DIRECT|PAC-a";
            bool reverse = false, unavailable = false;
            var fingerprint = new NetworkFingerprint(
                () => reverse ? new[] { "b:up", "a:up" } : new[] { "a:up", "b:up" },
                () => new[] { route },
                () => { if (unavailable) throw new InvalidOperationException("sensitive configuration"); return new[] { proxy }; });
            string initial = fingerprint.Read();
            reverse = true;
            Check("Fingerprint is stable when OS enumeration order changes", initial == fingerprint.Read());
            route = "v4:default:gateway-b:metric-10";
            string moved = fingerprint.Read();
            Check("Route-only changes invalidate the network fingerprint", initial != moved);
            proxy = "DIRECT|PAC-b";
            string changedProxy = fingerprint.Read();
            Check("Proxy-only changes invalidate the network fingerprint", moved != changedProxy);
            unavailable = true;
            string missing = fingerprint.Read();
            Check("Failed fingerprint section has a stable marker and detects recovery",
                missing == fingerprint.Read() && missing != changedProxy);
            unavailable = false;
            Check("Fingerprint recovers after local query permission failure", fingerprint.Read() == changedProxy);
            Check("Fingerprint returns only an opaque hash", Convert.FromBase64String(initial).Length == 32 && !initial.Contains("gateway"));

            byte[] settings = ProxyBlob(5, "proxy", "bypass", "http://localhost/pac");
            string settingsHash = NetworkFingerprint.ConnectionSettingsSignature(settings);
            settings[4]++;
            Check("Proxy change counter does not cause false network changes",
                settingsHash == NetworkFingerprint.ConnectionSettingsSignature(settings));
            settings[8] = 1;
            Check("PAC activation flags are part of network fingerprint",
                settingsHash != NetworkFingerprint.ConnectionSettingsSignature(settings));
            Check("Truncated proxy blobs fail safely with a stable marker",
                NetworkFingerprint.ConnectionSettingsSignature(new byte[] { 1, 2 }) == "unavailable" &&
                NetworkFingerprint.ConnectionSettingsSignature(new byte[13]) == "unavailable");
        }

        private static byte[] ProxyBlob(uint flags, params string[] values)
        {
            var result = new List<byte>(new byte[8]);
            result.AddRange(BitConverter.GetBytes(flags));
            foreach (string value in values)
            {
                byte[] encoded = Encoding.UTF8.GetBytes(value);
                result.AddRange(BitConverter.GetBytes(encoded.Length));
                result.AddRange(encoded);
            }
            return result.ToArray();
        }
    }
}
