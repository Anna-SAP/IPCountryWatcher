using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace IPCountryWatcher
{
    internal sealed class Snapshot
    {
        public string Ip;
        public string CountryCode;
        public string Country;
        public string Source;
        public string Error;
        public DateTime CheckedUtc;
        public bool CountryIsStale;
        public bool HasIp { get { return !String.IsNullOrEmpty(Ip); } }
        public bool HasCountry { get { return !String.IsNullOrEmpty(CountryCode); } }
    }

    internal interface ILookup
    {
        Task<Snapshot> QueryAsync(bool systemProxy, CancellationToken token);
    }

    internal interface ITransport
    {
        Task<string> GetAsync(string url, bool systemProxy, CancellationToken token);
    }

    internal sealed class RateLimitException : Exception
    {
        public readonly TimeSpan RetryAfter;
        public RateLimitException(TimeSpan retryAfter) : base("查询服务暂时限流")
        { RetryAfter = retryAfter; }
    }

    internal sealed class HttpTransport : ITransport
    {
        public async Task<string> GetAsync(string url, bool systemProxy, CancellationToken token)
        {
            // Recreate the handler so a changed Windows proxy is picked up on the next poll.
            using (var handler = new HttpClientHandler())
            {
                handler.UseProxy = systemProxy;
                if (systemProxy) handler.Proxy = WebRequest.GetSystemWebProxy();
                handler.AllowAutoRedirect = false;
                using (var client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromSeconds(4);
                    client.MaxResponseContentBufferSize = 16384;
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("IPCountryWatcher/1.0");
                    client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
                    using (var response = await client.GetAsync(url, token).ConfigureAwait(false))
                    {
                        if ((int)response.StatusCode == 429)
                        {
                            var retry = response.Headers.RetryAfter;
                            var delay = retry != null && retry.Delta.HasValue ? retry.Delta.Value :
                                retry != null && retry.Date.HasValue ? retry.Date.Value.UtcDateTime - DateTime.UtcNow : TimeSpan.FromHours(24);
                            if (delay < TimeSpan.FromMinutes(1)) delay = TimeSpan.FromMinutes(1);
                            throw new RateLimitException(delay);
                        }
                        response.EnsureSuccessStatusCode();
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }
        }
    }

    internal static class Validation
    {
        public static string PublicIp(string raw)
        {
            IPAddress address;
            string value = (raw ?? "").Trim();
            if (value.Length > 45 || !IPAddress.TryParse(value, out address) || IPAddress.IsLoopback(address))
                throw new FormatException("服务未返回有效的公网 IP");
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            byte[] b = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                // Reject private, shared, loopback, link-local, multicast and documentation ranges.
                if (value.IndexOf('.') < 0 || value.Split('.').Length != 4 ||
                    b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 ||
                    (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||
                    (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                    (b[0] == 192 && b[1] == 168) || (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) ||
                    (b[0] == 198 && (b[1] == 18 || b[1] == 19)) ||
                    (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113))
                    throw new FormatException("服务返回了非公网 IPv4 地址");
            }
            else if ((b[0] & 0xE0) != 0x20 || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8))
                throw new FormatException("服务返回了非公网 IPv6 地址");
            return address.ToString();
        }

        public static Snapshot CountryJson(string json, string expectedIp)
        { return CountryJson(json, expectedIp, false); }

        public static Snapshot CountryJson(string json, string expectedIp, bool ipapi)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 16384 };
            var data = serializer.Deserialize<Dictionary<string, object>>(json);
            object success;
            if (data == null || (ipapi ? data.ContainsKey("error") :
                !data.TryGetValue("success", out success) || !(success is bool) || !(bool)success))
                throw new FormatException("国家查询未成功");
            string ip = GetString(data, "ip");
            if (PublicIp(ip) != expectedIp) throw new FormatException("国家查询返回的 IP 不匹配");
            string code = GetString(data, "country_code").ToUpperInvariant();
            if (code.Length != 2 || code[0] < 'A' || code[0] > 'Z' || code[1] < 'A' || code[1] > 'Z')
                throw new FormatException("国家代码无效");
            string country = GetString(data, ipapi ? "country_name" : "country");
            try { country = new RegionInfo(code).DisplayName; } catch (ArgumentException) { }
            return new Snapshot { Ip = expectedIp, CountryCode = code, Country = country.Length == 0 ? code : country };
        }

        private static string GetString(Dictionary<string, object> data, string name)
        {
            object value;
            return data.TryGetValue(name, out value) && value is string ? (string)value : "";
        }
    }

    internal sealed class LookupService : ILookup
    {
        private readonly ITransport transport;
        private readonly Func<DateTime> clock;
        private readonly Dictionary<string, Snapshot> cache = new Dictionary<string, Snapshot>();
        private readonly Dictionary<string, DateTime> endpointCooldown = new Dictionary<string, DateTime>();
        private readonly object cooldownLock = new object();
        internal const int HedgeMilliseconds = 200;
        internal const int StageTimeoutMilliseconds = 4000;
        internal static readonly TimeSpan CountryCacheLifetime = TimeSpan.FromHours(24);
        internal static readonly TimeSpan CountryCacheFallbackLifetime = TimeSpan.FromDays(7);
        internal static readonly string[] IpUrls = { "https://api.ipify.org", "https://checkip.amazonaws.com", "https://api64.ipify.org" };

        public LookupService(ITransport transport, Func<DateTime> clock)
        { this.transport = transport; this.clock = clock; }

        // The UI serializes rounds. Only the winning, validated result may enter the cache.
        public async Task<Snapshot> QueryAsync(bool systemProxy, CancellationToken token)
        {
            Snapshot address = await RaceAsync(new[] { IpUrls[0], IpUrls[1] }, null, systemProxy, token).ConfigureAwait(false);
            // Preserve IPv4 preference; do not race IPv6 against a working IPv4 path.
            if (address == null)
                address = await RaceAsync(new[] { IpUrls[2] }, null, systemProxy, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (address == null) return new Snapshot { Error = "公网 IP 查询失败：请检查网络、代理或稍后重试", CheckedUtc = clock() };

            return await ResolveCountryAsync(address.Ip, systemProxy, token, address.Source).ConfigureAwait(false);
        }

        // Resolve an explicit observed IP; never query this monitor's own egress as a substitute.
        internal async Task<Snapshot> ResolveCountryAsync(string ip, bool systemProxy, CancellationToken token, string source)
        {
            var address = new Snapshot { Ip = Validation.PublicIp(ip), Source = source };
            token.ThrowIfCancellationRequested();
            Snapshot cached;
            if (cache.TryGetValue(address.Ip, out cached) && clock() >= cached.CheckedUtc &&
                clock() - cached.CheckedUtc < CountryCacheLifetime)
                return new Snapshot { Ip = address.Ip, CountryCode = cached.CountryCode, Country = cached.Country,
                    Source = address.Source, CheckedUtc = clock() };
            string escaped = Uri.EscapeDataString(address.Ip);
            Snapshot country = await RaceAsync(new[] {
                "https://ipwho.is/" + escaped + "?fields=ip,success,country,country_code,message",
                "https://ipapi.co/" + escaped + "/json/"
            }, address.Ip, systemProxy, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (country == null)
            {
                address.CheckedUtc = clock();
                // Only reuse the exact IP's country, with a hard age limit. Do not renew
                // the cache timestamp on failure, or repeated outages would extend it forever.
                if (cached != null && address.CheckedUtc >= cached.CheckedUtc &&
                    address.CheckedUtc - cached.CheckedUtc < CountryCacheFallbackLifetime)
                {
                    address.CountryCode = cached.CountryCode;
                    address.Country = cached.Country;
                    address.CountryIsStale = true;
                    address.Error = "公网 IP 已确认 · 国家服务暂不可用，沿用同一 IP 的缓存并重试";
                    return address;
                }
                address.Error = "国家暂未识别，将快速重试（限流服务等待 Retry-After）";
                return address;
            }
            country.Source = address.Source;
            country.CheckedUtc = clock();
            if (cache.Count >= 256) cache.Clear();
            cache[address.Ip] = country;
            return country;
        }

        // Start a backup after 200 ms, or immediately on failure. Return the first valid
        // response without waiting for losing requests or a transport that ignores cancellation.
        private async Task<Snapshot> RaceAsync(string[] urls, string expectedIp, bool proxy, CancellationToken token)
        {
            using (var race = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                race.CancelAfter(StageTimeoutMilliseconds);
                var pending = new List<Task<Snapshot>>();
                Task canceled = Task.Delay(Timeout.Infinite, race.Token);
                Task hedge = Task.Delay(HedgeMilliseconds, race.Token);
                int next = 0;
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        if (race.IsCancellationRequested) return null;
                        if (next < urls.Length && (pending.Count == 0 || hedge.IsCompleted))
                        {
                            string url = urls[next++];
                            string key = new Uri(url).Host;
                            DateTime until;
                            bool cooling;
                            lock (cooldownLock) cooling = endpointCooldown.TryGetValue(key, out until) && clock() < until;
                            if (cooling) continue;
                            // HttpClient's proxy/DNS setup can execute synchronously on .NET Framework.
                            CancellationToken attemptToken = race.Token;
                            var task = Task.Run(() => AttemptAsync(url, expectedIp, proxy, attemptToken));
                            ObserveFault(task);
                            pending.Add(task);
                            hedge = Task.Delay(HedgeMilliseconds, race.Token);
                        }
                        if (pending.Count == 0 && next == urls.Length) return null;
                        var waits = new List<Task>();
                        foreach (var task in pending) waits.Add(task);
                        waits.Add(canceled);
                        if (next < urls.Length) waits.Add(hedge);
                        Task done = await Task.WhenAny(waits).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (race.IsCancellationRequested) return null;
                        var completed = done as Task<Snapshot>;
                        if (completed == null) continue;
                        pending.Remove(completed);
                        Snapshot result = await completed.ConfigureAwait(false);
                        if (result != null) return result;
                        // A failed primary should not consume the hedge delay.
                        hedge = Task.FromResult(0);
                    }
                }
                finally { race.Cancel(); }
            }
        }

        private static void ObserveFault(Task task)
        {
            task.ContinueWith(t => { t.Exception.Handle(ex => true); }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private async Task<Snapshot> AttemptAsync(string url, string expectedIp, bool proxy, CancellationToken token)
        {
            try
            {
                string text = await transport.GetAsync(url, proxy, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (expectedIp == null)
                    return new Snapshot { Ip = Validation.PublicIp(text), Source = new Uri(url).Host };
                return Validation.CountryJson(text, expectedIp, new Uri(url).Host == "ipapi.co");
            }
            catch (RateLimitException ex)
            {
                // Cooldown belongs to the service, so one limited provider cannot block its backup.
                Cooldown(url, ex.RetryAfter);
            }
            catch (OperationCanceledException)
            {
                if (!token.IsCancellationRequested && expectedIp != null) Cooldown(url, TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                if (!(ex is HttpRequestException || ex is WebException || ex is FormatException ||
                    ex is ArgumentException || ex is InvalidOperationException)) throw;
                if (!token.IsCancellationRequested && expectedIp != null) Cooldown(url, TimeSpan.FromSeconds(5));
            }
            return null;
        }

        private void Cooldown(string url, TimeSpan delay)
        {
            lock (cooldownLock) endpointCooldown[new Uri(url).Host] = clock().Add(delay);
        }
    }
    // Generation tokens prevent a request started before a network change overwriting newer state.
    internal sealed class RefreshSchedule
    {
        public int Generation { get; private set; }
        public bool Running { get; private set; }
        public DateTime DueUtc { get; private set; }
        public RefreshSchedule() { DueUtc = DateTime.MinValue; }
        public void Request(DateTime now, TimeSpan debounce)
        {
            Generation++;
            DateTime due = now.Add(debounce);
            DueUtc = DueUtc < now || due < DueUtc ? due : DueUtc;
        }
        public int Begin(DateTime now)
        {
            if (Running || now < DueUtc) return -1;
            Running = true;
            return Generation;
        }
        public bool IsCurrent(int generation) { return generation == Generation; }
        public void Complete(int generation, DateTime now, TimeSpan interval)
        {
            Running = false;
            if (IsCurrent(generation)) DueUtc = now.Add(interval);
        }
    }
}
