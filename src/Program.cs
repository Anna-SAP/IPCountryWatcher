using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace IPCountryWatcher
{
    internal static class Program
    {
        private const string InstanceName = @"Local\IPCountryWatcher.Desktop.v1";
        internal const string RestartArgument = "--restart";
        // Covers the previous instance cancelling requests and closing its mutex after its message loop ends.
        internal static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(15);

        [STAThread]
        private static void Main(string[] args)
        {
            bool restart = false;
            using (var mutex = AcquireInstance(InstanceName, Array.IndexOf(args, RestartArgument) >= 0 ? RestartWait : TimeSpan.Zero))
            {
                if (mutex == null)
                {
                    MessageBox.Show("程序已在系统托盘中运行。请查看任务栏右下角或隐藏图标区域。", "IP 国旗监视器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var transport = new HttpTransport())
                    {
                        var lookup = new ConnectivityLookup(new LookupService(transport, () => DateTime.UtcNow),
                            new ConnectivityProbe(transport, () => DateTime.UtcNow), () => DateTime.UtcNow);
                        using (var context = new TrayContext(lookup, Settings.Load(), false,
                            null, new ChineseVoice(), null, null, transport))
                        {
                            if (Array.IndexOf(args, "--process-monitor") >= 0) context.ShowProcessMonitor();
                            Application.Run(context);
                            restart = context.RestartRequested;
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("程序无法启动：" + ex.Message, "IP 国旗监视器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { mutex.ReleaseMutex(); }
            }
            if (!restart) return;
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, RestartArgument) { UseShellExecute = false }); }
            catch (Exception ex)
            {
                MessageBox.Show("无法重新启动：" + ex.Message + "\n请手动启动程序。", "IP 国旗监视器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // The single-instance check is whether this call created the named mutex, so a restarted instance
        // retries until the previous one has closed its handle instead of reporting that it is still running.
        internal static Mutex AcquireInstance(string name, TimeSpan wait)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                bool created;
                var mutex = new Mutex(true, name, out created);
                if (created) return mutex;
                mutex.Dispose();
                if (elapsed.Elapsed >= wait) return null;
                Thread.Sleep(100);
            }
        }
    }
}
