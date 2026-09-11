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
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 16384 };
            var data = serializer.Deserialize<Dictionary<string, object>>(json);
            object success;
            if (data == null || !data.TryGetValue("success", out success) || !(success is bool) || !(bool)success)
                throw new FormatException("国家查询未成功");
            string ip = GetString(data, "ip");
            if (PublicIp(ip) != expectedIp) throw new FormatException("国家查询返回的 IP 不匹配");
            string code = GetString(data, "country_code").ToUpperInvariant();
            if (code.Length != 2 || code[0] < 'A' || code[0] > 'Z' || code[1] < 'A' || code[1] > 'Z')
                throw new FormatException("国家代码无效");
            string country = GetString(data, "country");
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
        private DateTime geoRetryUtc = DateTime.MinValue;
        private const string GeoBase = "https://ipwho.is/";
        internal static readonly string[] IpUrls = { "https://api.ipify.org", "https://checkip.amazonaws.com", "https://api64.ipify.org" };

        public LookupService(ITransport transport, Func<DateTime> clock)
        { this.transport = transport; this.clock = clock; }

        // Called serially by the UI scheduler; no concurrent cache mutation.
        public async Task<Snapshot> QueryAsync(bool systemProxy, CancellationToken token)
        {
            string ip = null;
            string source = null;
            foreach (string url in IpUrls)
            {
                token.ThrowIfCancellationRequested();
                DateTime until;
                if (endpointCooldown.TryGetValue(url, out until) && clock() < until) continue;
                try
                {
                    ip = Validation.PublicIp(await transport.GetAsync(url, systemProxy, token).ConfigureAwait(false));
                    source = new Uri(url).Host;
                    break;
                }
                catch (RateLimitException ex) { endpointCooldown[url] = clock().Add(ex.RetryAfter); }
                catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); }
                catch (Exception ex) { if (!(ex is HttpRequestException || ex is FormatException || ex is WebException)) throw; }
            }
            token.ThrowIfCancellationRequested();
            if (ip == null) return new Snapshot { Error = "公网 IP 查询失败：请检查网络、代理或稍后重试", CheckedUtc = clock() };

            Snapshot cached;
            if (cache.TryGetValue(ip, out cached) && clock() - cached.CheckedUtc < TimeSpan.FromHours(24))
                return new Snapshot { Ip = ip, CountryCode = cached.CountryCode, Country = cached.Country, Source = source, CheckedUtc = clock() };
            var result = new Snapshot { Ip = ip, Source = source, CheckedUtc = clock() };
            if (clock() < geoRetryUtc)
            {
                result.Error = "国家查询等待重试（" + geoRetryUtc.ToLocalTime().ToString("HH:mm:ss") + "）";
                return result;
            }
            try
            {
                string json = await transport.GetAsync(GeoBase + Uri.EscapeDataString(ip) + "?fields=ip,success,country,country_code,message", systemProxy, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                result = Validation.CountryJson(json, ip);
                result.CheckedUtc = clock();
                result.Source = source;
                if (cache.Count >= 256) cache.Clear();
                cache[ip] = result;
            }
            catch (RateLimitException ex)
            {
                geoRetryUtc = clock().Add(ex.RetryAfter);
                result.Error = "国家查询限流，稍后自动重试";
            }
            catch (OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                geoRetryUtc = clock().AddMinutes(5);
                result.Error = "国家查询超时，稍后自动重试";
            }
            catch (Exception ex)
            {
                if (!(ex is HttpRequestException || ex is FormatException || ex is ArgumentException || ex is InvalidOperationException)) throw;
                geoRetryUtc = clock().AddMinutes(5);
                result.Error = "国家暂时无法识别，5 分钟后自动重试";
            }
            return result;
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
