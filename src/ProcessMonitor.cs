using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IPCountryWatcher
{
    internal sealed class ProcessMonitorRow
    {
        public MonitoredApplication Application;
        public ProcessIdentity Identity;
        public string Key;
        public string Status;
        public string LocalAddresses;
        public int TcpCount;
        public Snapshot V4;
        public Snapshot V6;
        public string Error4;
        public string Error6;
        public DateTime CheckedUtc;
        public int Generation;
        public bool Blocked;

        internal bool IsFresh(DateTime now, int generation)
        { return Generation == generation && CheckedUtc != DateTime.MinValue && now - CheckedUtc < TimeSpan.FromSeconds(90); }
    }

    internal sealed class ProcessMonitor : IDisposable
    {
        private readonly Settings settings;
        private readonly Func<List<ProcessApplication>> inventory;
        private readonly IProcessProbe probe;
        private readonly LookupService geo;
        private readonly SemaphoreSlim slots = new SemaphoreSlim(2);
        private readonly SemaphoreSlim geoSlot = new SemaphoreSlim(1);
        private readonly Dictionary<string, ProcessMonitorRow> results = new Dictionary<string, ProcessMonitorRow>();
        private CancellationTokenSource cancellation = new CancellationTokenSource();
        private bool disposed;
        private bool running;
        private DateTime nextScan;
        internal int Generation { get; private set; }
        internal List<ProcessMonitorRow> Rows { get; private set; }
        internal string Error { get; private set; }
        internal bool Running { get { return running; } }

        internal ProcessMonitor(Settings settings, IProcessProbe probe, LookupService geo, Func<List<ProcessApplication>> inventory = null)
        { this.inventory = inventory ?? ProcessNetwork.Applications; this.settings = settings; this.probe = probe; this.geo = geo; Rows = new List<ProcessMonitorRow>(); }

        internal void Invalidate()
        {
            if (disposed) return;
            Generation++;
            cancellation.Cancel();
            // Native probes already in flight finish and unload normally; generation checks discard their result.
            nextScan = DateTime.MinValue;
        }

        internal async Task PollAsync()
        {
            if (disposed || running || DateTime.UtcNow < nextScan) return;
            running = true;
            if (cancellation.IsCancellationRequested) { cancellation.Dispose(); cancellation = new CancellationTokenSource(); }
            CancellationToken token = cancellation.Token;
            int generation = Generation;
            var configured = settings.MonitoredApplications.Select(a => a.Copy()).ToList();
            int interval = settings.ProcessPollSeconds;
            bool geoProxy = settings.UseSystemProxy;
            try
            {
                var apps = await Task.Run(inventory);
                if (disposed || generation != Generation) return;
                var rows = new List<ProcessMonitorRow>();
                foreach (var config in configured)
                {
                    var app = apps.FirstOrDefault(a => !String.IsNullOrEmpty(config.ExecutablePath) &&
                        String.Equals(a.Path, config.ExecutablePath, StringComparison.OrdinalIgnoreCase));
                    if (app == null)
                    {
                        rows.Add(new ProcessMonitorRow { Application = config, Status = String.IsNullOrEmpty(config.ExecutablePath) ?
                            "请在应用设置中选择 EXE" : "应用未运行", LocalAddresses = "", Generation = generation });
                        continue;
                    }
                    // Probe actual TCP-owning processes, including network subprocesses sharing this executable.
                    // When idle/UDP-only, one oldest process can still issue a fresh independent probe.
                    var identities = app.Identities.Where(p => app.NetworkPids.Contains(p.Pid)).ToList();
                    if (identities.Count == 0) identities = app.Identities.OrderBy(p => p.StartedUtcTicks).Take(1).ToList();
                    foreach (var identity in identities)
                    {
                        string key = config.Id + "|" + identity.Pid + "|" + identity.StartedUtcTicks + "|" + config.UseSystemProxy;
                        ProcessMonitorRow old;
                        var row = new ProcessMonitorRow { Application = config, Identity = identity, Key = key,
                            LocalAddresses = String.Join(", ", app.LocalAddresses), TcpCount = app.TcpCount, Generation = generation,
                            Status = config.Enabled ? "等待进程内探测" : "未启用探针（应用设置）" };
                        if (config.Enabled && results.TryGetValue(key, out old))
                        {
                            if (old.Blocked || old.Generation == generation) row = old;
                            row.Application = config; row.Identity = identity; row.LocalAddresses = String.Join(", ", app.LocalAddresses);
                            row.TcpCount = app.TcpCount;
                        }
                        rows.Add(row);
                    }
                }
                Rows = rows;
                var liveKeys = new HashSet<string>(rows.Where(r => r.Key != null).Select(r => r.Key));
                foreach (string key in results.Keys.Where(k => !liveKeys.Contains(k)).ToArray()) results.Remove(key);
                Error = null;
                var jobs = new List<Task>();
                foreach (var row in rows)
                {
                    if (row.Identity == null || !row.Application.Enabled || row.Blocked) continue;
                    if (row.Generation == generation && DateTime.UtcNow - row.CheckedUtc < TimeSpan.FromSeconds(interval)) continue;
                    jobs.Add(UpdateAsync(row, generation, geoProxy, token));
                }
                await Task.WhenAll(jobs);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!disposed && generation == Generation) Error = "读取进程失败：" + ex.Message; }
            finally
            {
                running = false;
                if (disposed) ReleaseResources();
                if (generation == Generation) nextScan = DateTime.UtcNow.AddSeconds(5);
            }
        }

        private async Task UpdateAsync(ProcessMonitorRow row, int generation, bool geoProxy, CancellationToken token)
        {
            await slots.WaitAsync(token);
            try
            {
                if (generation != Generation || disposed) return;
                row.Status = "正在进程内探测…";
                var measured = await probe.QueryAsync(row.Identity, row.Application.UseSystemProxy, token);
                if (disposed || generation != Generation || !row.Identity.IsAlive()) return;
                row.V4 = row.V6 = null;
                row.Error4 = measured.Error4; row.Error6 = measured.Error6;
                row.CheckedUtc = measured.CheckedUtc; row.Generation = generation;
                row.Blocked = measured.Blocked;
                row.Status = measured.Error ?? "实测完成";
                results[row.Key] = row;
                if (!String.IsNullOrEmpty(measured.Error)) return;
                // Publish the real IP immediately; geolocation may be temporarily unavailable.
                if (measured.Ip4 != null) row.V4 = new Snapshot { Ip = measured.Ip4, Source = measured.Source4 };
                if (measured.Ip6 != null) row.V6 = new Snapshot { Ip = measured.Ip6, Source = "api6.ipify.org" };
                row.Status = row.V4 != null || row.V6 != null ? "进程内实测 · 国家解析中" : "出口未确认";
                await geoSlot.WaitAsync(token);
                try
                {
                    if (row.V4 != null) row.V4 = await geo.ResolveCountryAsync(measured.Ip4, geoProxy, token, measured.Source4 ?? "api.ipify.org");
                    if (row.V6 != null) row.V6 = await geo.ResolveCountryAsync(measured.Ip6, geoProxy, token, "api6.ipify.org");
                }
                finally { geoSlot.Release(); }
                if (disposed || generation != Generation) return;
                row.Status = row.V4 != null || row.V6 != null ? "进程内实测" : "出口未确认";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (!disposed && generation == Generation)
                {
                    row.Status = "探测失败：" + ex.Message;
                    row.V4 = row.V6 = null;
                    row.CheckedUtc = DateTime.UtcNow;
                    results[row.Key] = row;
                }
            }
            finally { slots.Release(); }
        }

        private bool resourcesDisposed;
        private void ReleaseResources()
        {
            if (resourcesDisposed) return;
            resourcesDisposed = true;
            cancellation.Dispose(); slots.Dispose(); geoSlot.Dispose();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cancellation.Cancel();
            if (!running) ReleaseResources();
        }
    }
}
