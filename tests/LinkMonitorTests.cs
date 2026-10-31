using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IPCountryWatcher
{
    internal sealed class FakeLinkProbe : ILinkProbe
    {
        private readonly List<IPAddress> calls = new List<IPAddress>();
        internal volatile Func<IPAddress, bool> Reachable = address => true;
        internal long Rtt = 350;
        internal int CallCount { get { lock (calls) return calls.Count; } }
        internal IPAddress LastCall { get { lock (calls) return calls[calls.Count - 1]; } }
        public Task<long> HandshakeAsync(IPAddress address, int timeoutMilliseconds, CancellationToken token)
        {
            lock (calls) calls.Add(address);
            return Reachable(address) ? Task.FromResult(Rtt) : Task.FromException<long>(new TimeoutException());
        }
    }

    internal sealed class FakeTraffic : ITrafficCounters
    {
        internal volatile string Id = "tunnel";
        internal volatile bool Answer = true;
        private long sent, received;
        public TrafficSample Read()
        {
            long s = Interlocked.Add(ref sent, 3);
            long r = Answer ? Interlocked.Add(ref received, 2) : Interlocked.Read(ref received);
            return new TrafficSample { InterfaceId = Id, PacketsSent = s, PacketsReceived = r };
        }
    }

    internal sealed class FakeLinkEnvironment : ILinkEnvironment
    {
        internal volatile IPAddress[] Targets = new IPAddress[0];
        internal volatile bool Proxied, FailProxy;
        public IPAddress[] ResolveTargets() { return Targets; }
        public bool IsProxied(bool systemProxy)
        {
            if (FailProxy) throw new InvalidOperationException("WPAD unavailable");
            return Proxied;
        }
    }

    internal sealed class FakePool : IConnectionPool
    {
        internal int Resets;
        public void Reset() { Interlocked.Increment(ref Resets); }
    }

    internal static partial class Tests
    {
        internal static async Task RunLinkTests()
        {
            LatencyTrackerTests();
            await AdaptiveBudgetTests();
            await PooledTransportTests();
            TunnelStallTests();
            LinkHealthTests();
            await LinkMonitorTests();
            LinkBadgeTests();
            IncidentJournalTests();
        }

        private static void LatencyTrackerTests()
        {
            var tracker = new LatencyTracker();
            tracker.Record(100); tracker.Record(5000);
            Check("Two samples are not yet a path latency", tracker.Median() == -1 && tracker.Budget(4000) == 4000);
            tracker.Record(300);
            Check("Fast paths keep the original deadline", tracker.Median() == 300 && tracker.Budget(4000) == 4000);
            for (int i = 0; i < LatencyTracker.Capacity; i++) tracker.Record(1500);
            Check("A slow but answering path earns room for four typical requests", tracker.Budget(4000) == 6000);
            for (int i = 0; i < LatencyTracker.Capacity; i++) tracker.Record(3000);
            Check("Adaptive deadline never exceeds twice the original", tracker.Budget(4000) == 8000);
            for (int i = 0; i < LatencyTracker.Capacity; i++) tracker.Record(90);
            Check("Old latency samples age out of the window", tracker.Median() == 90);
        }

        private static async Task AdaptiveBudgetTests()
        {
            DateTime now = DateTime.UtcNow;
            int delay = 150;
            var transport = new FakeTransport { Handler = async (url, token) => {
                await Task.Delay(delay);
                return url.Contains("cloudflare") ? "ip=8.8.8.8\n" : "1.1.1.1";
            } };
            var learned = new ConnectivityProbe(transport, () => now, 300);
            for (int i = 0; i < 3; i++) await learned.CheckAsync(true, CancellationToken.None);
            delay = 450;
            ConnectivitySnapshot adapted = await learned.CheckAsync(true, CancellationToken.None);
            ConnectivitySnapshot fixedBudget = await new ConnectivityProbe(transport, () => now, 300).CheckAsync(true, CancellationToken.None);
            Check("A path's learned latency lets a slow answer succeed within twice the deadline",
                adapted.Health == ConnectivityHealth.Reachable && fixedBudget.Health == ConnectivityHealth.Unreachable);

            transport = new FakeTransport { Handler = async (url, token) => {
                if (url.StartsWith("https://ipwho.is/")) return Geo("8.8.8.8", "US");
                await Task.Delay(250, token);
                return "8.8.8.8";
            } };
            var service = new LookupService(transport, () => DateTime.UtcNow);
            for (int i = 0; i < 3; i++) await service.QueryAsync(true, CancellationToken.None);
            int hedges = transport.Calls.FindAll(url => url == LookupService.IpUrls[1]).Count;
            Snapshot result = await service.QueryAsync(true, CancellationToken.None);
            Check("Hedge waits for the path's usual latency instead of duplicating every request",
                result.Ip == "8.8.8.8" && service.HedgeDelay() > 300 && service.HedgeDelay() < 800 &&
                transport.Calls.FindAll(url => url == LookupService.IpUrls[1]).Count == hedges);
        }

        private static async Task PooledTransportTests()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int accepted = 0;
            Task server = Task.Run(async () => {
                while (true)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch (Exception) { return; }
                    Interlocked.Increment(ref accepted);
                    ConnectivityTasks.ObserveFault(Task.Run(() => ServeKeepAlive(client)));
                }
            });
            string url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/ip";
            var transport = new HttpTransport();
            try
            {
                string first = await transport.GetAsync(url, false, CancellationToken.None);
                string second = await transport.GetAsync(url, false, CancellationToken.None);
                Check("Pooled transport serves sequential requests over one keep-alive connection",
                    first == "8.8.8.8" && second == "8.8.8.8" && Volatile.Read(ref accepted) == 1);
                transport.Reset();
                await transport.GetAsync(url, false, CancellationToken.None);
                Check("Connection reset opens a fresh connection for the next request", Volatile.Read(ref accepted) == 2);
                transport.Dispose();
                try { await transport.GetAsync(url, false, CancellationToken.None); Check("Disposed transport refuses new requests", false); }
                catch (ObjectDisposedException) { Check("Disposed transport refuses new requests", true); }
            }
            finally { transport.Dispose(); listener.Stop(); }
            await Task.WhenAny(server, Task.Delay(1000));
        }

        // Minimal HTTP/1.1 keep-alive responder: answers every request on a connection.
        private static void ServeKeepAlive(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII))
                {
                    byte[] body = Encoding.ASCII.GetBytes("8.8.8.8");
                    byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + body.Length + "\r\n\r\n");
                    while (true)
                    {
                        string line = reader.ReadLine();
                        if (line == null) return;
                        while (line.Length > 0) { line = reader.ReadLine(); if (line == null) return; }
                        stream.Write(header, 0, header.Length);
                        stream.Write(body, 0, body.Length);
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        private static void TunnelStallTests()
        {
            DateTime t = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            var detector = new TunnelStallDetector();
            Func<long, long, TrafficSample> tunnel = (sent, received) => new TrafficSample { InterfaceId = "tunnel", PacketsSent = sent, PacketsReceived = received };
            detector.Observe(tunnel(10, 10), t);
            Check("Answered traffic is not a stall", detector.Observe(tunnel(13, 12), t.AddSeconds(2)) == TimeSpan.Zero);
            Check("Unanswered traffic counts from the previous sample",
                detector.Observe(tunnel(16, 12), t.AddSeconds(4)) == TimeSpan.FromSeconds(2) &&
                detector.Observe(tunnel(19, 12), t.AddSeconds(6)) == TimeSpan.FromSeconds(4));
            Check("Any received packet ends the stall", detector.Observe(tunnel(22, 13), t.AddSeconds(8)) == TimeSpan.Zero);
            Check("An idle route that sends nothing is not a stall", detector.Observe(tunnel(22, 13), t.AddSeconds(10)) == TimeSpan.Zero);
            Check("Restarted adapter counters are not traffic", detector.Observe(tunnel(1, 0), t.AddSeconds(12)) == TimeSpan.Zero);
            detector.Observe(tunnel(4, 0), t.AddSeconds(14));
            TimeSpan moved = detector.Observe(new TrafficSample { InterfaceId = "physical", PacketsSent = 100, PacketsReceived = 100 }, t.AddSeconds(16));
            Check("A different route interface is a route change, not a stall", detector.RouteChanged && moved == TimeSpan.Zero);
            detector.Observe(null, t.AddSeconds(18));
            detector.Observe(new TrafficSample { InterfaceId = "physical", PacketsSent = 103, PacketsReceived = 100 }, t.AddSeconds(20));
            Check("A route that disappears and returns unchanged is not a change", !detector.RouteChanged);
        }

        private static void LinkHealthTests()
        {
            DateTime t = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            TimeSpan none = TimeSpan.Zero;
            var health = new LinkHealth();
            Check("Link health starts unknown and learns its first answer as the baseline",
                health.State == LinkState.Unknown && health.Observe(true, 350, none, t) == LinkState.Healthy && health.BaselineMs == 350);
            Check("One lost handshake is only suspect, and an answer clears it",
                health.Observe(false, -1, none, t.AddSeconds(2)) == LinkState.Suspect &&
                health.Observe(true, 360, none, t.AddSeconds(3)) == LinkState.Healthy);
            health.Observe(false, -1, none, t.AddSeconds(5));
            health.Observe(false, -1, none, t.AddSeconds(6));
            Check("Three consecutive misses mark the link down",
                health.Observe(false, -1, none, t.AddSeconds(7)) == LinkState.Down && health.DownSinceUtc == t.AddSeconds(7));
            Check("The first answer after an outage only starts recovery", health.Observe(true, 350, none, t.AddSeconds(9)) == LinkState.Recovering);
            Check("A miss during recovery continues the same outage",
                health.Observe(false, -1, none, t.AddSeconds(11)) == LinkState.Down && health.DownSinceUtc == t.AddSeconds(7));
            health.Observe(true, 350, none, t.AddSeconds(13));
            health.Observe(true, 350, none, t.AddSeconds(14));
            Check("Three quick answers are not yet a recovery", health.Observe(true, 350, none, t.AddSeconds(15)) == LinkState.Recovering);
            Check("Recovery is confirmed once answers hold for six seconds, without outage loss lingering",
                health.Observe(true, 350, none, t.AddSeconds(19)) == LinkState.Healthy && health.LossPercent == 0);

            var stalled = new LinkHealth();
            stalled.Observe(true, 350, none, t);
            Check("A miss on a route that answered nothing for four seconds is down at once",
                stalled.Observe(false, -1, TimeSpan.FromSeconds(4), t.AddSeconds(4)) == LinkState.Down);
            Check("A still-stalled route cannot start recovery", stalled.Observe(true, 350, TimeSpan.FromSeconds(6), t.AddSeconds(6)) == LinkState.Down);

            var lossy = new LinkHealth();
            for (int i = 0; i < 8; i++) lossy.Observe(true, 350, none, t.AddSeconds(i));
            lossy.Observe(false, -1, none, t.AddSeconds(8));
            Check("Ten percent lost rounds degrade the link once the miss clears",
                lossy.Observe(true, 350, none, t.AddSeconds(9)) == LinkState.Degraded && lossy.LossPercent == 10);
            LinkState state = LinkState.Degraded;
            for (int i = 10; i < 20; i++) state = lossy.Observe(true, 350, none, t.AddSeconds(i));
            Check("Degradation has hysteresis: five percent loss is not yet healthy", state == LinkState.Degraded && lossy.LossPercent == 5);
            for (int i = 20; i < 29; i++) state = lossy.Observe(true, 350, none, t.AddSeconds(i));
            Check("Degradation clears once the lost round leaves the window", state == LinkState.Healthy);

            var slow = new LinkHealth();
            for (int i = 0; i < 6; i++) slow.Observe(true, 350, none, t.AddSeconds(i));
            slow.Observe(true, 900, none, t.AddSeconds(6));
            state = slow.Observe(true, 900, none, t.AddSeconds(7));
            Check("Two slow answers neither degrade the link nor drag its baseline", state == LinkState.Healthy && Math.Abs(slow.BaselineMs - 350) < 1);
            Check("A sustained doubling of handshake time degrades the link",
                slow.Observe(true, 900, none, t.AddSeconds(8)) == LinkState.Degraded && Math.Abs(slow.BaselineMs - 350) < 1);
            for (int i = 9; i < 12; i++) state = slow.Observe(true, 350, none, t.AddSeconds(i));
            Check("Normal handshake times end the degradation", state == LinkState.Healthy);
            slow.Forget();
            Check("Forgetting a route clears state and baseline", slow.State == LinkState.Unknown && slow.BaselineMs == -1 && slow.MedianRttMs == -1);
        }

        private static async Task<LinkState> LinkRound(LinkMonitor monitor, DateTime now)
        {
            LinkReading reading = await monitor.MeasureAsync(true, now);
            await monitor.EnvironmentTask;
            return monitor.Observe(reading, now);
        }

        private static async Task LinkMonitorTests()
        {
            DateTime t = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            IPAddress lookupHost = IPAddress.Parse("104.16.184.241"), google = IPAddress.Parse("8.8.8.8");
            var probe = new FakeLinkProbe { Reachable = address => false };
            var traffic = new FakeTraffic { Answer = false };
            var environment = new FakeLinkEnvironment { Targets = new[] { lookupHost } };
            var monitor = new LinkMonitor(probe, traffic, environment);
            LinkState state = LinkState.Unknown;
            for (int i = 0; i < 5; i++) state = await LinkRound(monitor, t = t.AddSeconds(2));
            Check("A route where no target has ever answered is never reported down", state == LinkState.Unknown && monitor.TimeoutMilliseconds == 2000);

            probe.Reachable = address => !address.Equals(google);
            traffic.Answer = true;
            await LinkRound(monitor, t = t.AddSeconds(2));
            int before = probe.CallCount;
            state = await LinkRound(monitor, t = t.AddSeconds(2));
            Check("A network that blocks one target is healthy through the others", state == LinkState.Healthy);
            Check("Steady state asks only the preferred, DNS-learned target", probe.CallCount == before + 1 && probe.LastCall.Equals(lookupHost));

            probe.Reachable = address => false;
            traffic.Answer = false;
            state = await LinkRound(monitor, t = t.AddSeconds(2));
            Check("The first failed round is suspect and shortens the next interval",
                state == LinkState.Suspect && monitor.Interval == LinkMonitor.ConfirmInterval);
            before = probe.CallCount;
            state = await LinkRound(monitor, t = t.AddMilliseconds(500));
            Check("Confirmation asks every target at once", state == LinkState.Suspect && probe.CallCount == before + 3);
            state = await LinkRound(monitor, t = t.AddMilliseconds(500));
            Check("Three confirmed misses mark the link down", state == LinkState.Down && monitor.Interval == LinkMonitor.NormalInterval);
            Check("Link summary carries numbers only", monitor.Summary().Contains("lossPct=") && !monitor.Summary().Contains("104.16") && !monitor.Summary().Contains("8.8"));

            probe.Reachable = address => true;
            traffic.Answer = true;
            state = await LinkRound(monitor, t = t.AddSeconds(2));
            Check("An answer after an outage starts recovery", state == LinkState.Recovering);
            await LinkRound(monitor, t = t.AddSeconds(2));
            state = await LinkRound(monitor, t = t.AddSeconds(2));
            LinkState held = await LinkRound(monitor, t = t.AddSeconds(2));
            Check("Recovery is confirmed only after six seconds of answers", state == LinkState.Recovering && held == LinkState.Healthy);
            Check("Handshake timeout follows the route's baseline", monitor.TimeoutMilliseconds == 1400);

            traffic.Id = "physical";
            probe.Reachable = address => false;
            state = await LinkRound(monitor, t = t.AddSeconds(2));
            Check("A new route forgets which targets answered on the old one", state == LinkState.Unknown && monitor.TimeoutMilliseconds == 2000);
            probe.Reachable = address => true;
            probe.Rtt = 30;
            state = await LinkRound(monitor, t = t.AddSeconds(2));
            Check("A new route relearns its baseline instead of reporting degradation",
                state == LinkState.Healthy && monitor.Health.BaselineMs == 30 && monitor.TimeoutMilliseconds == 1000);

            environment.Proxied = true;
            monitor.Invalidate();
            await LinkRound(monitor, t = t.AddSeconds(2));
            before = probe.CallCount;
            state = await LinkRound(monitor, t = t.AddSeconds(2));
            Check("A proxied HTTPS path is not judged by direct handshakes", state == LinkState.Unknown && probe.CallCount == before && monitor.Proxied);
            environment.Proxied = false;
            monitor.Invalidate();
            await LinkRound(monitor, t = t.AddSeconds(2));
            environment.FailProxy = true;
            monitor.Invalidate();
            await LinkRound(monitor, t = t.AddSeconds(2));
            before = probe.CallCount;
            state = await LinkRound(monitor, t = t.AddSeconds(2));
            Check("An unknown proxy decision disables the direct loop", state == LinkState.Unknown && probe.CallCount == before);
            environment.FailProxy = false;
            await LinkRound(monitor, t = t.AddSeconds(31));
            before = probe.CallCount;
            await LinkRound(monitor, t = t.AddSeconds(2));
            Check("A failed proxy check is retried after 30 seconds", !monitor.Proxied && probe.CallCount > before);
        }

        private static void LinkBadgeTests()
        {
            DateTime now = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            var state = new TrayDisplayState();
            state.Accept(Measured(now, ConnectivityHealth.Reachable));
            state.Link = LinkState.Down;
            Check("A confirmed link outage is red even while HTTPS results are fresh", state.Badge(now) == ConnectionBadge.Unconfirmed);
            bool warned = true;
            foreach (LinkState warning in new[] { LinkState.Suspect, LinkState.Degraded, LinkState.Recovering })
            { state.Link = warning; warned &= state.Badge(now) == ConnectionBadge.Limited; }
            Check("Suspect, degraded and recovering links show an early warning", warned);
            state.Link = LinkState.Unknown;
            ConnectionBadge unknown = state.Badge(now);
            state.Link = LinkState.Healthy;
            Check("Unknown and healthy links leave a fresh result unmarked", unknown == ConnectionBadge.None && state.Badge(now) == ConnectionBadge.None);
            state.Accept(Measured(now, ConnectivityHealth.Unreachable, false, false));
            state.Accept(Measured(now, ConnectivityHealth.Reachable));
            state.Accept(Measured(now, ConnectivityHealth.Unreachable, false, false));
            Check("A successful round resets the failed-round confirmation", state.Badge(now) == ConnectionBadge.Limited && !state.FailureConfirmed);
        }

        private static void IncidentJournalTests()
        {
            DateTime now = new DateTime(2026, 11, 1, 1, 2, 3, DateTimeKind.Utc);
            string folder = Path.Combine(Path.GetTempPath(), "IPCountryWatcher-journal-" + Guid.NewGuid().ToString("N"));
            try
            {
                var journal = new IncidentJournal(folder, () => now, 700);
                journal.Record("link", "Healthy->Down; rttMs=-1");
                journal.RecordSnapshot(new[] { "2026-11-01T01:02:00.0000000Z | probe | source=www.cloudflare.com; success=False" });
                string text = File.ReadAllText(journal.FilePath);
                Check("Incident journal persists events with UTC times", text.Contains("2026-11-01T01:02:03.0000000Z | link | Healthy->Down"));
                Check("Outage snapshot copies the session ring as indented lines",
                    text.Contains("snapshot-begin | 1 recent session events") && text.Contains("\r\n    2026-11-01T01:02:00") && text.Contains("snapshot-end"));
                for (int i = 0; i < 20; i++) journal.Record("event", "line-" + i.ToString("D2"));
                string[] tail = journal.Tail(3);
                Check("Journal rolls over at its size limit and keeps two files",
                    File.Exists(Path.Combine(folder, "incidents.1.log")) && new FileInfo(journal.FilePath).Length <= 700 &&
                    Directory.GetFiles(folder).Length == 2);
                Check("Journal tail spans files in order", tail.Length == 3 && tail[0].EndsWith("line-17") && tail[2].EndsWith("line-19"));
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
            string blocked = Path.GetTempFileName();
            try
            {
                var broken = new IncidentJournal(Path.Combine(blocked, "child"), () => now);
                broken.Record("link", "unwritable");
                Check("Journal write failures never affect monitoring", broken.Tail(5).Length == 0);
            }
            finally { File.Delete(blocked); }
        }

        private static void TrayLinkTest()
        {
            DateTime now = DateTime.UtcNow;
            int calls = 0, phase = 0, callsAtDown = 0, resetsAtDown = 0;
            var lookup = new DelegateLookup { Handler = token => { Interlocked.Increment(ref calls); return Task.FromResult(Measured(now, ConnectivityHealth.Reachable)); } };
            var probe = new FakeLinkProbe();
            var traffic = new FakeTraffic();
            var monitor = new LinkMonitor(probe, traffic, new FakeLinkEnvironment());
            var pool = new FakePool();
            string folder = Path.Combine(Path.GetTempPath(), "IPCountryWatcher-tray-" + Guid.NewGuid().ToString("N"));
            var journal = new IncidentJournal(folder, () => now);
            bool healthy = false, down = false, named = false, refreshed = false, recovered = false;
            var voice = new RecordingVoice();
            int spokenAtDown = 0;
            var context = new TrayContext(lookup, new Settings { PollSeconds = 60, NotifyOnChange = false }, true, () => now,
                voice, null, monitor, pool, journal);
            var watch = Stopwatch.StartNew();
            using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
            {
                timer.Tick += (s, e) => {
                    // Ten times real speed keeps the 2 s link cadence and 6 s recovery hold short.
                    now = now.AddMilliseconds(250);
                    if (phase == 0 && context.VisibleLink == LinkState.Healthy && context.VisibleBadge == ConnectionBadge.None)
                    {
                        healthy = context.LinkText.Contains("正常");
                        probe.Reachable = address => false; traffic.Answer = false; phase = 1;
                    }
                    else if (phase == 1 && context.VisibleLink == LinkState.Down)
                    {
                        down = context.VisibleBadge == ConnectionBadge.Unconfirmed && context.VisibleCountry == "CN" && context.Current != null;
                        named = context.TooltipText.StartsWith("链路") && context.LinkText.Contains("中断");
                        callsAtDown = calls; resetsAtDown = pool.Resets;
                        phase = 2;
                    }
                    // Let the tray's own tick announce the outage before the link answers again.
                    else if (phase == 2 && voice.Spoken.Count > 0)
                    { spokenAtDown = voice.Spoken.Count; probe.Reachable = address => true; traffic.Answer = true; phase = 3; }
                    else if (phase == 3 && context.VisibleLink == LinkState.Recovering)
                    { refreshed = pool.Resets > resetsAtDown; phase = 4; }
                    else if (phase == 4 && context.VisibleLink == LinkState.Healthy)
                    { recovered = calls > callsAtDown; phase = 5; }
                    if (phase == 5 || watch.ElapsedMilliseconds > 6000) { timer.Stop(); context.ExitThread(); }
                };
                timer.Start(); Application.Run(context);
            }
            context.Dispose();
            string journalText = File.Exists(journal.FilePath) ? File.ReadAllText(journal.FilePath) : "";
            try { Directory.Delete(folder, true); } catch (IOException) { }
            Check("Real tray keeps an unmarked flag while the fast link loop is healthy", healthy);
            Check("Fast link loop marks red while HTTPS results still look fresh", down);
            Check("Tooltip and menu name the link outage", named);
            Check("Recovery resets pooled connections and rechecks the egress", refreshed && recovered);
            Check("A link outage is announced while the last IP is still fresh, then its recovery once",
                spokenAtDown == 1 && voice.Spoken.Count == 2 && voice.Spoken[0] == "网络连接异常，检测请求均未成功，正在等待重试。" &&
                voice.Spoken[1] == "网络连接已恢复，出口在中国。");
            Check("Incident journal keeps the outage, its snapshot and duration without IPs",
                journalText.Contains("->Down") && journalText.Contains("snapshot-begin") && journalText.Contains("recovered") &&
                !journalText.Contains("8.8.8.8") && !journalText.Contains("1.1.1.1"));
        }
    }
}
