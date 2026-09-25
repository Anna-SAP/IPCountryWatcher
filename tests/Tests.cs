using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IPCountryWatcher
{
    internal sealed class FakeTransport : ITransport
    {
        public readonly List<string> Calls = new List<string>();
        public Func<string, CancellationToken, Task<string>> Handler;
        public Task<string> GetAsync(string url, bool proxy, CancellationToken token)
        { lock (Calls) Calls.Add(url); return Handler(url, token); }
    }

    internal sealed class SmokeLookup : ILookup
    {
        public int Calls;
        public async Task<Snapshot> QueryAsync(bool proxy, CancellationToken token)
        {
            int call = Interlocked.Increment(ref Calls);
            if (call == 1)
            {
                // Deliberately ignores cancellation to test generation protection.
                await Task.Delay(1100);
                return new Snapshot { Ip = "8.8.8.8", CountryCode = "US", Country = "美国", CheckedUtc = DateTime.UtcNow };
            }
            await Task.Delay(25, token);
            return new Snapshot { Ip = "1.1.1.1", CountryCode = call == 2 ? "JP" : "DE", Country = "测试国家", CheckedUtc = DateTime.UtcNow };
        }
    }

    internal sealed class RetryLookup : ILookup
    {
        public int Calls;
        public bool Stale;
        public Task<Snapshot> QueryAsync(bool proxy, CancellationToken token)
        {
            int call = Interlocked.Increment(ref Calls);
            return Task.FromResult(new Snapshot { Ip = "8.8.8.8", CountryCode = call > 1 || Stale ? "US" : null,
                Country = call > 1 || Stale ? "美国" : null, CountryIsStale = Stale && call == 1, CheckedUtc = DateTime.UtcNow });
        }
    }
    internal static partial class Tests
    {
        private static readonly List<string> report = new List<string>();
        private static int failures;
        private static void Check(string name, bool condition)
        {
            string line = (condition ? "PASS " : "FAIL ") + name;
            report.Add(line); Console.WriteLine(line);
            if (!condition) failures++;
        }
        private static void Reject(string name, Action action)
        {
            try { action(); Check(name, false); }
            catch (FormatException) { Check(name, true); }
        }
        private static string Geo(string ip, string code)
        { return "{\"success\":true,\"ip\":\"" + ip + "\",\"country_code\":\"" + code + "\",\"country\":\"Test\"}"; }
        private static string Trace(string ip)
        { return "fl=467f1\nh=ipv4.icanhazip.com\nip=" + ip + "\nts=1790000000.123\nvisit_scheme=https\ncolo=SIN\nloc=SG\n"; }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--ui-only")
                { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); ProcessWindowTest(); return failures == 0 ? 0 : 1; }
                ProcessTests().GetAwaiter().GetResult();
                CoreTests().GetAwaiter().GetResult();
                CountryCacheResilienceTests().GetAwaiter().GetResult();
                TrayDisplayStateTests();
                LatencyTests().GetAwaiter().GetResult();
                IconTests();
                SmokeTest();
                TrayLatencyTest();
                TrayRetryTest();
                TrayRetryTest(true);
                TrayFailureRetryTest();
                TrayRetentionExpiryTest();
                ProcessWindowTest();
            }
            catch (Exception ex) { Check("Unhandled exception: " + ex, false); }
            Directory.CreateDirectory("test-results");
            report.Add("Failures: " + failures);
            File.WriteAllLines("test-results/results.txt", report);
            return failures == 0 ? 0 : 1;
        }

        private static async Task CoreTests()
        {
            Check("IPv4 trim and parse", Validation.PublicIp(" 8.8.8.8\n") == "8.8.8.8");
            Check("IPv6 normalization", Validation.PublicIp("2606:4700:4700:0:0:0:0:1111") == "2606:4700:4700::1111");
            foreach (string ip in new[] { "127.0.0.1", "10.1.2.3", "192.168.1.1", "172.16.0.1", "169.254.2.1",
                "100.64.1.1", "::1", "fe80::1", "fc00::1", "0.0.0.0", "255.255.255.255", "203.0.113.5", "2001:db8::1", "123", "<html>error</html>" })
                Reject("Reject non-public IP " + ip, () => Validation.PublicIp(ip));
            Check("Plain-text IP response", Validation.ResponseIp("8.8.8.8\n") == "8.8.8.8");
            Check("Cloudflare trace IP response", Validation.ResponseIp(Trace("8.8.8.8")) == "8.8.8.8" &&
                Validation.ResponseIp(Trace("2606:4700:4700::1111").Replace("\n", "\r\n")) == "2606:4700:4700::1111");
            Reject("Reject non-public trace IP", () => Validation.ResponseIp(Trace("10.1.2.3")));
            Reject("Reject trace without IP", () => Validation.ResponseIp("fl=467f1\nloc=SG\n"));
            Check("Country parsing", Validation.CountryJson(Geo("8.8.8.8", "us"), "8.8.8.8").CountryCode == "US");
            Reject("Reject mismatched country IP", () => Validation.CountryJson(Geo("1.1.1.1", "AU"), "8.8.8.8"));
            Reject("Reject API success false", () => Validation.CountryJson("{\"success\":false}", "8.8.8.8"));
            Reject("Reject invalid country code", () => Validation.CountryJson(Geo("8.8.8.8", "../"), "8.8.8.8"));

            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var transport = new FakeTransport();
            string ipNow = "8.8.8.8";
            int geoCalls = 0;
            bool failGeo = false;
            transport.Handler = (url, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (url.StartsWith("https://ipwho.is/"))
                {
                    geoCalls++;
                    if (failGeo) throw new HttpRequestException("simulated");
                    return Task.FromResult(Geo(ipNow, ipNow == "8.8.8.8" ? "US" : "JP"));
                }
                return Task.FromResult(ipNow);
            };
            var service = new LookupService(transport, () => now);
            Snapshot first = await service.QueryAsync(true, CancellationToken.None);
            Check("Initial country lookup", first.CountryCode == "US" && geoCalls == 1);
            await service.QueryAsync(true, CancellationToken.None);
            Check("Same IP uses country cache", geoCalls == 1);
            now = now.AddHours(25);
            await service.QueryAsync(true, CancellationToken.None);
            Check("Country cache expires", geoCalls == 2);
            ipNow = "1.1.1.1";
            failGeo = true;
            Snapshot unknown = await service.QueryAsync(true, CancellationToken.None);
            Check("Changed IP never retains old country", unknown.Ip == ipNow && !unknown.HasCountry);
            await service.QueryAsync(true, CancellationToken.None);
            Check("Geo failure cooldown avoids repeated calls", geoCalls == 3);
            failGeo = false;
            now = now.AddSeconds(5);
            Snapshot recovered = await service.QueryAsync(true, CancellationToken.None);
            Check("Geo failure recovers after 5 seconds with unchanged IP", recovered.CountryCode == "JP" && geoCalls == 4);

            transport = new FakeTransport();
            transport.Handler = (url, token) =>
            {
                if (url == LookupService.IpUrls[0]) throw new HttpRequestException("primary down");
                if (url == LookupService.IpUrls[1]) return Task.FromResult(Trace("1.1.1.1"));
                if (url.StartsWith("https://ipwho.is/")) return Task.FromResult(Geo("1.1.1.1", "AU"));
                return Task.FromResult("1.1.1.1");
            };
            service = new LookupService(transport, () => now);
            Snapshot fallback = await service.QueryAsync(false, CancellationToken.None);
            Check("IP service failover", fallback.Source == "ipv4.icanhazip.com/cdn-cgi/trace" && fallback.Ip == "1.1.1.1" && fallback.HasCountry);
            transport.Handler = (url, token) => { throw new HttpRequestException("offline"); };
            Snapshot offline = await service.QueryAsync(false, CancellationToken.None);
            Check("All endpoints down clears current IP and flag", !offline.HasIp && !offline.HasCountry && offline.Error != null);

            transport = new FakeTransport();
            int limitedCalls = 0;
            transport.Handler = (url, token) =>
            {
                if (url.StartsWith("https://ipwho.is/"))
                { limitedCalls++; throw new RateLimitException(TimeSpan.FromHours(2)); }
                return Task.FromResult("8.8.8.8");
            };
            service = new LookupService(transport, () => now);
            await service.QueryAsync(true, CancellationToken.None);
            now = now.AddMinutes(10);
            await service.QueryAsync(true, CancellationToken.None);
            Check("HTTP 429 honors service retry delay", limitedCalls == 1);
            now = now.AddHours(2);
            await service.QueryAsync(true, CancellationToken.None);
            Check("HTTP 429 eventually retries", limitedCalls == 2);

            transport = new FakeTransport();
            transport.Handler = (url, token) => { throw new OperationCanceledException(); };
            service = new LookupService(transport, () => now);
            Snapshot timeout = await service.QueryAsync(true, CancellationToken.None);
            Check("Request timeouts use fallback and yield offline state", !timeout.HasIp && transport.Calls.Count == 3);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                try { await service.QueryAsync(true, cts.Token); Check("Cancellation propagated", false); }
                catch (OperationCanceledException) { Check("Cancellation propagated", true); }
            }

            var schedule = new RefreshSchedule();
            int old = schedule.Begin(now);
            Check("Scheduler starts first request", old == 0);
            Check("Scheduler disallows overlap", schedule.Begin(now) == -1);
            schedule.Request(now, TimeSpan.FromMilliseconds(400));
            Check("Network change invalidates in-flight result", !schedule.IsCurrent(old));
            schedule.Complete(old, now, TimeSpan.FromSeconds(60));
            Check("Stale completion cannot postpone network refresh", schedule.DueUtc <= now.AddMilliseconds(400));
            Check("Debounce waits for network to settle", schedule.Begin(now) == -1);
            int latest = schedule.Begin(now.AddSeconds(1));
            Check("Queued network refresh starts", latest == 1);
            schedule.Complete(latest, now, TimeSpan.FromSeconds(5));
            Check("Normal completion schedules polling", schedule.DueUtc == now.AddSeconds(5));
        }

        private static string BackupGeo(string ip, string code)
        { return "{\"ip\":\"" + ip + "\",\"country_code\":\"" + code + "\",\"country_name\":\"Test\"}"; }

        private static async Task LatencyTests()
        {
            Check("Backup provider parses country", Validation.CountryJson(BackupGeo("8.8.8.8", "US"), "8.8.8.8", true).CountryCode == "US");
            Reject("Backup rejects mismatched IP", () => Validation.CountryJson(BackupGeo("1.1.1.1", "AU"), "8.8.8.8", true));
            Reject("Backup rejects error response", () => Validation.CountryJson("{\"error\":true}", "8.8.8.8", true));
            Reject("Backup rejects invalid country", () => Validation.CountryJson(BackupGeo("8.8.8.8", "../"), "8.8.8.8", true));

            var transport = new FakeTransport();
            var slowIp = new TaskCompletionSource<string>();
            var slowGeo = new TaskCompletionSource<string>();
            int canceledLosers = 0;
            transport.Handler = (url, token) =>
            {
                if (url == LookupService.IpUrls[0] || url.StartsWith("https://ipwho.is/"))
                {
                    token.Register(() => Interlocked.Increment(ref canceledLosers));
                    return url == LookupService.IpUrls[0] ? slowIp.Task : slowGeo.Task;
                }
                if (url.StartsWith("https://ipapi.co/")) return Task.FromResult(BackupGeo("8.8.8.8", "US"));
                return Task.FromResult("8.8.8.8");
            };
            var service = new LookupService(transport, () => DateTime.UtcNow);
            var watch = Stopwatch.StartNew();
            Snapshot result = await service.QueryAsync(true, CancellationToken.None);
            Check("Slow IP and geo primaries bypassed in " + watch.ElapsedMilliseconds + " ms",
                result.CountryCode == "US" && result.Source == "ipv4.icanhazip.com/cdn-cgi/trace" && watch.ElapsedMilliseconds < 1500);
            Check("Losing requests canceled without waiting for completion", canceledLosers == 2 && !slowIp.Task.IsCompleted && !slowGeo.Task.IsCompleted);
            Check("Healthy IPv4 avoids IPv6 fallback", !transport.Calls.Contains(LookupService.IpUrls[2]));
            slowIp.SetResult("1.1.1.1");
            slowGeo.SetResult(Geo("8.8.8.8", "JP"));
            await Task.Delay(50);
            int callsBefore = transport.Calls.Count;
            transport.Handler = (url, token) => Task.FromResult("8.8.8.8");
            watch.Restart();
            result = await service.QueryAsync(true, CancellationToken.None);
            Check("Late loser cannot poison cache; cached flag in " + watch.ElapsedMilliseconds + " ms",
                result.CountryCode == "US" && transport.Calls.Count == callsBefore + 1 && watch.ElapsedMilliseconds < 500);

            transport = new FakeTransport();
            int limited = 0;
            transport.Handler = (url, token) =>
            {
                if (url.StartsWith("https://ipwho.is/"))
                { Interlocked.Increment(ref limited); throw new RateLimitException(TimeSpan.FromHours(2)); }
                if (url.StartsWith("https://ipapi.co/")) return Task.FromResult(BackupGeo("8.8.8.8", "US"));
                return Task.FromResult("8.8.8.8");
            };
            service = new LookupService(transport, () => DateTime.UtcNow);
            result = await service.QueryAsync(true, CancellationToken.None);
            Check("Primary rate limit does not block backup country", result.CountryCode == "US" && limited == 1);

            transport = new FakeTransport();
            transport.Handler = (url, token) =>
            {
                if (url.StartsWith("https://ipwho.is/")) return Task.FromResult(Geo("1.1.1.1", "AU"));
                if (url.StartsWith("https://ipapi.co/")) return Task.FromResult(BackupGeo("8.8.8.8", "US"));
                return Task.FromResult("8.8.8.8");
            };
            result = await new LookupService(transport, () => DateTime.UtcNow).QueryAsync(false, CancellationToken.None);
            Check("Invalid primary response cannot win country race", result.CountryCode == "US" && result.Ip == "8.8.8.8");

            var stuck = new TaskCompletionSource<string>();
            transport = new FakeTransport();
            transport.Handler = (url, token) => stuck.Task;
            using (var cts = new CancellationTokenSource())
            {
                service = new LookupService(transport, () => DateTime.UtcNow);
                watch.Restart();
                Task<Snapshot> query = service.QueryAsync(true, cts.Token);
                cts.CancelAfter(50);
                try { await query; Check("Cancellation interrupts uncooperative transport", false); }
                catch (OperationCanceledException) { Check("Cancellation interrupts uncooperative transport", watch.ElapsedMilliseconds < 500); }
            }
            stuck.SetException(new HttpRequestException("late losing failure"));

            var deadlineIp = new TaskCompletionSource<string>();
            transport = new FakeTransport();
            transport.Handler = (url, token) =>
            {
                if (url == LookupService.IpUrls[0] || url == LookupService.IpUrls[1]) return deadlineIp.Task;
                if (url.StartsWith("https://ipwho.is/")) return Task.FromResult(Geo("2606:4700:4700::1111", "AU"));
                return Task.FromResult("2606:4700:4700::1111");
            };
            watch.Restart();
            result = await new LookupService(transport, () => DateTime.UtcNow).QueryAsync(false, CancellationToken.None);
            Check("Stage deadline bounds uncooperative IPv4 and enables IPv6 in " + watch.ElapsedMilliseconds + " ms",
                result.HasCountry && result.Ip == "2606:4700:4700::1111" && watch.ElapsedMilliseconds < 5500);
            deadlineIp.SetResult("8.8.8.8");
        }

        private static void TrayLatencyTest()
        {
            var slow = new TaskCompletionSource<string>();
            var watch = Stopwatch.StartNew();
            long responseMs = -1;
            var transport = new FakeTransport();
            transport.Handler = (url, token) =>
            {
                if (url.StartsWith("https://ipwho.is/")) return slow.Task;
                if (url.StartsWith("https://ipapi.co/"))
                {
                    Interlocked.Exchange(ref responseMs, watch.ElapsedMilliseconds);
                    return Task.FromResult(BackupGeo("8.8.8.8", "US"));
                }
                return Task.FromResult("8.8.8.8");
            };
            var context = new TrayContext(new LookupService(transport, () => DateTime.UtcNow),
                new Settings { PollSeconds = 60, NotifyOnChange = false }, true);
            long visibleMs = -1;
            using (var timer = new System.Windows.Forms.Timer { Interval = 15 })
            {
                timer.Tick += (sender, args) =>
                {
                    if (context.VisibleCountry == "US") visibleMs = watch.ElapsedMilliseconds;
                    if (visibleMs >= 0 || watch.ElapsedMilliseconds > 2500) { timer.Stop(); context.ExitThread(); }
                };
                timer.Start();
                Application.Run(context);
            }
            context.Dispose();
            slow.SetResult(Geo("8.8.8.8", "JP"));
            Check("Real tray shows backup flag in " + visibleMs + " ms despite stuck primary", visibleMs >= 0 && visibleMs < 1500);
            Check("Country response to visible tray takes " + (visibleMs - responseMs) + " ms", responseMs >= 0 && visibleMs >= responseMs && visibleMs - responseMs < 250);
        }
        private static void TrayRetryTest(bool stale = false)
        {
            var lookup = new RetryLookup { Stale = stale };
            var watch = Stopwatch.StartNew();
            var context = new TrayContext(lookup, new Settings { PollSeconds = 60, NotifyOnChange = false }, true);
            bool sawUnknown = false;
            long recoveredMs = -1;
            using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
            {
                timer.Tick += (sender, args) =>
                {
                    if (context.Current != null && context.Current.HasIp && (!context.Current.HasCountry || context.Current.CountryIsStale))
                        sawUnknown = !stale || (context.VisibleCountry == "US" && context.TooltipText.Contains("缓存待更新"));
                    if (context.VisibleCountry == "US" && !context.Current.CountryIsStale) recoveredMs = watch.ElapsedMilliseconds;
                    if (recoveredMs >= 0 || watch.ElapsedMilliseconds > 6500) { timer.Stop(); context.ExitThread(); }
                };
                timer.Start();
                Application.Run(context);
            }
            context.Dispose();
            Check("60-second polling still retries " + (stale ? "stale cached" : "unknown") + " country in " + recoveredMs + " ms",
                sawUnknown && lookup.Calls == 2 && recoveredMs >= 4500 && recoveredMs < 6500);
        }
        private static void IconTests()
        {
            int count = 0;
            foreach (string resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                if (!resource.StartsWith("Flags.")) continue;
                string code = resource.Substring(6, 2);
                using (var icon = FlagIcons.Create(code, 32))
                using (var bitmap = icon.ToBitmap())
                {
                    if (bitmap.Width != 32) throw new Exception("Invalid flag " + code);
                }
                count++;
            }
            Check("All bundled flags decode and create Windows icons (" + count + ")", count >= 240);
            using (var unknown = FlagIcons.Create(null, 16))
            using (var missing = FlagIcons.Create("ZZ", 16))
                Check("Offline and unknown-country fallback icons", unknown.Width == 16 && missing.Width == 16);
            using (var sheet = new Bitmap(480, 120))
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(240, 244, 248));
                string[] codes = { "CN", "US", "JP", "GB", "DE", "SG", "FR", null };
                using (var font = new Font("Segoe UI", 9))
                {
                    for (int i = 0; i < codes.Length; i++)
                    {
                        using (var icon = FlagIcons.Create(codes[i], 32)) g.DrawIcon(icon, i * 60 + 14, 20);
                        using (var icon = FlagIcons.Create(codes[i], 16)) g.DrawIcon(icon, i * 60 + 22, 62);
                        g.DrawString(codes[i] ?? "--", font, Brushes.Black, i * 60 + 20, 88);
                    }
                }
                Directory.CreateDirectory("test-results");
                sheet.Save("test-results/flags-preview.png", System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private static void SmokeTest()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var lookup = new SmokeLookup();
            var context = new TrayContext(lookup, new Settings { PollSeconds = 1, NotifyOnChange = false }, true);
            DateTime start = DateTime.UtcNow;
            bool invalidated = false, sawFresh = false, sawPoll = false, staleSeen = false;
            using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
            {
                timer.Tick += (sender, args) =>
                {
                    double elapsed = (DateTime.UtcNow - start).TotalSeconds;
                    if (!invalidated && elapsed > .6)
                    { context.RequestRefresh(); invalidated = true; }
                    if (context.VisibleCountry == "US") staleSeen = true;
                    if (context.VisibleCountry == "JP") sawFresh = true;
                    if (context.VisibleCountry == "DE") sawPoll = true;
                    if (elapsed > 4.5) { timer.Stop(); context.ExitThread(); }
                };
                timer.Start();
                Application.Run(context);
            }
            context.Dispose();
            Check("Real WinForms loop rejects stale network reply", invalidated && !staleSeen);
            Check("Real tray updates after simulated network change", sawFresh);
            Check("Real tray updates again through automatic polling", sawPoll);
            Check("Tray exits cleanly without making HTTP requests", lookup.Calls >= 3);
        }
    }
}
