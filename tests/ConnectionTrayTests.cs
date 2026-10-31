using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IPCountryWatcher
{
    internal sealed class ChangingFingerprint : INetworkFingerprint
    {
        internal string Value = "first-network";
        public string Read() { return Value; }
    }

    internal static partial class Tests
    {
        private static Snapshot Measured(DateTime now, ConnectivityHealth health, bool hasIp = true, bool hasCountry = true)
        {
            return new Snapshot { Ip = hasIp ? "8.8.8.8" : null, CountryCode = hasCountry ? "CN" : null,
                Country = hasCountry ? "中国" : null, CheckedUtc = now,
                Connectivity = new ConnectivitySnapshot { Health = health, CheckedUtc = now, Results = new ProbeResult[0] } };
        }

        private static void ConnectionPresentationTests()
        {
            DateTime now = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
            var state = new TrayDisplayState();
            state.Accept(Measured(now, ConnectivityHealth.Reachable));
            Check("Fresh country and independent health display an unmarked flag", state.Badge(now) == ConnectionBadge.None && state.IsIpFresh(now));
            Check("Thirty-second freshness expires even without another result", state.Badge(now.AddSeconds(30)) == ConnectionBadge.Pending && !state.IsIpFresh(now.AddSeconds(30)));
            Check("Clock rollback never presents a healthy or copyable result", state.Health(now.AddSeconds(-1)) == ConnectivityHealth.Checking && !state.IsIpFresh(now.AddSeconds(-1)));
            state.Invalidate(false);
            Check("Refresh immediately marks a retained flag, before lookup finishes", state.Displayed(now).CountryCode == "CN" && state.Badge(now) == ConnectionBadge.Pending && !state.IsIpFresh(now));
            state.Accept(Measured(now, ConnectivityHealth.Limited));
            Check("A real Chinese IP with failed health targets cannot look fully healthy", state.Badge(now) == ConnectionBadge.Limited && state.IsIpFresh(now));
            state.Accept(Measured(now, ConnectivityHealth.Unreachable, false, false));
            Check("One fully failed round warns while it is rechecked", state.Badge(now) == ConnectionBadge.Limited && !state.FailureConfirmed &&
                state.Displayed(now).CountryCode == "CN" && !state.IsIpFresh(now));
            state.Accept(Measured(now, ConnectivityHealth.Unreachable, false, false));
            Check("Failed probes visibly mark the old country as unconfirmed", state.Badge(now) == ConnectionBadge.Unconfirmed && state.Displayed(now).CountryCode == "CN" && !state.IsIpFresh(now));
            state.Accept(Measured(now, ConnectivityHealth.Reachable, true, false));
            Check("Country lookup failure does not erase successful connectivity", state.Displayed(now) == null && state.Health(now) == ConnectivityHealth.Reachable && state.Badge(now) == ConnectionBadge.None);
            state.Accept(Measured(now, ConnectivityHealth.Reachable, false, false));
            Check("Healthy probes without a public-IP result do not invent an egress", state.Health(now) == ConnectivityHealth.Reachable && state.Badge(now) == ConnectionBadge.Pending && !state.IsIpFresh(now));
            state.Accept(Measured(now, ConnectivityHealth.Reachable));
            Check("Historical country has a hard lifetime even while Current remains set", state.Displayed(now.AddMinutes(2)) == null);
            ConnectionIconPreview();
        }

        private static void ConnectionIconPreview()
        {
            Directory.CreateDirectory("test-results");
            using (var bitmap = new Bitmap(640, 150))
            using (var g = Graphics.FromImage(bitmap))
            using (var font = new Font("Segoe UI", 11))
            {
                g.Clear(Color.FromArgb(245, 246, 248));
                var badges = new[] { ConnectionBadge.None, ConnectionBadge.Pending, ConnectionBadge.Limited, ConnectionBadge.Unconfirmed };
                for (int i = 0; i < badges.Length; i++)
                {
                    using (var large = FlagIcons.Create("CN", 32, badges[i]))
                    using (var small = FlagIcons.Create("CN", 16, badges[i]))
                    using (var globe = FlagIcons.Create(null, 16, badges[i]))
                    {
                        g.DrawIcon(large, i * 160 + 20, 15);
                        g.DrawIcon(small, i * 160 + 25, 65);
                        g.DrawIcon(globe, i * 160 + 65, 65);
                    }
                    g.DrawString(badges[i].ToString(), font, Brushes.Black, i * 160 + 10, 104);
                }
                bitmap.Save("test-results/connection-status-preview.png");
            }
            using (var good = FlagIcons.Create("CN", 16, ConnectionBadge.None))
            using (var pending = FlagIcons.Create("CN", 16, ConnectionBadge.Pending))
            using (var failed = FlagIcons.Create("CN", 16, ConnectionBadge.Unconfirmed))
            using (var a = good.ToBitmap())
            using (var b = pending.ToBitmap())
            using (var c = failed.ToBitmap())
            {
                bool amberChanged = false, redChanged = false;
                for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
                { amberChanged |= a.GetPixel(x, y) != b.GetPixel(x, y); redChanged |= b.GetPixel(x, y) != c.GetPixel(x, y); }
                Check("Pending and failed icons differ visibly at actual 16px tray size", amberChanged && redChanged);
            }
        }

        private static void TrayConnectivityTest()
        {
            int calls = 0, seen = 0;
            var lookup = new DelegateLookup { Handler = token => {
                int call = Interlocked.Increment(ref calls);
                bool down = call == 3 || call == 4;
                return Task.FromResult(Measured(DateTime.UtcNow, call == 2 ? ConnectivityHealth.Limited :
                    down ? ConnectivityHealth.Unreachable : ConnectivityHealth.Reachable, !down, !down));
            } };
            bool limited = false, rechecking = false, failed = false, recovered = false;
            var context = new TrayContext(lookup, new Settings { PollSeconds = 60, NotifyOnChange = false }, true);
            var watch = Stopwatch.StartNew();
            using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
            {
                timer.Tick += (s, e) => {
                    if (context.Current != null && seen < calls)
                    {
                        seen = calls;
                        if (seen == 2) limited = context.VisibleCountry == "CN" && context.VisibleBadge == ConnectionBadge.Limited && context.ConnectivityText.Contains("部分");
                        if (seen == 3) rechecking = context.VisibleCountry == "CN" && context.VisibleBadge == ConnectionBadge.Limited &&
                            context.ConnectivityText.Contains("复核") && !context.CanCopyIp;
                        if (seen == 4) failed = context.VisibleCountry == "CN" && context.VisibleBadge == ConnectionBadge.Unconfirmed && !context.CanCopyIp;
                        if (seen == 5) recovered = context.VisibleBadge == ConnectionBadge.None && context.CanCopyIp;
                        if (seen < 5) context.RequestRefresh();
                    }
                    if (seen >= 5 || watch.ElapsedMilliseconds > 3000) { timer.Stop(); context.ExitThread(); }
                };
                timer.Start(); Application.Run(context);
            }
            context.Dispose();
            Check("Real tray rechecks one fully failed round before marking it red", rechecking);
            Check("Real tray distinguishes partial availability, failure and recovery", limited && failed && recovered);
        }

        private static void TrayFreshnessDuringRequestTest()
        {
            DateTime now = DateTime.UtcNow;
            int calls = 0, phase = 0;
            var pending = new TaskCompletionSource<Snapshot>();
            var lookup = new DelegateLookup { Handler = token => Interlocked.Increment(ref calls) == 1 ?
                Task.FromResult(Measured(now, ConnectivityHealth.Reachable)) : pending.Task };
            bool expired = false;
            var context = new TrayContext(lookup, new Settings { PollSeconds = 5, NotifyOnChange = false }, true, () => now);
            var watch = Stopwatch.StartNew();
            using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
            {
                timer.Tick += (s, e) => {
                    if (phase == 0 && context.Current != null) { now = now.AddSeconds(6); phase = 1; }
                    else if (phase == 1 && calls == 2) { now = now.AddSeconds(30); phase = 2; }
                    else if (phase == 2) expired = context.VisibleCountry == "CN" && context.VisibleBadge == ConnectionBadge.Pending && !context.CanCopyIp;
                    if (expired || watch.ElapsedMilliseconds > 2000) { timer.Stop(); context.ExitThread(); }
                };
                timer.Start(); Application.Run(context);
            }
            context.Dispose(); pending.TrySetResult(Measured(now, ConnectivityHealth.Reachable));
            Check("In-flight query cannot keep an expired healthy flag or copyable IP", expired);
        }

        private static void TrayFingerprintTest()
        {
            DateTime now = DateTime.UtcNow;
            int calls = 0, phase = 0;
            var fingerprint = new ChangingFingerprint();
            var lookup = new DelegateLookup { Handler = token => { Interlocked.Increment(ref calls); return Task.FromResult(Measured(now, ConnectivityHealth.Reachable)); } };
            var context = new TrayContext(lookup, new Settings { PollSeconds = 60, NotifyOnChange = false }, true, () => now, null, fingerprint);
            var watch = Stopwatch.StartNew();
            bool rechecked = false, rollbackRechecked = false;
            using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
            {
                timer.Tick += (s, e) => {
                    if (phase == 0 && calls == 1 && watch.ElapsedMilliseconds >= 200)
                    { fingerprint.Value = "different-route-and-dns"; now = now.AddSeconds(6); phase = 1; }
                    if (phase == 1 && calls >= 2 && context.Current != null)
                    { rechecked = true; now = now.AddHours(-1); phase = 2; }
                    if (phase == 2 && calls >= 3 && context.Current != null) rollbackRechecked = true;
                    if (rollbackRechecked || watch.ElapsedMilliseconds > 2000) { timer.Stop(); context.ExitThread(); }
                };
                timer.Start(); Application.Run(context);
            }
            context.Dispose();
            Check("Route/DNS fingerprint change rechecks without a network address event", rechecked);
            Check("Clock rollback cannot postpone network revalidation until old wall time", rollbackRechecked);
        }
    }
}
