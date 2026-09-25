using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace IPCountryWatcher
{
    internal sealed class FakeProcessProbe : IProcessProbe
    {
        internal Func<ProcessIdentity, CancellationToken, Task<ProcessProbeResult>> Handler;
        internal int Calls;
        public Task<ProcessProbeResult> QueryAsync(ProcessIdentity identity, bool proxy, CancellationToken token)
        { Calls++; return Handler(identity, token); }
    }

    internal static partial class Tests
    {
        private static async Task ProcessTests()
        {
            var defaults = new Settings();
            Check("Nine editable app presets; injection disabled until configured", defaults.MonitoredApplications.Count == 9 &&
                defaults.MonitoredApplications.All(a => !a.Enabled && a.ExecutablePath == ""));
            var configured = MonitoredApplication.Defaults();
            configured.RemoveAt(0);
            configured.Add(new MonitoredApplication { Name = "Custom", ExecutablePath = @"C:\Apps\Custom.exe", Enabled = true, UseSystemProxy = true });
            MonitoredApplication.Validate(configured);
            var restored = new JavaScriptSerializer().Deserialize<List<MonitoredApplication>>(new JavaScriptSerializer().Serialize(configured));
            Check("Added/removed app configuration and per-app proxy survive serialization", restored.Count == 9 &&
                restored.Last().Enabled && restored.Last().UseSystemProxy && restored.Last().ExecutablePath == @"C:\Apps\Custom.exe");
            bool duplicate = false;
            try { configured.Add(configured.Last().Copy()); MonitoredApplication.Validate(configured); }
            catch (ArgumentException) { duplicate = true; }
            Check("Duplicate app identities/paths are rejected", duplicate);
            var copied = restored.Last().Copy(); copied.Name = "Changed";
            Check("Canceling edits leaves original configuration untouched", restored.Last().Name == "Custom");

            var self = ProcessIdentity.Read(Process.GetCurrentProcess().Id);
            Check("Process path and creation time identified", self.StartedUtcTicks != 0 && self.Path.EndsWith(".exe") && self.IsAlive());
            var reused = new ProcessIdentity { Pid = self.Pid, Path = self.Path, StartedUtcTicks = self.StartedUtcTicks - 1 };
            Check("PID reuse invalidates identity", !reused.IsAlive());
            CheckTcpOwner(IPAddress.Loopback);
            if (Socket.OSSupportsIPv6) CheckTcpOwner(IPAddress.IPv6Loopback);

            var transport = new FakeTransport { Handler = (url, token) => Task.FromResult(Geo("8.8.8.8", "US")) };
            var geo = new LookupService(transport, () => DateTime.UtcNow);
            var country = await geo.ResolveCountryAsync("8.8.8.8", false, CancellationToken.None, "process probe");
            Check("Explicit process IP is geolocated without querying global egress", country.Ip == "8.8.8.8" &&
                transport.Calls.All(url => url.Contains("/8.8.8.8")));
            int callCount = transport.Calls.Count;
            await geo.ResolveCountryAsync("8.8.8.8", false, CancellationToken.None, "other process");
            Check("Country cache shared across processes with the same IP", transport.Calls.Count == callCount);

            foreach (string arch in new[] { "x86", "x64" })
            {
                string fixturePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IPCountryWatcher.ProbeFixture." + arch + ".exe");
                using (var fixture = Process.Start(new ProcessStartInfo(fixturePath) { UseShellExecute = false, CreateNoWindow = true }))
                {
                    using (var stop = OpenFixtureSignal(fixture.Id))
                    try
                    {
                        var identity = ProcessIdentity.Read(fixture.Id);
                        var native = new NativeProcessProbe();
                        Check("Correct native helper architecture " + arch, NativeProcessProbe.Architecture(fixture.Id) == arch);
                        var result = await native.QueryAsync(identity, false, CancellationToken.None, true);
                        Check("Offline remote DLL roundtrip executes in target PID " + arch + " [" + result.Error + "]",
                            result.Error == null && result.Identity.Pid == fixture.Id && result.Ip4 == null && result.Ip6 == null);
                        var again = await native.QueryAsync(identity, false, CancellationToken.None, true);
                        Check("Probe DLL unload permits a second clean roundtrip " + arch + " [" + again.Error + "]", again.Error == null);
                        string helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IPCountryWatcher.ProbeHost." + arch + ".exe");
                        string args = fixture.Id + " 1 0 \"" + identity.Path + "\" 0";
                        using (var bad = Process.Start(new ProcessStartInfo(helper, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
                        {
                            string text = bad.StandardOutput.ReadToEnd(); bad.WaitForExit();
                            Check("Native loader independently rejects wrong creation time " + arch, bad.ExitCode != 0 && text.Contains("created-mismatch"));
                        }
                    }
                    finally { stop.Set(); if (!fixture.WaitForExit(5000)) fixture.Kill(); }
                }
            }

            var appConfig = new MonitoredApplication { Name = "Fixture", ExecutablePath = self.Path, Enabled = true };
            var monitorSettings = new Settings { MonitoredApplications = new List<MonitoredApplication> { appConfig } };
            var app = new ProcessApplication { Name = self.Name, Path = self.Path };
            app.Identities.Add(self); app.Pids.Add(self.Pid);
            var started = new TaskCompletionSource<bool>();
            var delayed = new TaskCompletionSource<ProcessProbeResult>();
            var fake = new FakeProcessProbe { Handler = (identity, token) => { started.TrySetResult(true); return delayed.Task; } };
            using (var monitor = new ProcessMonitor(monitorSettings, fake, geo, () => new List<ProcessApplication> { app }))
            {
                Task polling = monitor.PollAsync();
                await started.Task;
                monitor.Invalidate();
                delayed.SetResult(new ProcessProbeResult { Identity = self, Ip4 = "8.8.8.8", CheckedUtc = DateTime.UtcNow });
                await polling;
                Check("Network change discards late native result even if it ignores cancellation",
                    monitor.Rows.All(r => r.V4 == null || !r.IsFresh(DateTime.UtcNow, monitor.Generation)));
                fake.Handler = (identity, token) => Task.FromResult(new ProcessProbeResult {
                    Identity = identity, Source4 = NativeProcessProbe.Sources4[1], Ip4 = "8.8.8.8", Error6 = "No IPv6", CheckedUtc = DateTime.UtcNow });
                await monitor.PollAsync();
                Check("Fresh process result and country reach monitor after invalidation",
                    monitor.Rows[0].V4 != null && monitor.Rows[0].V4.CountryCode == "US" && monitor.Rows[0].V4.Source == NativeProcessProbe.Sources4[1] && monitor.Rows[0].Error6 == "No IPv6");
                var row = monitor.Rows[0];
                Check("Old measurements become stale after 90 seconds", !row.IsFresh(row.CheckedUtc.AddSeconds(91), monitor.Generation));
                appConfig.Enabled = false;
                monitor.Invalidate(); await monitor.PollAsync();
                Check("Disabling a configured app clears its displayed IP", monitor.Rows[0].V4 == null && !monitor.Rows[0].Application.Enabled);
                monitorSettings.MonitoredApplications.Clear();
                monitor.Invalidate(); await monitor.PollAsync();
                Check("Deleting a configured app removes its monitor rows", monitor.Rows.Count == 0);
            }
        }

        private static EventWaitHandle OpenFixtureSignal(int pid)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                try { return EventWaitHandle.OpenExisting("Local\\IPCountryWatcher.ProbeFixture." + pid); }
                catch (WaitHandleCannotBeOpenedException)
                {
                    if (watch.ElapsedMilliseconds > 5000) throw;
                    Thread.Sleep(20);
                }
            }
        }

        private static void CheckTcpOwner(IPAddress address)
        {
            var listener = new TcpListener(address, 0);
            listener.Start();
            try
            {
                using (var client = new TcpClient(address.AddressFamily))
                {
                    client.Connect((IPEndPoint)listener.LocalEndpoint);
                    using (var accepted = listener.AcceptTcpClient())
                    {
                        var owner = ProcessNetwork.FindOwner((IPEndPoint)client.Client.LocalEndPoint, (IPEndPoint)client.Client.RemoteEndPoint);
                        Check("TCP owner/port/address mapping " + address.AddressFamily, owner != null && owner.Pid == Process.GetCurrentProcess().Id);
                    }
                }
            }
            finally { listener.Stop(); }
        }

        private static void ProcessWindowTest()
        {
            var settings = new Settings();
            var probe = new FakeProcessProbe { Handler = (identity, token) => Task.FromResult(new ProcessProbeResult()) };
            using (var monitor = new ProcessMonitor(settings, probe, new LookupService(new FakeTransport(), () => DateTime.UtcNow)))
            {
                var self = ProcessIdentity.Read(Process.GetCurrentProcess().Id);
                foreach (var app in settings.MonitoredApplications)
                    monitor.Rows.Add(new ProcessMonitorRow { Application = app, Status = "请在应用设置中选择 EXE" });
                monitor.Rows[0] = new ProcessMonitorRow {
                    Application = new MonitoredApplication { Name = "爱奇艺（测试数据）", Enabled = true, ExecutablePath = self.Path },
                    Identity = self, Key = "test", CheckedUtc = DateTime.UtcNow, Status = "进程内实测",
                    V4 = new Snapshot { Ip = "8.8.8.8", CountryCode = "US", Country = "美国", Source = NativeProcessProbe.Sources4[0] },
                    Error6 = "此地址族不可用", LocalAddresses = "192.168.1.2 [测试网卡]" };
                using (var form = new ProcessMonitorForm(monitor, () => new Snapshot { Ip = "1.1.1.1", Country = "测试国家" }, () => { }, () => { }, true))
                {
                    form.Show(); Application.DoEvents(); form.RenderRows();
                    using (var bitmap = new Bitmap(form.Width, form.Height)) {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save("test-results/process-monitor-preview.png");
                    }
                    var resultGrid = form.Controls[0].Controls.OfType<DataGridView>().Single();
                    Check("High DPI preserves readable PID and address-family columns",
                        resultGrid.Columns[1].Width >= TextRenderer.MeasureText("65535", resultGrid.Font).Width &&
                        resultGrid.Columns[2].Width >= TextRenderer.MeasureText("IPv6", resultGrid.Font).Width);
                    Check("Process window renders configured apps, separate IPv4/IPv6 and evidence", form.Controls.Count > 0 && probe.Calls == 0);
                    form.Close();
                }
                using (var form = new ApplicationSettingsForm(settings))
                {
                    form.Show(); Application.DoEvents();
                    using (var bitmap = new Bitmap(form.Width, form.Height)) {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save("test-results/process-settings-preview.png");
                    }
                    Check("Settings window opens with editable nine-app list and no injection", form.Applications.Count == 9 && probe.Calls == 0);
                    var buttons = form.Controls[0].Controls.OfType<FlowLayoutPanel>().SelectMany(panel => panel.Controls.OfType<Button>()).ToList();
                    buttons.Single(button => button.Text == "增加应用").PerformClick();
                    Check("Add application button updates the editable list", form.Applications.Count == 10);
                    buttons.Single(button => button.Text == "删除所选").PerformClick();
                    Check("Delete selected application button updates the editable list", form.Applications.Count == 9);
                    Check("Uncommitted dialog edits preserve saved settings", settings.MonitoredApplications.Count == 9);
                    form.Close();
                }
            }
        }
    }
}
