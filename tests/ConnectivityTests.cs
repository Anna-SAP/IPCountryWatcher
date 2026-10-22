using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace IPCountryWatcher
{
    internal sealed class ConnectivityTestLookup : ILookup
    {
        internal Func<bool, CancellationToken, Task<Snapshot>> Handler;
        public Task<Snapshot> QueryAsync(bool systemProxy, CancellationToken token)
        { return Handler(systemProxy, token); }
    }

    internal sealed class ConnectivityTestProbe : IConnectivityProbe
    {
        internal int Calls;
        internal Func<bool, CancellationToken, Task<ConnectivitySnapshot>> Handler;
        public Task<ConnectivitySnapshot> CheckAsync(bool systemProxy, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Handler(systemProxy, token); }
    }

    internal static partial class Tests
    {
        private static ConnectivitySnapshot HealthAt(DateTime now, ConnectivityHealth health, bool proxy)
        {
            return new ConnectivitySnapshot {
                Health = health, CheckedUtc = now, SystemProxy = proxy,
                Results = new[] {
                    new ProbeResult { Source = "first", Success = health == ConnectivityHealth.Reachable || health == ConnectivityHealth.Limited },
                    new ProbeResult { Source = "second", Success = health == ConnectivityHealth.Reachable }
                }
            };
        }

        internal static async Task RunConnectivityTests()
        {
            await IndependentConnectivityProbeTests();
            await ConnectivityCooldownTests();
            await ConnectivityCacheTests();
            await ConnectivityEpochTests();
            await ConnectivityConcurrentCacheTests();
        }

        private static async Task IndependentConnectivityProbeTests()
        {
            DateTime now = DateTime.UtcNow;
            int started = 0;
            var bothStarted = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<string>();
            var transport = new FakeTransport { Handler = async (url, token) => {
                if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult(true);
                await release.Task;
                return url.Contains("cloudflare") ? "ip=8.8.8.8\ncolo=SIN\n" : "1.1.1.1";
            } };
            var probe = new ConnectivityProbe(transport, () => now);
            Task<ConnectivitySnapshot> task = probe.CheckAsync(true, CancellationToken.None);
            bool concurrent = await Task.WhenAny(bothStarted.Task, Task.Delay(1000)) == bothStarted.Task;
            release.SetResult("");
            ConnectivitySnapshot health = await task;
            Check("Health probes start both independent hosts concurrently", concurrent && transport.Calls.Count == 2 &&
                new Uri(transport.Calls[0]).Host != new Uri(transport.Calls[1]).Host);
            Check("Two validated HTTPS/IP responses establish selected-target reachability", health.Health == ConnectivityHealth.Reachable &&
                health.Results.Length == 2 && health.Results[0].Success && health.Results[1].Success && health.SystemProxy);

            transport.Handler = (url, token) => Task.FromResult(url.Contains("cloudflare") ? "ip=8.8.8.8\n" : "<html>secret-login-page</html>");
            health = await probe.CheckAsync(false, CancellationToken.None);
            Check("One good target and one captive/wrong response is limited", health.Health == ConnectivityHealth.Limited &&
                !health.SystemProxy && health.Results[1].Failure == "invalid-response");

            transport.Handler = (url, token) => {
                if (url.Contains("cloudflare")) return Task.FromResult("ip=8.8.8.8\n");
                throw new HttpStatusException(503);
            };
            health = await probe.CheckAsync(true, CancellationToken.None);
            Check("Server HTTP failure retains its status and another service's reachability", health.Health == ConnectivityHealth.Limited &&
                health.Results[0].Success && health.Results[1].Failure == "http-503");

            transport.Handler = (url, token) => {
                if (url.Contains("cloudflare")) return Task.FromResult("ip=8.8.8.8\n");
                throw new RateLimitException(TimeSpan.FromMinutes(1));
            };
            health = await probe.CheckAsync(true, CancellationToken.None);
            Check("HTTP 429 cannot discard another probe's successful evidence", health.Health == ConnectivityHealth.Limited &&
                health.Results[0].Success && health.Results[1].Failure == "http-429");
            probe = new ConnectivityProbe(transport, () => now);
            transport.Handler = (url, token) => {
                if (url.Contains("cloudflare")) return Task.FromResult("ip=8.8.8.8\n");
                throw new NotSupportedException("secret transport implementation detail");
            };
            health = await probe.CheckAsync(true, CancellationToken.None);
            Check("Unexpected ordinary transport failure cannot erase another successful endpoint", health.Health == ConnectivityHealth.Limited &&
                health.Results[1].Failure == "transport");

            transport.Handler = (url, token) => {
                if (url.Contains("cloudflare")) throw new HttpRequestException("secret-proxy-url", new WebException("secret-host", WebExceptionStatus.NameResolutionFailure));
                throw new RateLimitException(TimeSpan.FromMinutes(1));
            };
            health = await probe.CheckAsync(true, CancellationToken.None);
            Check("All failed probes preserve safe DNS and HTTP categories without exception text", health.Health == ConnectivityHealth.Unreachable &&
                health.Results[0].Failure == "dns" && health.Results[1].Failure == "http-429");

            var ignored = new TaskCompletionSource<string>();
            transport.Handler = (url, token) => ignored.Task;
            var bounded = new ConnectivityProbe(transport, () => now, 80);
            var watch = Stopwatch.StartNew();
            health = await bounded.CheckAsync(true, CancellationToken.None);
            Check("Health total deadline bounds transports that ignore cancellation", watch.ElapsedMilliseconds < 1500 &&
                health.Health == ConnectivityHealth.Unreachable && health.Results[0].Failure == "timeout" && health.Results[1].Failure == "timeout");
            int requestsBeforeRetry = transport.Calls.Count;
            health = await bounded.CheckAsync(true, CancellationToken.None);
            Check("Repeated timeouts cannot accumulate cancellation-ignoring background requests", transport.Calls.Count == requestsBeforeRetry &&
                health.Results[0].Failure == "previous-request-pending" && health.Results[1].Failure == "previous-request-pending");
            ignored.SetException(new HttpRequestException("late failure must be observed"));

            var callerIgnored = new TaskCompletionSource<string>();
            transport.Handler = (url, token) => callerIgnored.Task;
            using (var canceled = new CancellationTokenSource())
            {
                task = probe.CheckAsync(true, canceled.Token);
                canceled.Cancel();
                try { await task; Check("Caller cancellation is not returned as an offline measurement", false); }
                catch (OperationCanceledException) { Check("Caller cancellation is not returned as an offline measurement", true); }
            }
            callerIgnored.SetResult("8.8.8.8");
            Check("Health expires at 30 seconds and rejects clock rollback", !HealthAt(now, ConnectivityHealth.Reachable, true).IsFresh(now.AddSeconds(30)) &&
                !HealthAt(now, ConnectivityHealth.Reachable, true).IsFresh(now.AddSeconds(-1)));
        }

        private static async Task ConnectivityCooldownTests()
        {
            DateTime now = DateTime.UtcNow;
            int limitedCalls = 0, successfulCalls = 0;
            var transport = new FakeTransport { Handler = (url, token) => {
                if (url.Contains("cloudflare"))
                {
                    if (Interlocked.Increment(ref limitedCalls) == 1) throw new RateLimitException(TimeSpan.FromSeconds(20));
                    return Task.FromResult("ip=8.8.8.8\n");
                }
                Interlocked.Increment(ref successfulCalls);
                return Task.FromResult("1.1.1.1");
            } };
            var probe = new ConnectivityProbe(transport, () => now);
            ConnectivitySnapshot health = await probe.CheckAsync(true, CancellationToken.None);
            now = now.AddSeconds(10);
            health = await probe.CheckAsync(true, CancellationToken.None);
            Check("Retry-After skips only the limited host while the other probe continues", limitedCalls == 1 && successfulCalls == 2 &&
                health.Health == ConnectivityHealth.Limited && health.Results[0].Failure == "http-429-cooldown");
            now = now.AddSeconds(10);
            health = await probe.CheckAsync(true, CancellationToken.None);
            Check("Rate-limited health host is retried when its own cooldown expires", limitedCalls == 2 && successfulCalls == 3 &&
                health.Health == ConnectivityHealth.Reachable);
        }

        private static async Task ConnectivityCacheTests()
        {
            DateTime now = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
            bool ipFails = false;
            string publicIp = "8.8.8.8";
            ConnectivityHealth sampledHealth = ConnectivityHealth.Reachable;
            var inner = new ConnectivityTestLookup { Handler = (proxy, token) => Task.FromResult(new Snapshot {
                Ip = ipFails ? null : publicIp, Error = "Country service unavailable", CheckedUtc = now
            }) };
            var probe = new ConnectivityTestProbe { Handler = (proxy, token) => Task.FromResult(HealthAt(now, sampledHealth, proxy)) };
            var lookup = new ConnectivityLookup(inner, probe, () => now);
            Snapshot result = await lookup.QueryAsync(true, CancellationToken.None);
            DateTime originalHealthTime = result.Connectivity.CheckedUtc;
            Check("Country-only lookup failure cannot make fresh connectivity fail", !result.HasCountry && result.HasIp &&
                result.Connectivity.Health == ConnectivityHealth.Reachable);
            now = now.AddSeconds(14);
            result = await lookup.QueryAsync(true, CancellationToken.None);
            Check("Successful IP rounds reuse health without renewing its timestamp", probe.Calls == 1 &&
                result.Connectivity.Health == ConnectivityHealth.Reachable && result.Connectivity.CheckedUtc == originalHealthTime);
            now = now.AddSeconds(1);
            await lookup.QueryAsync(true, CancellationToken.None);
            Check("Health cache is remeasured at the 15-second boundary", probe.Calls == 2);

            ipFails = true; sampledHealth = ConnectivityHealth.Unreachable;
            result = await lookup.QueryAsync(true, CancellationToken.None);
            Check("Failed IP query bypasses cached healthy evidence immediately", probe.Calls == 3 && result.Connectivity.Health == ConnectivityHealth.Unreachable);
            ipFails = false;
            result = await lookup.QueryAsync(true, CancellationToken.None);
            Check("A successful public-IP query is partial reachability even when health targets fail", result.Connectivity.Health == ConnectivityHealth.Limited && probe.Calls == 4);
            await lookup.QueryAsync(true, CancellationToken.None);
            Check("Negative health is remeasured instead of cached across fast retries", probe.Calls == 5);

            sampledHealth = ConnectivityHealth.Reachable;
            lookup.Invalidate();
            result = await lookup.QueryAsync(true, CancellationToken.None);
            Check("Invalidation accepts freshly recovered health", probe.Calls == 6 && result.Connectivity.Health == ConnectivityHealth.Reachable);
            publicIp = "1.1.1.1";
            await lookup.QueryAsync(true, CancellationToken.None);
            Check("A changed public IP forces fresh health without a network event", probe.Calls == 7);
            result = await lookup.QueryAsync(false, CancellationToken.None);
            Check("A different proxy mode cannot reuse the other path's health", probe.Calls == 8 && !result.Connectivity.SystemProxy);

            ipFails = true;
            result = await lookup.QueryAsync(false, CancellationToken.None);
            Check("IP endpoint failure does not hide two reachable independent health targets", result.Connectivity.Health == ConnectivityHealth.Reachable && !result.HasIp);
            now = now.AddMinutes(-1); ipFails = false;
            await lookup.QueryAsync(false, CancellationToken.None);
            Check("Clock rollback cannot reuse a future health sample", probe.Calls == 10);
            lookup.Invalidate();
            await lookup.QueryAsync(false, CancellationToken.None);
            Check("Manual/network invalidation discards recently cached healthy evidence", probe.Calls == 11);
        }

        private static async Task ConnectivityEpochTests()
        {
            DateTime now = DateTime.UtcNow;
            var oldProbe = new TaskCompletionSource<ConnectivitySnapshot>();
            var started = new TaskCompletionSource<bool>();
            int probeCalls = 0;
            var probe = new ConnectivityTestProbe { Handler = (proxy, token) => {
                if (Interlocked.Increment(ref probeCalls) == 1) { started.SetResult(true); return oldProbe.Task; }
                return Task.FromResult(HealthAt(now, ConnectivityHealth.Limited, proxy));
            } };
            var inner = new ConnectivityTestLookup { Handler = (proxy, token) => Task.FromResult(new Snapshot { Ip = "8.8.8.8", CheckedUtc = now }) };
            var lookup = new ConnectivityLookup(inner, probe, () => now);
            Task<Snapshot> old = lookup.QueryAsync(true, CancellationToken.None);
            await started.Task;
            lookup.Invalidate();
            try { await old; Check("Invalidated epoch cancels a probe that ignores cancellation", false); }
            catch (OperationCanceledException) { Check("Invalidated epoch cancels a probe that ignores cancellation", true); }
            Snapshot latest = await lookup.QueryAsync(true, CancellationToken.None);
            oldProbe.SetResult(HealthAt(now, ConnectivityHealth.Reachable, true));
            latest = await lookup.QueryAsync(true, CancellationToken.None);
            Check("Late old-epoch success cannot poison the new epoch's health cache", latest.Connectivity.Health == ConnectivityHealth.Limited && probe.Calls == 3);

            var pendingIp = new TaskCompletionSource<Snapshot>();
            var ipStarted = new TaskCompletionSource<bool>();
            inner.Handler = (proxy, token) => { ipStarted.TrySetResult(true); return pendingIp.Task; };
            using (var canceled = new CancellationTokenSource())
            {
                Task<Snapshot> running = lookup.QueryAsync(true, canceled.Token);
                await ipStarted.Task;
                canceled.Cancel();
                try { await running; Check("Lookup wrapper cancellation bounds an IP lookup ignoring cancellation", false); }
                catch (OperationCanceledException) { Check("Lookup wrapper cancellation bounds an IP lookup ignoring cancellation", true); }
            }
            pendingIp.SetResult(new Snapshot { Ip = "1.1.1.1", CheckedUtc = now });
        }

        private static async Task ConnectivityConcurrentCacheTests()
        {
            DateTime now = DateTime.UtcNow;
            var earlierProbe = new TaskCompletionSource<ConnectivitySnapshot>();
            var earlierStarted = new TaskCompletionSource<bool>();
            int started = 0;
            var probe = new ConnectivityTestProbe { Handler = (proxy, token) => {
                if (Interlocked.Increment(ref started) == 1) { earlierStarted.SetResult(true); return earlierProbe.Task; }
                var sample = HealthAt(now, ConnectivityHealth.Reachable, proxy);
                sample.Results[0].Source = "newer-round";
                return Task.FromResult(sample);
            } };
            var inner = new ConnectivityTestLookup { Handler = (proxy, token) => Task.FromResult(new Snapshot { Ip = "8.8.8.8", CheckedUtc = now }) };
            var lookup = new ConnectivityLookup(inner, probe, () => now);
            Task<Snapshot> earlier = lookup.QueryAsync(true, CancellationToken.None);
            await earlierStarted.Task;
            await lookup.QueryAsync(true, CancellationToken.None);
            var stale = HealthAt(now, ConnectivityHealth.Reachable, true);
            stale.Results[0].Source = "older-round";
            earlierProbe.SetResult(stale);
            await earlier;
            Snapshot latest = await lookup.QueryAsync(true, CancellationToken.None);
            Check("An older concurrent round cannot replace a newer cached sample", probe.Calls == 2 &&
                latest.Connectivity.Results[0].Source == "newer-round");
        }
    }
}
