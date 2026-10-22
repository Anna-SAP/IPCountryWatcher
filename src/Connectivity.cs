using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace IPCountryWatcher
{
    internal enum ConnectivityHealth { Checking, Reachable, Limited, Unreachable }

    internal sealed class ProbeResult
    {
        public string Source;
        public bool Success;
        public string Failure;
        public long ElapsedMilliseconds;
    }

    // Reachability describes only the selected HTTPS targets on this process's
    // configured proxy path. It cannot establish another application's health.
    internal sealed class ConnectivitySnapshot
    {
        internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(30);
        public ConnectivityHealth Health;
        public DateTime CheckedUtc;
        public ProbeResult[] Results;
        public bool SystemProxy;

        internal bool IsFresh(DateTime now)
        { return now >= CheckedUtc && now - CheckedUtc < MaximumAge; }
    }

    internal interface IConnectivityProbe
    {
        Task<ConnectivitySnapshot> CheckAsync(bool systemProxy, CancellationToken token);
    }

    internal interface IInvalidatableLookup
    {
        void Invalidate();
    }

    internal sealed class ConnectivityProbe : IConnectivityProbe
    {
        internal static readonly string[] Urls = {
            "https://www.cloudflare.com/cdn-cgi/trace", "https://ifconfig.me/ip"
        };
        private readonly ITransport transport;
        private readonly Func<DateTime> clock;
        private readonly int timeoutMilliseconds;
        private readonly object attemptGate = new object();
        // There are only two configured hosts. A broken transport can leave at
        // most one unfinished request per host, rather than one per retry.
        private readonly Dictionary<string, Task<string>> outstanding = new Dictionary<string, Task<string>>();
        private readonly Dictionary<string, DateTime> cooldowns = new Dictionary<string, DateTime>();

        internal ConnectivityProbe(ITransport transport, Func<DateTime> clock, int timeoutMilliseconds = 4000)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            if (clock == null) throw new ArgumentNullException("clock");
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            this.transport = transport;
            this.clock = clock;
            this.timeoutMilliseconds = timeoutMilliseconds;
        }

        public async Task<ConnectivitySnapshot> CheckAsync(bool systemProxy, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(timeoutMilliseconds);
                try
                {
                    // Both independent hosts start now, rather than treating a second
                    // path on the same host as an independent successful probe.
                    var first = AttemptAsync(Urls[0], true, systemProxy, budget.Token);
                    var second = AttemptAsync(Urls[1], false, systemProxy, budget.Token);
                    ProbeResult[] results = await Task.WhenAll(first, second).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    int successful = (results[0].Success ? 1 : 0) + (results[1].Success ? 1 : 0);
                    return new ConnectivitySnapshot {
                        Health = successful == 2 ? ConnectivityHealth.Reachable :
                            successful == 1 ? ConnectivityHealth.Limited : ConnectivityHealth.Unreachable,
                        CheckedUtc = clock(), SystemProxy = systemProxy, Results = results
                    };
                }
                finally { budget.Cancel(); }
            }
        }

        private async Task<ProbeResult> AttemptAsync(string url, bool trace, bool systemProxy, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            var result = new ProbeResult { Source = LookupService.SourceName(url) };
            try
            {
                token.ThrowIfCancellationRequested();
                // DNS/proxy initialization may block synchronously on .NET Framework.
                // Racing its task also bounds transports which ignore cancellation.
                Task<string> work;
                string host = new Uri(url).Host;
                lock (attemptGate)
                {
                    DateTime until;
                    if (cooldowns.TryGetValue(host, out until) && clock() < until)
                    {
                        result.Failure = "http-429-cooldown";
                        result.ElapsedMilliseconds = watch.ElapsedMilliseconds;
                        return result;
                    }
                    Task<string> previous;
                    if (outstanding.TryGetValue(host, out previous) && !previous.IsCompleted)
                    {
                        result.Failure = "previous-request-pending";
                        result.ElapsedMilliseconds = watch.ElapsedMilliseconds;
                        return result;
                    }
                    work = Task.Run(() => transport.GetAsync(url, systemProxy, token));
                    outstanding[host] = work;
                    ConnectivityTasks.ObserveFault(work.ContinueWith(completed => {
                        lock (attemptGate)
                        {
                            Task<string> current;
                            if (outstanding.TryGetValue(host, out current) && Object.ReferenceEquals(current, completed))
                                outstanding.Remove(host);
                        }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
                }
                ConnectivityTasks.ObserveFault(work);
                Task canceled = Task.Delay(Timeout.Infinite, token);
                if (await Task.WhenAny(work, canceled).ConfigureAwait(false) != work)
                    throw new OperationCanceledException(token);
                string body = await work.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (trace) Validation.ResponseIp(body); else Validation.PublicIp(body);
                result.Success = true;
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException || ex is StackOverflowException ||
                    ex is AccessViolationException || ex is ThreadAbortException) throw;
                result.Failure = FailureCategory(ex);
                var limited = ex as RateLimitException;
                if (limited != null && !token.IsCancellationRequested)
                {
                    DateTime now = clock();
                    TimeSpan delay = limited.RetryAfter < TimeSpan.Zero ? TimeSpan.Zero : limited.RetryAfter;
                    DateTime until = delay > DateTime.MaxValue - now ? DateTime.MaxValue : now.Add(delay);
                    lock (attemptGate) cooldowns[new Uri(url).Host] = until;
                }
            }
            result.ElapsedMilliseconds = watch.ElapsedMilliseconds;
            return result;
        }

        internal static string FailureCategory(Exception exception)
        {
            var httpStatus = exception as HttpStatusException;
            if (httpStatus != null) return "http-" + httpStatus.StatusCode;
            // Never expose exception messages: they may contain URLs, proxy
            // credentials, response bodies or application-specific secrets.
            if (exception is RateLimitException) return "http-429";
            if (exception is OperationCanceledException) return "timeout";
            if (exception is FormatException) return "invalid-response";
            if (exception is AuthenticationException) return "tls";
            var web = exception as WebException;
            if (web != null)
            {
                switch (web.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure: return "dns";
                    case WebExceptionStatus.ProxyNameResolutionFailure: return "proxy-dns";
                    case WebExceptionStatus.ConnectFailure: return "tcp-connect";
                    case WebExceptionStatus.Timeout: return "timeout";
                    case WebExceptionStatus.TrustFailure:
                    case WebExceptionStatus.SecureChannelFailure: return "tls";
                    case WebExceptionStatus.ProtocolError:
                        var response = web.Response as HttpWebResponse;
                        return response == null ? "http" : "http-" + (int)response.StatusCode;
                }
            }
            var socket = exception as SocketException;
            if (socket != null)
            {
                switch (socket.SocketErrorCode)
                {
                    case SocketError.HostNotFound:
                    case SocketError.NoData:
                    case SocketError.TryAgain: return "dns";
                    case SocketError.NetworkUnreachable:
                    case SocketError.HostUnreachable: return "route";
                    case SocketError.TimedOut: return "timeout";
                    default: return "tcp";
                }
            }
            if (exception.InnerException != null) return FailureCategory(exception.InnerException);
            return exception is HttpRequestException ? "http-or-transport" : "transport";
        }
    }

    internal static class ConnectivityTasks
    {
        internal static void ObserveFault(Task task)
        {
            task.ContinueWith(t => { t.Exception.Handle(ex => true); }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        internal static async Task<T> WithCancellation<T>(Task<T> work, CancellationToken token)
        {
            ObserveFault(work);
            if (await Task.WhenAny(work, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false) != work)
                throw new OperationCanceledException(token);
            token.ThrowIfCancellationRequested();
            return await work.ConfigureAwait(false);
        }
    }

    internal sealed class ConnectivityLookup : ILookup, IInvalidatableLookup
    {
        internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(15);
        private readonly ILookup lookup;
        private readonly IConnectivityProbe probe;
        private readonly Func<DateTime> clock;
        private readonly object gate = new object();
        private CancellationTokenSource epochCancellation = new CancellationTokenSource();
        private long epoch;
        private long sampleSequence;
        private long cachedSequence;
        private ConnectivitySnapshot cached;
        private string cachedIp;

        internal ConnectivityLookup(ILookup lookup, IConnectivityProbe probe, Func<DateTime> clock)
        {
            if (lookup == null) throw new ArgumentNullException("lookup");
            if (probe == null) throw new ArgumentNullException("probe");
            if (clock == null) throw new ArgumentNullException("clock");
            this.lookup = lookup;
            this.probe = probe;
            this.clock = clock;
        }

        public void Invalidate()
        {
            CancellationTokenSource previous;
            lock (gate)
            {
                epoch++;
                cached = null;
                cachedIp = null;
                previous = epochCancellation;
                epochCancellation = new CancellationTokenSource();
            }
            try { previous.Cancel(); }
            finally { previous.Dispose(); }
        }

        private bool CanReuse(ConnectivitySnapshot sample, bool systemProxy)
        {
            DateTime now = clock();
            return sample != null && sample.Health == ConnectivityHealth.Reachable &&
                sample.SystemProxy == systemProxy && now >= sample.CheckedUtc &&
                now - sample.CheckedUtc < CacheLifetime && sample.IsFresh(now);
        }

        public async Task<Snapshot> QueryAsync(bool systemProxy, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            CancellationTokenSource round;
            ConnectivitySnapshot health;
            string measuredIp;
            long generation, sequence;
            lock (gate)
            {
                generation = epoch;
                sequence = ++sampleSequence;
                round = CancellationTokenSource.CreateLinkedTokenSource(token, epochCancellation.Token);
                health = CanReuse(cached, systemProxy) ? cached : null;
                measuredIp = cachedIp;
            }
            using (round)
            {
                CancellationToken roundToken = round.Token;
                try
                {
                    // Due health rounds overlap the existing IP/country query.
                    Task<ConnectivitySnapshot> pending = health == null ?
                        Task.Run(() => probe.CheckAsync(systemProxy, roundToken)) : null;
                    if (pending != null) ConnectivityTasks.ObserveFault(pending);
                    var addressTask = Task.Run(() => lookup.QueryAsync(systemProxy, roundToken));
                    Snapshot result = await ConnectivityTasks.WithCancellation(addressTask, roundToken).ConfigureAwait(false);
                    // Cached success cannot conceal a newly failed public-IP query.
                    if (pending == null && (!result.HasIp || result.Ip != measuredIp || !CanReuse(health, systemProxy)))
                        pending = Task.Run(() => probe.CheckAsync(systemProxy, roundToken));
                    if (pending != null)
                        health = await ConnectivityTasks.WithCancellation(pending, roundToken).ConfigureAwait(false);
                    if (!health.IsFresh(clock()))
                        health = await ConnectivityTasks.WithCancellation(
                            Task.Run(() => probe.CheckAsync(systemProxy, roundToken)), roundToken).ConfigureAwait(false);
                    roundToken.ThrowIfCancellationRequested();

                    lock (gate)
                    {
                        if (generation != epoch) throw new OperationCanceledException(roundToken);
                        roundToken.ThrowIfCancellationRequested();
                        // Concurrent rounds may complete out of order. An older round
                        // must never replace a newer round's cached health sample.
                        if (sequence >= cachedSequence && health.IsFresh(clock()))
                        {
                            cached = health;
                            cachedIp = result.HasIp ? result.Ip : null;
                            cachedSequence = sequence;
                        }
                    }
                    result.Connectivity = new ConnectivitySnapshot {
                        Health = !health.IsFresh(clock()) ? ConnectivityHealth.Checking :
                            health.Health == ConnectivityHealth.Unreachable && result.HasIp ?
                            ConnectivityHealth.Limited : health.Health,
                        CheckedUtc = health.CheckedUtc, SystemProxy = systemProxy, Results = health.Results
                    };
                    return result;
                }
                finally { round.Cancel(); }
            }
        }
    }
}
