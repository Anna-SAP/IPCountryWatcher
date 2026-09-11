using System;
using System.Collections.Generic;
using System.Drawing;
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
        { Calls.Add(url); return Handler(url, token); }
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

    internal static class Tests
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

        [STAThread]
        private static int Main()
        {
            try
            {
                CoreTests().GetAwaiter().GetResult();
                IconTests();
                SmokeTest();
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
            now = now.AddMinutes(6);
            Snapshot recovered = await service.QueryAsync(true, CancellationToken.None);
            Check("Geo failure retries even with unchanged IP", recovered.CountryCode == "JP" && geoCalls == 4);

            transport = new FakeTransport();
            transport.Handler = (url, token) =>
            {
                if (url == LookupService.IpUrls[0]) throw new HttpRequestException("primary down");
                if (url.StartsWith("https://ipwho.is/")) return Task.FromResult(Geo("1.1.1.1", "AU"));
                return Task.FromResult("1.1.1.1");
            };
            service = new LookupService(transport, () => now);
            Snapshot fallback = await service.QueryAsync(false, CancellationToken.None);
            Check("IP service failover", fallback.Source == "checkip.amazonaws.com" && fallback.HasCountry);
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
