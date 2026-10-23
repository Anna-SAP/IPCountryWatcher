using System;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IPCountryWatcher
{
    internal sealed class TrayContext : ApplicationContext
    {
        private readonly ILookup lookup;
        private readonly Settings settings;
        private readonly bool testing;
        private readonly Control dispatcher = new Control();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly RefreshSchedule schedule = new RefreshSchedule();
        private readonly ToolStripMenuItem title = new ToolStripMenuItem("IP 国旗监视器");
        private readonly ToolStripMenuItem ipItem = new ToolStripMenuItem("本程序公网 IP：正在查询…");
        private readonly ToolStripMenuItem countryItem = new ToolStripMenuItem("国家 / 地区：待识别");
        private readonly ToolStripMenuItem statusItem = new ToolStripMenuItem("正在启动");
        private readonly ToolStripMenuItem connectivityItem = new ToolStripMenuItem("连接：待确认");
        private readonly ToolStripMenuItem probesItem = new ToolStripMenuItem("检测尚未完成");
        private readonly ToolStripMenuItem timeItem = new ToolStripMenuItem("尚未完成查询");
        private readonly ToolStripMenuItem copyItem = new ToolStripMenuItem("复制公网 IP");
        private readonly ToolStripMenuItem startupItem = new ToolStripMenuItem("开机启动");
        private readonly ToolStripMenuItem processesItem = new ToolStripMenuItem("进程 IP 监控…");
        private readonly System.Windows.Forms.Timer processTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private ProcessMonitor processMonitor;
        private ProcessMonitorForm processForm;
        private Icon ownedIcon;
        private string iconCode = "#";
        private ConnectionBadge iconBadge;
        private readonly DiagnosticLog diagnostics;
        private readonly INetworkFingerprint fingerprint;
        private string lastFingerprint;
        private DateTime nextFingerprintUtc;
        private bool readingFingerprint;
        private DateTime lastTickUtc;
        private readonly TrayDisplayState displayState = new TrayDisplayState();
        private readonly EgressChangeTracker egressChanges = new EgressChangeTracker();
        private readonly ConnectionAlertTracker connectionAlerts = new ConnectionAlertTracker();
        private readonly IVoice voice;
        // Lets the Windows notification chime finish before the announcement starts.
        private static readonly TimeSpan ChimeDelay = TimeSpan.FromMilliseconds(600);
        private readonly Func<DateTime> clock;
        private Snapshot current { get { return displayState.Current; } }
        private string status = "正在启动";
        private CancellationTokenSource activeRequest;
        private int failures;
        private volatile bool closing;
        internal string VisibleCountry { get { return iconCode; } }
        internal Snapshot Current { get { return current; } }
        internal string StatusText { get { return statusItem.Text; } }
        internal string TooltipText { get { return tray.Text; } }
        internal bool CanCopyIp { get { return copyItem.Enabled; } }
        internal ConnectionBadge VisibleBadge { get { return iconBadge; } }
        internal string ConnectivityText { get { return connectivityItem.Text; } }
        internal ContextMenuStrip Menu { get { return menu; } }
        internal bool RestartRequested { get; private set; }

        public TrayContext(ILookup lookup, Settings settings, bool testing, Func<DateTime> clock = null, IVoice voice = null,
            INetworkFingerprint fingerprint = null)
        {
            this.lookup = lookup;
            this.settings = settings;
            this.testing = testing;
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.voice = voice;
            this.fingerprint = fingerprint ?? (testing ? null : new NetworkFingerprint());
            diagnostics = new DiagnosticLog(this.clock);
            diagnostics.Record("start", "IPCountryWatcher " + Application.ProductVersion);
            IntPtr handle = dispatcher.Handle;
            title.Enabled = ipItem.Enabled = countryItem.Enabled = statusItem.Enabled = timeItem.Enabled = connectivityItem.Enabled = probesItem.Enabled = false;
            copyItem.Enabled = false;
            menu.Font = new Font("Microsoft YaHei UI", 9f);
            menu.Items.AddRange(new ToolStripItem[] { title, new ToolStripSeparator(), ipItem, countryItem, connectivityItem, probesItem, statusItem, timeItem, new ToolStripSeparator() });
            menu.Items.Add("立即刷新", null, (s, e) => RequestRefresh());
            menu.Items.Add(copyItem);
            menu.Items.Add("导出连接诊断…", null, (s, e) => ExportDiagnostics());
            copyItem.Click += (s, e) => CopyIp();
            var interval = new ToolStripMenuItem("检查间隔");
            foreach (int seconds in new[] { 5, 10, 30, 60 })
            {
                int selected = seconds;
                var item = new ToolStripMenuItem(seconds + " 秒") { Checked = settings.PollSeconds == seconds };
                item.Click += (s, e) =>
                {
                    settings.PollSeconds = selected;
                    foreach (ToolStripMenuItem other in interval.DropDownItems) other.Checked = other == item;
                    SaveSettings();
                    RequestRefresh();
                };
                interval.DropDownItems.Add(item);
            }
            menu.Items.Add(interval);
            processesItem.Click += (s, e) => ShowProcessMonitor();
            menu.Items.Add(processesItem);
            menu.Items.Add("监控应用设置…", null, (s, e) => EditApplications());
            var proxy = new ToolStripMenuItem("跟随 Windows 系统代理") { Checked = settings.UseSystemProxy, CheckOnClick = true };
            proxy.Click += (s, e) => { settings.UseSystemProxy = proxy.Checked; SaveSettings(); RequestRefresh(true); };
            menu.Items.Add(proxy);
            var notify = new ToolStripMenuItem("国家 / 地区变化时通知") { Checked = settings.NotifyOnChange, CheckOnClick = true };
            notify.Click += (s, e) => { settings.NotifyOnChange = notify.Checked; SaveSettings(); };
            menu.Items.Add(notify);
            menu.Items.Add(CreateVoiceMenu());
            startupItem.Checked = !testing && IsStartupEnabled();
            startupItem.Click += (s, e) => ToggleStartup();
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关于", null, (s, e) => MessageBox.Show(
                "IP 国旗监视器 " + Application.ProductVersion + "\n\n国旗表示出口国家；连接状态单独验证。\n黄色 !：待确认、部分可达或国家缓存待更新。\n红色 ×：本轮所测连接未确认。\n地球：国家未知，不等于整个系统断网。\n\nIP：icanhazip / Cloudflare trace / ifconfig.me\n连接检测：Cloudflare trace / ifconfig.me\n国家：ipwho.is / ipapi.co\n国旗：Flagpedia.net / flagcdn.com（已内嵌）\n\n这些服务会获知请求的公网 IP。\n检测结果仅代表本程序访问所测服务的路径，\n不能证明微信等其他应用或所有地址族正常。\n\n双击刷新；右键可导出本次运行的连接诊断。",
                "关于 IP 国旗监视器", MessageBoxButtons.OK, MessageBoxIcon.Information));
            menu.Items.Add("重启应用", null, (s, e) => Restart());
            menu.Items.Add("退出", null, (s, e) => ExitThread());
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => RequestRefresh();
            SetIcon(null, ConnectionBadge.Pending);
            tray.Text = "IP 国旗监视器 · 正在查询";
            tray.Visible = true;
            timer.Interval = 100;
            timer.Tick += Tick;
            NetworkChange.NetworkAddressChanged += NetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            timer.Start();
            processTimer.Tick += async (s, e) => {
                if (closing || processMonitor == null) return;
                await processMonitor.PollAsync();
            };
            if (!testing && settings.MonitoredApplications.Exists(a => a.Enabled)) EnsureProcessMonitor();
            dispatcher.BeginInvoke((Action)(() => Tick(null, EventArgs.Empty)));
        }

        private ToolStripMenuItem CreateVoiceMenu()
        {
            var voiceMenu = new ToolStripMenuItem("语音播报");
            var modes = new[] { VoiceMode.IpOrCountry, VoiceMode.CountryOnly, VoiceMode.Off };
            var labels = new[] { "IP 或国家 / 地区变化时", "仅国家 / 地区变化时", "关闭" };
            var items = new ToolStripMenuItem[modes.Length];
            // "关闭" silences every announcement, so this option is unavailable while it is selected.
            var connection = new ToolStripMenuItem("连接异常及恢复时播报") { Checked = settings.AnnounceConnection,
                CheckOnClick = true, Enabled = settings.VoiceMode != VoiceMode.Off };
            connection.Click += (s, e) => { settings.AnnounceConnection = connection.Checked; SaveSettings(); };
            for (int i = 0; i < modes.Length; i++)
            {
                VoiceMode mode = modes[i];
                var item = items[i] = new ToolStripMenuItem(labels[i]) { Checked = settings.VoiceMode == mode };
                item.Click += (s, e) =>
                {
                    settings.VoiceMode = mode;
                    foreach (var other in items) other.Checked = other == item;
                    connection.Enabled = mode != VoiceMode.Off;
                    SaveSettings();
                };
                voiceMenu.DropDownItems.Add(item);
            }
            voiceMenu.DropDownItems.Add(new ToolStripSeparator());
            voiceMenu.DropDownItems.Add(connection);
            voiceMenu.DropDownItems.Add(new ToolStripSeparator());
            string name = voice == null ? null : voice.Name;
            var preview = new ToolStripMenuItem("试听当前出口") { Enabled = name != null };
            // Only the latest query is spoken; a retained flag awaiting confirmation is not a current result.
            preview.Click += (s, e) => Speak(VoiceText.Current(displayState.IsIpFresh(this.clock()) ? current : null), TimeSpan.Zero);
            voiceMenu.DropDownItems.Add(preview);
            voiceMenu.DropDownItems.Add(new ToolStripMenuItem(name != null ? "语音：" + name :
                "未找到简体中文女声，请在 Windows 语音设置中添加中文") { Enabled = false });
            return voiceMenu;
        }

        private void Speak(string text, TimeSpan delay)
        {
            if (voice != null) voice.Speak(text, delay);
        }

        private void EnsureProcessMonitor()
        {
            if (processMonitor != null) return;
            processMonitor = new ProcessMonitor(settings, new NativeProcessProbe(), new LookupService(new HttpTransport(), () => DateTime.UtcNow));
            processTimer.Start();
        }

        internal void ShowProcessMonitor()
        {
            EnsureProcessMonitor();
            if (processForm == null || processForm.IsDisposed)
                processForm = new ProcessMonitorForm(processMonitor, () => current, EditApplications,
                    () => processMonitor.Invalidate(), testing);
            processForm.Show();
            if (processForm.WindowState == FormWindowState.Minimized) processForm.WindowState = FormWindowState.Normal;
            processForm.Activate();
        }

        private void EditApplications()
        {
            using (var form = new ApplicationSettingsForm(settings))
            {
                if (form.ShowDialog(processForm != null && !processForm.IsDisposed ? processForm : null) != DialogResult.OK) return;
                var previous = settings.MonitoredApplications;
                int interval = settings.ProcessPollSeconds;
                settings.MonitoredApplications = form.Applications;
                settings.ProcessPollSeconds = form.PollSeconds;
                try { if (!testing) settings.Save(); }
                catch (Exception ex)
                {
                    settings.MonitoredApplications = previous; settings.ProcessPollSeconds = interval;
                    ShowError("无法保存监控设置：" + ex.Message); return;
                }
                EnsureProcessMonitor(); processMonitor.Invalidate();
            }
        }

        private void SaveSettings()
        {
            if (testing) return;
            try { settings.Save(); }
            catch (Exception ex) { ShowError("无法保存设置：" + ex.Message); }
        }

        private void SetIcon(string code, ConnectionBadge badge)
        {
            if (code == iconCode && badge == iconBadge) return;
            var replacement = FlagIcons.Create(code, Math.Max(16, SystemInformation.SmallIconSize.Width), badge);
            tray.Icon = replacement;
            var old = ownedIcon;
            ownedIcon = replacement;
            iconCode = code;
            iconBadge = badge;
            if (old != null) old.Dispose();
        }

        private void NetworkChanged(object sender, EventArgs args) { PostRefresh(); }
        private void AvailabilityChanged(object sender, NetworkAvailabilityEventArgs args) { PostRefresh(!args.IsAvailable); }
        private void PowerChanged(object sender, PowerModeChangedEventArgs args)
        { if (args.Mode == PowerModes.Resume) PostRefresh(true); }

        private void PostRefresh(bool clearPrevious = false)
        {
            if (closing) return;
            try { dispatcher.BeginInvoke((Action)(() => RequestRefresh(TimeSpan.FromMilliseconds(200), clearPrevious))); }
            catch (InvalidOperationException) { /* Dispatcher is closing. */ }
        }

        internal void RequestRefresh()
        { RequestRefresh(false); }

        internal void RequestRefresh(bool clearPrevious)
        { RequestRefresh(TimeSpan.Zero, clearPrevious); }

        private void RequestRefresh(TimeSpan debounce, bool clearPrevious)
        {
            if (closing) return;
            schedule.Request(clock(), debounce);
            failures = 0;
            if (processMonitor != null) processMonitor.Invalidate();
            if (activeRequest != null) activeRequest.Cancel();
            var invalidatable = lookup as IInvalidatableLookup;
            if (invalidatable != null) invalidatable.Invalidate();
            displayState.Invalidate(clearPrevious);
            diagnostics.Record("invalidate", clearPrevious ? "previous egress cleared" : "previous egress pending verification");
            status = "正在重新确认网络出口…";
            RenderState();
            if (debounce == TimeSpan.Zero) Tick(null, EventArgs.Empty);
        }

        private async void Tick(object sender, EventArgs args)
        {
            if (closing) return;
            DateTime now = clock();
            if (lastTickUtc != DateTime.MinValue && now < lastTickUtc)
            {
                lastTickUtc = now;
                nextFingerprintUtc = DateTime.MinValue;
                diagnostics.Record("clock-change", "clock moved backwards; revalidating measurements");
                RequestRefresh();
                return;
            }
            lastTickUtc = now;
            CheckNetworkFingerprint();
            // Re-render freshness even during an in-flight request or long user polling interval.
            RenderState();
            AnnounceConnection(false);
            int generation = schedule.Begin(clock());
            if (generation < 0) return;
            activeRequest = new CancellationTokenSource();
            var request = activeRequest;
            bool useProxy = settings.UseSystemProxy;
            status = "正在检查…";
            RenderState();
            try
            {
                var result = await Task.Run(() => lookup.QueryAsync(useProxy, request.Token));
                if (closing || !schedule.IsCurrent(generation)) return;
                failures = result.HasIp || (result.Connectivity != null && result.Connectivity.Health != ConnectivityHealth.Unreachable) ? 0 : Math.Min(5, failures + 1);
                Apply(result);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!closing && schedule.IsCurrent(generation))
                {
                    failures = Math.Min(5, failures + 1);
                    Apply(new Snapshot { Error = "查询发生异常，稍后自动重试", CheckedUtc = clock() });
                }
            }
            finally
            {
                activeRequest = null;
                request.Dispose();
                // The first failed check should recover promptly, even with 60-second polling.
                double seconds = failures == 0 ? settings.PollSeconds : Math.Min(60, 5 * Math.Pow(2, failures - 1));
                if (current != null && current.Connectivity != null)
                {
                    // Healthy results have a finite lifetime, including when the user selected 60s.
                    if (current.Connectivity.Health == ConnectivityHealth.Reachable)
                        seconds = Math.Min(seconds, Math.Max(1, (current.Connectivity.CheckedUtc.Add(ConnectivitySnapshot.MaximumAge) - clock()).TotalSeconds - 5));
                    else if (current.Connectivity.Health == ConnectivityHealth.Limited || current.Connectivity.Health == ConnectivityHealth.Checking)
                        seconds = Math.Min(seconds, 5);
                }
                // An unresolved country must not wait for a long user-selected IP polling interval.
                if (current != null && current.HasIp && (!current.HasCountry || current.CountryIsStale)) seconds = Math.Min(5, seconds);
                schedule.Complete(generation, clock(), TimeSpan.FromSeconds(seconds));
            }
        }

        private void Apply(Snapshot result)
        {
            displayState.Accept(result);
            diagnostics.RecordResult(result);
            status = result.Error ?? "监控中 · " + result.Source;
            RenderState();
            // Observe even while notifications are off, so re-enabling them cannot compare against an old result.
            EgressChange change = egressChanges.Observe(result);
            bool changeSpoken = false;
            if (change != null)
            {
                bool balloon = !testing && settings.NotifyOnChange && change.CountryChanged;
                if (balloon)
                {
                    tray.ShowBalloonTip(4000, "国家 / 地区已变化",
                        change.Previous.Country + " → " + result.Country + "\n" + change.Previous.Ip + " → " + result.Ip, ToolTipIcon.Info);
                }
                changeSpoken = settings.VoiceMode == VoiceMode.IpOrCountry || (settings.VoiceMode == VoiceMode.CountryOnly && change.CountryChanged);
                if (changeSpoken) Speak(VoiceText.Change(change), balloon ? ChimeDelay : TimeSpan.Zero);
            }
            AnnounceConnection(changeSpoken);
        }

        // A spoken egress change already tells the user that the connection works again.
        private void AnnounceConnection(bool changeSpoken)
        {
            DateTime now = clock();
            // Observe even while announcements are off, so enabling them mid-outage cannot report a stale state.
            ConnectionAlert alert = connectionAlerts.Observe(now, displayState.IsIpFresh(now), displayState.Health(now));
            if (alert == ConnectionAlert.None) return;
            diagnostics.Record("connection-alert", alert.ToString());
            if (settings.VoiceMode == VoiceMode.Off || !settings.AnnounceConnection ||
                (alert == ConnectionAlert.Restored && changeSpoken)) return;
            Speak(VoiceText.Connection(alert, current), TimeSpan.Zero);
        }

        private void RenderState()
        {
            DateTime now = clock();
            Snapshot shown = displayState.Displayed(now);
            bool retained = shown != null && (shown != current || !displayState.IsIpFresh(now));
            ConnectivityHealth health = displayState.Health(now);
            string healthText = health == ConnectivityHealth.Reachable ? "检测服务可达" :
                health == ConnectivityHealth.Limited ? "部分检测可达" :
                health == ConnectivityHealth.Unreachable ? "所测连接未确认" : "连接待确认";
            SetIcon(shown == null ? null : shown.CountryCode, displayState.Badge(now));
            connectivityItem.Text = "连接：" + healthText + " · " + (settings.UseSystemProxy ? "系统代理模式" : "直连模式（含 VPN）");
            var connectivity = current == null ? null : current.Connectivity;
            probesItem.Text = connectivity == null ? "检测尚未完成；不代表其他应用的连接状态" :
                (health == ConnectivityHealth.Checking ? "上次探测：" : "探测时间：") + connectivity.CheckedUtc.ToLocalTime().ToString("HH:mm:ss") + " · " + ProbeSummary(connectivity);
            ipItem.Text = retained ? "上次确认 IP（待复核）：" + shown.Ip :
                "本程序公网 IP：" + (current == null ? "正在查询…" : current.Ip ?? "无法获取");
            countryItem.Text = (retained ? "上次国家 / 地区：" : "国家 / 地区：") +
                (shown == null ? "暂未识别" : shown.Country + " (" + shown.CountryCode + ")" +
                (shown.CountryIsStale ? " · 缓存待更新" : ""));
            statusItem.Text = status + (retained ? " · 暂留上次国旗（最多 2 分钟）" : "");
            timeItem.Text = retained ? "上次确认：" + shown.CheckedUtc.ToLocalTime().ToString("HH:mm:ss") :
                current == null ? "尚未完成本次查询" : "检查时间：" + current.CheckedUtc.ToLocalTime().ToString("HH:mm:ss");
            copyItem.Enabled = displayState.IsIpFresh(now);
            string tooltip = retained ? healthText + " · 待确认 · 上次 " + shown.CountryCode + " · " + shown.Ip :
                shown != null ? (shown.CountryIsStale ? "国家缓存待更新 · " : "") + shown.CountryCode + " · " + shown.Ip :
                current != null && current.HasIp ? "国家待识别 · " + current.Ip : "网络未确认 · 等待重试";
            if (!retained) tooltip = healthText + " · " + tooltip;
            tray.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
        }

        private void CopyIp()
        {
            if (!displayState.IsIpFresh(clock())) return;
            try { Clipboard.SetText(current.Ip); }
            catch (System.Runtime.InteropServices.ExternalException) { ShowError("剪贴板正忙，请重试。"); }
        }

        private static string ProbeSummary(ConnectivitySnapshot snapshot)
        {
            if (snapshot.Results == null) return "未提供逐项结果";
            return String.Join(" / ", Array.ConvertAll(snapshot.Results, p => p.Source + ": " + (p.Success ? "通过" : FailureText(p.Failure))));
        }

        private static string FailureText(string failure)
        {
            if (failure == "timeout") return "超时";
            if (failure == "dns" || failure == "proxy-dns") return "域名解析失败";
            if (failure == "tls") return "安全连接失败";
            if (failure == "invalid-response") return "返回内容异常";
            if (failure == "previous-request-pending") return "前次请求未结束";
            if (failure != null && failure.StartsWith("http-429", StringComparison.Ordinal)) return "服务限流，稍后重试";
            return "连接未确认";
        }

        private async void CheckNetworkFingerprint()
        {
            if (fingerprint == null || readingFingerprint || clock() < nextFingerprintUtc) return;
            readingFingerprint = true;
            nextFingerprintUtc = clock().AddSeconds(5);
            try
            {
                string value = await Task.Run(() => fingerprint.Read());
                if (closing) return;
                if (lastFingerprint != null && !String.Equals(value, lastFingerprint, StringComparison.Ordinal))
                {
                    diagnostics.Record("network-change", "route, interface, DNS or proxy configuration changed");
                    RequestRefresh();
                }
                lastFingerprint = value;
            }
            catch (Exception) { if (!closing) diagnostics.Record("network-read", "configuration unavailable"); }
            finally { readingFingerprint = false; }
        }

        private void ExportDiagnostics()
        {
            using (var dialog = new SaveFileDialog { Title = "导出本次运行的连接诊断", Filter = "文本文件 (*.txt)|*.txt",
                FileName = "IPCountryWatcher-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                try { File.WriteAllText(dialog.FileName, diagnostics.ExportText(), new System.Text.UTF8Encoding(true)); }
                catch (Exception ex) { ShowError("无法导出诊断：" + ex.Message); }
            }
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static string StartupCommand { get { return "\"" + Application.ExecutablePath + "\""; } }
        private static bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && String.Equals(key.GetValue("IPCountryWatcher") as string, StartupCommand, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }
        private void ToggleStartup()
        {
            if (testing) return;
            try
            {
                bool enable = !IsStartupEnabled();
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enable) key.SetValue("IPCountryWatcher", StartupCommand);
                    else key.DeleteValue("IPCountryWatcher", false);
                }
                startupItem.Checked = IsStartupEnabled();
            }
            catch (Exception ex) { ShowError("无法更新开机启动：" + ex.Message); }
        }

        // Program starts the new instance after this one has released its tray icon, voice, probes and mutex.
        internal void Restart()
        {
            RestartRequested = true;
            ExitThread();
        }

        private void ShowError(string text)
        {
            if (!testing) tray.ShowBalloonTip(4000, "IP 国旗监视器", text, ToolTipIcon.Warning);
        }

        private void Shutdown()
        {
            if (closing) return;
            closing = true;
            timer.Stop();
            processTimer.Stop(); processTimer.Dispose();
            if (processMonitor != null) processMonitor.Dispose();
            if (processForm != null) processForm.Dispose();
            NetworkChange.NetworkAddressChanged -= NetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
            SystemEvents.PowerModeChanged -= PowerChanged;
            if (activeRequest != null) activeRequest.Cancel();
            if (voice != null) voice.Dispose();
            tray.Visible = false;
            tray.Dispose();
            if (ownedIcon != null) ownedIcon.Dispose();
            menu.Dispose();
            timer.Dispose();
            dispatcher.Dispose();
        }
        protected override void ExitThreadCore() { Shutdown(); base.ExitThreadCore(); }
        protected override void Dispose(bool disposing) { if (disposing) Shutdown(); base.Dispose(disposing); }
    }
}
