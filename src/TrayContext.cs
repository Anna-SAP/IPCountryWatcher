using System;
using System.Drawing;
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
        private readonly ToolStripMenuItem timeItem = new ToolStripMenuItem("尚未完成查询");
        private readonly ToolStripMenuItem copyItem = new ToolStripMenuItem("复制公网 IP");
        private readonly ToolStripMenuItem startupItem = new ToolStripMenuItem("开机启动");
        private readonly ToolStripMenuItem processesItem = new ToolStripMenuItem("进程 IP 监控…");
        private readonly System.Windows.Forms.Timer processTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private ProcessMonitor processMonitor;
        private ProcessMonitorForm processForm;
        private Icon ownedIcon;
        private string iconCode = "#";
        private readonly TrayDisplayState displayState = new TrayDisplayState();
        private readonly Func<DateTime> clock;
        private Snapshot current { get { return displayState.Current; } }
        private string status = "正在启动";
        private Snapshot lastKnown;
        private CancellationTokenSource activeRequest;
        private int failures;
        private volatile bool closing;
        internal string VisibleCountry { get { return iconCode; } }
        internal Snapshot Current { get { return current; } }
        internal string StatusText { get { return statusItem.Text; } }
        internal string TooltipText { get { return tray.Text; } }
        internal bool CanCopyIp { get { return copyItem.Enabled; } }

        public TrayContext(ILookup lookup, Settings settings, bool testing, Func<DateTime> clock = null)
        {
            this.lookup = lookup;
            this.settings = settings;
            this.testing = testing;
            this.clock = clock ?? (() => DateTime.UtcNow);
            IntPtr handle = dispatcher.Handle;
            title.Enabled = ipItem.Enabled = countryItem.Enabled = statusItem.Enabled = timeItem.Enabled = false;
            copyItem.Enabled = false;
            menu.Font = new Font("Microsoft YaHei UI", 9f);
            menu.Items.AddRange(new ToolStripItem[] { title, new ToolStripSeparator(), ipItem, countryItem, statusItem, timeItem, new ToolStripSeparator() });
            menu.Items.Add("立即刷新", null, (s, e) => RequestRefresh());
            menu.Items.Add(copyItem);
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
            var notify = new ToolStripMenuItem("IP 变化时通知") { Checked = settings.NotifyOnChange, CheckOnClick = true };
            notify.Click += (s, e) => { settings.NotifyOnChange = notify.Checked; SaveSettings(); };
            menu.Items.Add(notify);
            startupItem.Checked = !testing && IsStartupEnabled();
            startupItem.Click += (s, e) => ToggleStartup();
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关于", null, (s, e) => MessageBox.Show(
                "IP 国旗监视器 " + Application.ProductVersion + "\n\n网络变化时自动检查，定时轮询公网出口。\n灰色地球表示正在查询或结果未确认。\n\nIP：icanhazip / Cloudflare trace / ifconfig.me\n国家：ipwho.is / ipapi.co\n国旗：Flagpedia.net / flagcdn.com（已内嵌）\n\n这些查询服务会获知请求的公网 IP。\n显示的是本程序请求所经过的出口，分流代理下\n可能与浏览器或其他应用不同。\n\n双击图标可立即刷新；右键打开菜单。",
                "关于 IP 国旗监视器", MessageBoxButtons.OK, MessageBoxIcon.Information));
            menu.Items.Add("退出", null, (s, e) => ExitThread());
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => RequestRefresh();
            SetIcon(null);
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

        private void SetIcon(string code)
        {
            if (code == iconCode) return;
            var replacement = FlagIcons.Create(code, Math.Max(16, SystemInformation.SmallIconSize.Width));
            tray.Icon = replacement;
            var old = ownedIcon;
            ownedIcon = replacement;
            iconCode = code;
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
            displayState.Invalidate(clearPrevious);
            status = "正在重新确认网络出口…";
            RenderState();
            if (debounce == TimeSpan.Zero) Tick(null, EventArgs.Empty);
        }

        private async void Tick(object sender, EventArgs args)
        {
            if (closing) return;
            // Expire a retained flag even while a request or the retry timer is pending.
            if (iconCode != null && (current == null || !current.HasCountry) && displayState.Displayed(clock()) == null)
                RenderState();
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
                failures = result.HasIp ? 0 : Math.Min(5, failures + 1);
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
                // An unresolved country must not wait for a long user-selected IP polling interval.
                if (current != null && current.HasIp && (!current.HasCountry || current.CountryIsStale)) seconds = Math.Min(5, seconds);
                schedule.Complete(generation, clock(), TimeSpan.FromSeconds(seconds));
            }
        }

        private void Apply(Snapshot result)
        {
            Snapshot previous = lastKnown;
            displayState.Accept(result);
            status = result.Error ?? "监控中 · " + result.Source;
            RenderState();
            if (result.HasIp)
            {
                if (!testing && settings.NotifyOnChange && previous != null && previous.Ip != result.Ip)
                {
                    tray.ShowBalloonTip(4000, "公网 IP 已变化",
                        previous.Ip + " → " + result.Ip + "\n" + (result.Country ?? "国家待识别"), ToolTipIcon.Info);
                }
                lastKnown = result;
            }
        }

        private void RenderState()
        {
            Snapshot shown = displayState.Displayed(clock());
            bool retained = shown != null && shown != current;
            SetIcon(shown == null ? null : shown.CountryCode);
            ipItem.Text = retained ? "上次确认 IP（待复核）：" + shown.Ip :
                "本程序公网 IP：" + (current == null ? "正在查询…" : current.Ip ?? "无法获取");
            countryItem.Text = (retained ? "上次国家 / 地区：" : "国家 / 地区：") +
                (shown == null ? "暂未识别" : shown.Country + " (" + shown.CountryCode + ")" +
                (shown.CountryIsStale ? " · 缓存待更新" : ""));
            statusItem.Text = status + (retained ? " · 暂留上次国旗（最多 2 分钟）" : "");
            timeItem.Text = retained ? "上次确认：" + shown.CheckedUtc.ToLocalTime().ToString("HH:mm:ss") :
                current == null ? "尚未完成本次查询" : "检查时间：" + current.CheckedUtc.ToLocalTime().ToString("HH:mm:ss");
            copyItem.Enabled = current != null && current.HasIp;
            string tooltip = retained ? "待确认 · 上次 " + shown.CountryCode + " · " + shown.Ip :
                shown != null ? (shown.CountryIsStale ? "国家缓存待更新 · " : "") + shown.CountryCode + " · " + shown.Ip :
                current != null && current.HasIp ? "国家待识别 · " + current.Ip : "网络未确认 · 等待重试";
            tray.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
        }

        private void CopyIp()
        {
            if (current == null || !current.HasIp) return;
            try { Clipboard.SetText(current.Ip); }
            catch (System.Runtime.InteropServices.ExternalException) { ShowError("剪贴板正忙，请重试。"); }
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
