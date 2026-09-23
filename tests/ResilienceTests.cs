using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IPCountryWatcher
{
    internal sealed class DelegateLookup : ILookup
    {
        internal Func<CancellationToken, Task<Snapshot>> Handler;
        public Task<Snapshot> QueryAsync(bool proxy, CancellationToken token) { return Handler(token); }
    }

    internal static partial class Tests
    {
        private static Snapshot Confirmed(DateTime now)
        { return new Snapshot { Ip = "8.8.8.8", CountryCode = "SG", Country = "新加坡", CheckedUtc = now }; }

        private static void TrayDisplayStateTests()
        {
            DateTime now = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
            var state = new TrayDisplayState();
            Check("Cold start cannot invent a previous flag", state.Displayed(now) == null);
            var good = Confirmed(now);
            state.Accept(good);
            state.Invalidate(false);
            Check("Refresh retains icon but invalidates measured current state", state.Displayed(now) == good && state.Current == null);
            var failed = new Snapshot { Error = "timeout", CheckedUtc = now.AddSeconds(5) };
            state.Accept(failed);
            Check("Transient failure retains flag without claiming an IP measurement", state.Displayed(now.AddSeconds(5)) == good && !state.Current.HasIp);
            state.Invalidate(false);
            state.Accept(new Snapshot { Error = "timeout", CheckedUtc = now.AddSeconds(119) });
            Check("Repeated events/failures do not extend the two-minute grace", state.Displayed(now.AddSeconds(119)) == good && state.Displayed(now.AddMinutes(2)) == null);
            Check("Clock rollback cannot keep a historical flag", state.Displayed(now.AddSeconds(-1)) == null);
            state.Accept(good);
            state.Invalidate(true);
            state.Accept(failed);
            Check("Explicit disconnect/proxy change clears retained flag permanently", state.Displayed(now) == null);
            state.Accept(good);
            state.Accept(new Snapshot { Ip = "1.1.1.1", CheckedUtc = now.AddSeconds(1) });
            Check("New unresolved IP clears previous country's flag immediately", state.Displayed(now.AddSeconds(1)) == null);
            state.Accept(failed);
            Check("Later failures cannot resurrect the old IP flag", state.Displayed(now.AddSeconds(5)) == null);
            var next = new Snapshot { Ip = "1.1.1.1", CountryCode = "JP", CheckedUtc = now.AddSeconds(6) };
            state.Accept(next);
            Check("Confirmed country change is displayed immediately", state.Displayed(now.AddSeconds(6)) == next);
        }

        private static async Task CountryCacheResilienceTests()
        {
            DateTime start = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc), now = start;
            bool fail = false;
            var transport = new FakeTransport { Handler = (url, token) => {
                if (fail) throw new HttpRequestException("temporary outage");
                return Task.FromResult(Geo("8.8.8.8", "SG"));
            } };
            var lookup = new LookupService(transport, () => now);
            await lookup.ResolveCountryAsync("8.8.8.8", true, CancellationToken.None, "test");
            now = start.AddHours(24); fail = true;
            var fallback = await lookup.ResolveCountryAsync("8.8.8.8", true, CancellationToken.None, "test");
            Check("Expired cache survives temporary geo outage for the exact confirmed IP", fallback.CountryCode == "SG" && fallback.CountryIsStale && fallback.CheckedUtc == now && fallback.Error != null);
            var changed = await lookup.ResolveCountryAsync("1.1.1.1", true, CancellationToken.None, "test");
            Check("Stale cache cannot supply a different IP's country", !changed.HasCountry);
            now = start.AddDays(7);
            var expired = await lookup.ResolveCountryAsync("8.8.8.8", true, CancellationToken.None, "test");
            Check("Failure fallback does not renew the seven-day hard cache limit", !expired.HasCountry && !expired.CountryIsStale);
            now = now.AddSeconds(5); fail = false;
            var recovered = await lookup.ResolveCountryAsync("8.8.8.8", true, CancellationToken.None, "test");
            Check("Successful geo recovery replaces stale state", recovered.CountryCode == "SG" && !recovered.CountryIsStale && recovered.Error == null);
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { await lookup.ResolveCountryAsync("8.8.8.8", true, canceled.Token, "test"); Check("Cached country honors cancellation", false); }
                catch (OperationCanceledException) { Check("Cached country honors cancellation", true); }
            }
        }

        private static void TrayFailureRetryTest()
        {
            int calls = 0, recoveredCalls = 0;
            var lookup = new DelegateLookup { Handler = token => {
                int call = Interlocked.Increment(ref calls);
                return Task.FromResult(call == 2 ? new Snapshot { Error = "simulated timeout", CheckedUtc = DateTime.UtcNow } : Confirmed(DateTime.UtcNow));
            } };
            var watch = Stopwatch.StartNew();
            bool refreshed = false, retainedFailure = false, recovered = false;
            var context = new TrayContext(lookup, new Settings { PollSeconds = 60, NotifyOnChange = false }, true);
            using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
            {
                timer.Tick += (s, e) => {
                    if (!refreshed && context.VisibleCountry == "SG")
                    {
                        refreshed = true; context.RequestRefresh();
                        Check("Real tray keeps flag during refresh and disables stale-IP copying", context.VisibleCountry == "SG" && context.Current == null && !context.CanCopyIp && context.TooltipText.Contains("待确认"));
                    }
                    if (context.Current != null && !context.Current.HasIp)
                        retainedFailure = context.VisibleCountry == "SG" && context.StatusText.Contains("simulated timeout") && context.TooltipText.Contains("上次") && !context.CanCopyIp;
                    recovered = calls >= 3 && context.Current != null && context.Current.HasCountry;
                    if (recovered)
                    {
                        recoveredCalls = calls;
                        context.RequestRefresh(true);
                        Check("Real tray clears historical flag for explicit route/disconnect invalidation", context.VisibleCountry == null && context.Current == null && !context.CanCopyIp);
                    }
                    if (recovered || watch.ElapsedMilliseconds > 7000) { timer.Stop(); context.ExitThread(); }
                };
                timer.Start(); Application.Run(context);
            }
            context.Dispose();
            Check("Real tray marks retained flag honestly after an endpoint failure", retainedFailure);
            Check("60-second polling recovers from first IP failure after about 5 seconds", recovered && recoveredCalls == 3 && watch.ElapsedMilliseconds >= 4500 && watch.ElapsedMilliseconds < 7000);
        }

        private static void TrayRetentionExpiryTest()
        {
            DateTime now = DateTime.UtcNow;
            var pending = new TaskCompletionSource<Snapshot>();
            int calls = 0, phase = 0;
            var lookup = new DelegateLookup { Handler = token => Interlocked.Increment(ref calls) == 1 ? Task.FromResult(Confirmed(now)) : pending.Task };
            var watch = Stopwatch.StartNew();
            bool expired = false;
            var context = new TrayContext(lookup, new Settings { PollSeconds = 60, NotifyOnChange = false }, true, () => now);
            using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
            {
                timer.Tick += (s, e) => {
                    if (phase == 0 && context.VisibleCountry == "SG") { context.RequestRefresh(); phase = 1; }
                    else if (phase == 1 && calls == 2) { now = now.AddMinutes(2); phase = 2; }
                    else if (phase == 2 && context.VisibleCountry == null) expired = context.Current == null && !context.CanCopyIp;
                    if (expired || watch.ElapsedMilliseconds > 2000) { timer.Stop(); context.ExitThread(); }
                };
                timer.Start(); Application.Run(context);
            }
            context.Dispose();
            pending.SetResult(Confirmed(now));
            Check("Real tray expires retained flag even with a query still pending", expired);
        }
    }
}
