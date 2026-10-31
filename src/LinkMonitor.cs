using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace IPCountryWatcher
{
    // Unknown: no target has answered on this route yet, or a proxy carries HTTPS, so
    // silence proves nothing. Unknown and Healthy never change the tray badge.
    internal enum LinkState { Unknown, Healthy, Degraded, Suspect, Down, Recovering }

    internal interface ILinkProbe
    {
        // Elapsed milliseconds of a completed TCP handshake; throws on failure or timeout.
        Task<long> HandshakeAsync(IPAddress address, int timeoutMilliseconds, CancellationToken token);
    }

    internal sealed class TrafficSample
    {
        public string InterfaceId;
        public long PacketsSent;
        public long PacketsReceived;
    }

    internal interface ITrafficCounters
    {
        // Unicast counters of the interface routing Internet traffic now (the tunnel while a
        // VPN is up); null when it cannot be determined.
        TrafficSample Read();
    }

    internal interface ILinkEnvironment
    {
        // Worker-thread calls, bounded by the caller.
        IPAddress[] ResolveTargets();
        bool IsProxied(bool systemProxy);
    }

    internal sealed class LinkAttempt
    {
        public IPAddress Address;
        public bool Success;
        public long ElapsedMilliseconds;
    }

    internal sealed class LinkReading
    {
        public bool Proxied;
        public LinkAttempt[] Attempts;
        public TrafficSample Traffic;
    }

    // One round trip and three small packets; no data is sent. VPNs commonly filter ICMP
    // (Surfshark drops every ping), so a handshake to port 443 stands in for ping.
    internal sealed class TcpLinkProbe : ILinkProbe
    {
        public async Task<long> HandshakeAsync(IPAddress address, int timeoutMilliseconds, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            using (var client = new TcpClient(address.AddressFamily))
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task connect = client.ConnectAsync(address, 443);
                ConnectivityTasks.ObserveFault(connect);
                try
                {
                    if (await Task.WhenAny(connect, Task.Delay(timeoutMilliseconds, deadline.Token)).ConfigureAwait(false) != connect)
                    {
                        token.ThrowIfCancellationRequested();
                        // Disposing the client abandons the outstanding SYN.
                        throw new TimeoutException();
                    }
                    await connect.ConfigureAwait(false);
                    return watch.ElapsedMilliseconds;
                }
                finally { deadline.Cancel(); }
            }
        }
    }

    // Call on a worker. Follows the route Windows would use for 1.1.1.1, so the counters
    // belong to the VPN tunnel while one is up, whatever the adapter is called.
    internal sealed class RouteTrafficCounters : ITrafficCounters
    {
        private static readonly uint Destination = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
        private NetworkInterface cached;
        private uint cachedIndex;

        [DllImport("iphlpapi.dll")]
        private static extern int GetBestInterface(uint destination, out uint index);

        public TrafficSample Read()
        {
            uint index;
            if (GetBestInterface(Destination, out index) != 0) return null;
            if (cached == null || cachedIndex != index) { cached = Find(index); cachedIndex = index; }
            if (cached == null) return null;
            try
            {
                IPInterfaceStatistics statistics = cached.GetIPStatistics();
                return new TrafficSample { InterfaceId = cached.Id, PacketsSent = statistics.UnicastPacketsSent,
                    PacketsReceived = statistics.UnicastPacketsReceived };
            }
            catch (NetworkInformationException) { cached = null; return null; }
        }

        private static NetworkInterface Find(uint index)
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (adapter.Supports(NetworkInterfaceComponent.IPv4) &&
                        adapter.GetIPProperties().GetIPv4Properties().Index == index) return adapter;
                }
                catch (NetworkInformationException) { }
            }
            return null;
        }
    }

    internal sealed class SystemLinkEnvironment : ILinkEnvironment
    {
        // The primary IP-lookup host: when it is unreachable this app's lookups fail too, so it
        // is a fair target on networks that block 1.1.1.1 or 8.8.8.8.
        internal const string LookupHost = "ipv4.icanhazip.com";
        private static readonly Uri LookupUri = new Uri("https://" + LookupHost + "/");

        public IPAddress[] ResolveTargets()
        {
            var result = new List<IPAddress>();
            foreach (IPAddress address in Dns.GetHostAddresses(LookupHost))
                if (address.AddressFamily == AddressFamily.InterNetwork && result.Count < 2) result.Add(address);
            return result.ToArray();
        }

        public bool IsProxied(bool systemProxy)
        {
            return systemProxy && !WebRequest.GetSystemWebProxy().IsBypassed(LookupUri);
        }
    }

    // Passive and free: this app's own handshakes keep TX moving, and a live peer always
    // answers them. TX growing while RX stays flat means the route is swallowing traffic,
    // the window in which a VPN client can still show "connected".
    internal sealed class TunnelStallDetector
    {
        private string interfaceId;
        private long sent = -1, received = -1;
        private DateTime lastSampleUtc = DateTime.MinValue;
        private DateTime stalledSinceUtc = DateTime.MinValue;

        internal bool RouteChanged { get; private set; }

        internal TimeSpan Observe(TrafficSample sample, DateTime now)
        {
            RouteChanged = false;
            DateTime previousSample = lastSampleUtc;
            lastSampleUtc = now;
            if (sample == null)
            {
                // Keep the interface identity, so a route that returns elsewhere is still a change.
                sent = received = -1; stalledSinceUtc = DateTime.MinValue;
                return TimeSpan.Zero;
            }
            if (sample.InterfaceId != interfaceId)
            {
                RouteChanged = interfaceId != null;
                interfaceId = sample.InterfaceId;
                sent = received = -1;
            }
            // Counters restart when an adapter is reset; a restart is not traffic.
            bool comparable = sent >= 0 && sample.PacketsSent >= sent && sample.PacketsReceived >= received;
            bool txMoved = comparable && sample.PacketsSent > sent;
            bool rxMoved = comparable && sample.PacketsReceived > received;
            sent = sample.PacketsSent; received = sample.PacketsReceived;
            if (rxMoved || !txMoved || previousSample == DateTime.MinValue || now < previousSample)
            {
                stalledSinceUtc = DateTime.MinValue;
                return TimeSpan.Zero;
            }
            // The unanswered packets left at some point after the previous sample.
            if (stalledSinceUtc == DateTime.MinValue || now < stalledSinceUtc) stalledSinceUtc = previousSample;
            return now - stalledSinceUtc;
        }
    }

    // Hysteresis turns noisy per-round evidence into a state that neither hides an outage nor
    // flashes red on one lost packet: one failed round is only Suspect, Down needs three in a
    // row (or a failure plus a stalled route), and recovery must hold for six seconds.
    internal sealed class LinkHealth
    {
        internal const int Window = 20;
        internal const int RttWindow = 5;
        internal static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(4);
        internal static readonly TimeSpan RecoveryHold = TimeSpan.FromSeconds(6);
        private readonly Queue<bool> outcomes = new Queue<bool>();
        private readonly Queue<long> rtts = new Queue<long>();
        private int failuresInRow, successesInRow;
        private DateTime recoveringSinceUtc;

        internal LinkHealth() { BaselineMs = -1; }

        internal LinkState State { get; private set; }
        // Typical handshake on this route, learned only while Healthy; -1 until known.
        internal double BaselineMs { get; private set; }
        internal DateTime DownSinceUtc { get; private set; }

        // Percentage of failed rounds; 0 until ten rounds exist.
        internal int LossPercent
        {
            get
            {
                if (outcomes.Count < 10) return 0;
                int lost = 0;
                foreach (bool outcome in outcomes) if (!outcome) lost++;
                return lost * 100 / outcomes.Count;
            }
        }

        // Median of the last five successful handshakes; -1 until three exist.
        internal long MedianRttMs
        {
            get
            {
                if (rtts.Count < 3) return -1;
                long[] copy = rtts.ToArray();
                Array.Sort(copy);
                return copy[copy.Length / 2];
            }
        }

        // A different route (VPN up or down) has a different normal; relearn it from scratch.
        internal void Forget()
        {
            outcomes.Clear(); rtts.Clear();
            BaselineMs = -1;
            failuresInRow = successesInRow = 0;
            State = LinkState.Unknown;
        }

        internal LinkState Observe(bool ok, long rttMs, TimeSpan stall, DateTime now)
        {
            Push(outcomes, ok, Window);
            if (ok) { Push(rtts, rttMs, RttWindow); successesInRow++; failuresInRow = 0; }
            else { failuresInRow++; successesInRow = 0; }
            bool stalled = stall >= StallThreshold;
            LinkState previous = State;
            switch (State)
            {
                case LinkState.Down:
                    if (ok && !stalled) { State = LinkState.Recovering; recoveringSinceUtc = now; }
                    break;
                case LinkState.Recovering:
                    if (!ok || stalled) State = LinkState.Down;
                    else if (successesInRow >= 3 && (now - recoveringSinceUtc >= RecoveryHold || now < recoveringSinceUtc))
                    {
                        State = LinkState.Healthy;
                        // The outage's failures say nothing about quality after recovery.
                        outcomes.Clear();
                    }
                    break;
                default:
                    if (failuresInRow >= 3 || (!ok && stalled)) State = LinkState.Down;
                    else if (!ok || stalled) State = LinkState.Suspect;
                    else State = Quality(State == LinkState.Degraded);
                    break;
            }
            if (State == LinkState.Down && previous != LinkState.Down && previous != LinkState.Recovering) DownSinceUtc = now;
            // Learn from the median, not single samples, so a spike cannot drag the baseline up
            // before the median crosses the degradation threshold.
            if (ok && State == LinkState.Healthy)
            {
                long typical = MedianRttMs >= 0 ? MedianRttMs : rttMs;
                BaselineMs = BaselineMs < 0 ? typical : BaselineMs * 0.9 + typical * 0.1;
            }
            return State;
        }

        private LinkState Quality(bool degraded)
        {
            long median = MedianRttMs;
            double ratio = BaselineMs > 0 && median >= 0 ? median / BaselineMs : 1;
            if (degraded) return LossPercent < 5 && ratio <= 1.5 ? LinkState.Healthy : LinkState.Degraded;
            return LossPercent >= 10 || ratio > 2 ? LinkState.Degraded : LinkState.Healthy;
        }

        private static void Push<T>(Queue<T> queue, T value, int capacity)
        {
            if (queue.Count >= capacity) queue.Dequeue();
            queue.Enqueue(value);
        }
    }

    // Fast, low-traffic liveness loop beside the HTTPS rounds: normally one handshake every
    // two seconds. Not thread-safe; the tray serializes MeasureAsync and Observe, and only the
    // background environment refresh writes the volatile fields.
    internal sealed class LinkMonitor
    {
        internal static readonly TimeSpan NormalInterval = TimeSpan.FromSeconds(2);
        internal static readonly TimeSpan ConfirmInterval = TimeSpan.FromMilliseconds(500);
        internal static readonly TimeSpan EnvironmentLifetime = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan EnvironmentRetry = TimeSpan.FromSeconds(30);
        internal static readonly IPAddress[] FixedTargets = { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8") };
        private const int EnvironmentTimeoutMilliseconds = 3000;
        private readonly ILinkProbe probe;
        private readonly ITrafficCounters counters;
        private readonly ILinkEnvironment environment;
        private readonly LinkHealth health = new LinkHealth();
        private readonly TunnelStallDetector stall = new TunnelStallDetector();
        // Only a target that has answered on this route may vote for an outage, so a network
        // that blocks 8.8.8.8 (or every target) from the start never looks "down".
        private readonly HashSet<IPAddress> eligible = new HashSet<IPAddress>();
        private IPAddress preferred;
        private volatile IPAddress[] learned = new IPAddress[0];
        private volatile bool proxied;
        private volatile bool environmentStale = true;
        private volatile bool environmentFailed;
        private DateTime environmentDueUtc;
        private Task environmentTask = Task.FromResult(0);

        internal LinkMonitor() : this(new TcpLinkProbe(), new RouteTrafficCounters(), new SystemLinkEnvironment()) { }

        internal LinkMonitor(ILinkProbe probe, ITrafficCounters counters, ILinkEnvironment environment)
        {
            if (probe == null) throw new ArgumentNullException("probe");
            if (counters == null) throw new ArgumentNullException("counters");
            if (environment == null) throw new ArgumentNullException("environment");
            this.probe = probe;
            this.counters = counters;
            this.environment = environment;
        }

        internal LinkState State { get { return health.State; } }
        internal LinkHealth Health { get { return health; } }
        internal TimeSpan Stall { get; private set; }
        internal bool Proxied { get { return proxied; } }
        internal TimeSpan Interval { get { return health.State == LinkState.Suspect ? ConfirmInterval : NormalInterval; } }
        // Completes when the latest proxy/target refresh has been applied.
        internal Task EnvironmentTask { get { return environmentTask; } }

        // Four typical handshakes, within one to three seconds.
        internal int TimeoutMilliseconds
        {
            get { double baseline = health.BaselineMs; return baseline <= 0 ? 2000 : (int)Math.Round(Math.Max(1000, Math.Min(3000, 4 * baseline))); }
        }

        // Network events: re-read the proxy decision and lookup-host addresses next round.
        internal void Invalidate() { environmentStale = true; }

        // Numbers only: no addresses, adapter names or hosts.
        internal string Summary()
        {
            return "rttMs=" + health.MedianRttMs.ToString(CultureInfo.InvariantCulture) +
                "; baselineMs=" + ((long)health.BaselineMs).ToString(CultureInfo.InvariantCulture) +
                "; lossPct=" + health.LossPercent.ToString(CultureInfo.InvariantCulture) +
                "; stallSec=" + ((long)Stall.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        internal async Task<LinkReading> MeasureAsync(bool systemProxy, DateTime now)
        {
            RefreshEnvironmentIfDue(systemProxy, now);
            if (proxied) return new LinkReading { Proxied = true, Attempts = new LinkAttempt[0] };
            IPAddress[] order = Order();
            int timeout = TimeoutMilliseconds;
            var attempts = new List<LinkAttempt>();
            // Normally only the preferred target is asked. While confirming or waiting for
            // recovery every target is asked at once, rather than one timeout after another.
            bool confirming = health.State == LinkState.Suspect || health.State == LinkState.Down;
            int asked = 0;
            if (!confirming)
            {
                attempts.Add(await AttemptAsync(order[0], timeout).ConfigureAwait(false));
                asked = 1;
            }
            if ((confirming || !attempts[0].Success) && order.Length > asked)
            {
                var rest = new Task<LinkAttempt>[order.Length - asked];
                for (int i = asked; i < order.Length; i++) rest[i - asked] = AttemptAsync(order[i], timeout);
                attempts.AddRange(await Task.WhenAll(rest).ConfigureAwait(false));
            }
            // Read after the handshakes, so this round's SYNs and any answers are counted.
            TrafficSample traffic = null;
            Task<TrafficSample> read = Task.Run(() => counters.Read());
            ConnectivityTasks.ObserveFault(read);
            if (await Task.WhenAny(read, Task.Delay(EnvironmentTimeoutMilliseconds)).ConfigureAwait(false) == read &&
                read.Status == TaskStatus.RanToCompletion) traffic = read.Result;
            return new LinkReading { Attempts = attempts.ToArray(), Traffic = traffic };
        }

        internal LinkState Observe(LinkReading reading, DateTime now)
        {
            Stall = stall.Observe(reading.Traffic, now);
            if (stall.RouteChanged)
            {
                // VPN up or down: neither old answers nor old round-trip times describe this route.
                eligible.Clear();
                preferred = null;
                health.Forget();
            }
            if (reading.Proxied)
            {
                // A direct handshake cannot speak for a proxied HTTPS path.
                Stall = TimeSpan.Zero;
                health.Forget();
                return health.State;
            }
            bool voted = false;
            LinkAttempt fastest = null;
            foreach (LinkAttempt attempt in reading.Attempts)
            {
                if (!attempt.Success) { voted |= eligible.Contains(attempt.Address); continue; }
                if (fastest == null || attempt.ElapsedMilliseconds < fastest.ElapsedMilliseconds) fastest = attempt;
            }
            foreach (LinkAttempt attempt in reading.Attempts) if (attempt.Success) eligible.Add(attempt.Address);
            if (fastest == null && !voted) return health.State;
            if (fastest != null)
                preferred = reading.Attempts[0].Success ? reading.Attempts[0].Address : fastest.Address;
            return health.Observe(fastest != null, fastest == null ? -1 : fastest.ElapsedMilliseconds, Stall, now);
        }

        private IPAddress[] Order()
        {
            var order = new List<IPAddress>();
            if (preferred != null) order.Add(preferred);
            foreach (IPAddress address in learned) if (!order.Contains(address)) order.Add(address);
            foreach (IPAddress address in FixedTargets) if (!order.Contains(address)) order.Add(address);
            return order.ToArray();
        }

        private async Task<LinkAttempt> AttemptAsync(IPAddress address, int timeout)
        {
            var attempt = new LinkAttempt { Address = address };
            using (var deadline = new CancellationTokenSource())
            {
                // Bounds a probe that ignores its own timeout; socket setup stays off the UI thread.
                deadline.CancelAfter(timeout + 500);
                try
                {
                    attempt.ElapsedMilliseconds = await ConnectivityTasks.WithCancellation(
                        Task.Run(() => probe.HandshakeAsync(address, timeout, deadline.Token)), deadline.Token).ConfigureAwait(false);
                    attempt.Success = true;
                }
                catch (Exception ex)
                {
                    if (ex is OutOfMemoryException || ex is StackOverflowException ||
                        ex is AccessViolationException || ex is ThreadAbortException) throw;
                }
            }
            return attempt;
        }

        private void RefreshEnvironmentIfDue(bool systemProxy, DateTime now)
        {
            if (!environmentTask.IsCompleted) return;
            if (environmentFailed) { environmentFailed = false; environmentDueUtc = now.Add(EnvironmentRetry); }
            if (!environmentStale && now < environmentDueUtc && environmentDueUtc - now <= EnvironmentLifetime) return;
            environmentStale = false;
            environmentDueUtc = now.Add(EnvironmentLifetime);
            // Runs beside the measurement: a hanging DNS or WPAD lookup must not delay outage detection.
            environmentTask = Task.Run(async () => {
                Task<bool> proxy = Task.Run(() => environment.IsProxied(systemProxy));
                Task<IPAddress[]> targets = Task.Run(() => environment.ResolveTargets());
                bool proxyKnown = await Completes(proxy).ConfigureAwait(false);
                bool targetsKnown = await Completes(targets).ConfigureAwait(false);
                // An unknown proxy decision disables the loop rather than guessing it is direct.
                proxied = !proxyKnown || proxy.Result;
                if (targetsKnown && targets.Result != null) learned = targets.Result;
                environmentFailed = !proxyKnown || !targetsKnown;
            });
            ConnectivityTasks.ObserveFault(environmentTask);
        }

        private static async Task<bool> Completes(Task work)
        {
            ConnectivityTasks.ObserveFault(work);
            return await Task.WhenAny(work, Task.Delay(EnvironmentTimeoutMilliseconds)).ConfigureAwait(false) == work &&
                work.Status == TaskStatus.RanToCompletion;
        }
    }
}
