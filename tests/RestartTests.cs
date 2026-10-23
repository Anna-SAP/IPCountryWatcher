using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IPCountryWatcher
{
    internal static partial class Tests
    {
        private static void RestartTests()
        {
            string name = @"Local\IPCountryWatcher.Tests." + Guid.NewGuid().ToString("N");
            var held = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var previous = new Thread(() => {
                using (var mutex = Program.AcquireInstance(name, TimeSpan.Zero)) { held.Set(); release.Wait(); mutex.ReleaseMutex(); }
            });
            previous.Start();
            held.Wait();
            Check("A normal second launch is refused while an instance runs", Program.AcquireInstance(name, TimeSpan.Zero) == null);
            var watch = Stopwatch.StartNew();
            ThreadPool.QueueUserWorkItem(state => { Thread.Sleep(300); release.Set(); });
            using (var restarted = Program.AcquireInstance(name, TimeSpan.FromSeconds(5)))
            {
                Check("A restarted instance waits until the previous one has closed instead of reporting it as running",
                    restarted != null && watch.ElapsedMilliseconds >= 250);
                if (restarted != null) restarted.ReleaseMutex();
            }
            previous.Join();
            held.Dispose(); release.Dispose();
            TrayRestartTest();
        }

        private static void TrayRestartTest()
        {
            var voice = new RecordingVoice();
            var settings = new Settings { PollSeconds = 60, NotifyOnChange = false };
            var lookup = new DelegateLookup { Handler = token => Task.FromResult(Measured(DateTime.UtcNow, ConnectivityHealth.Reachable)) };
            var context = new TrayContext(lookup, settings, true, null, voice);
            ToolStripItem restart = null, exit = null, alerts = null, off = null;
            foreach (ToolStripItem item in context.Menu.Items)
            {
                if (item.Text == "重启应用") restart = item;
                if (item.Text == "退出") exit = item;
                var submenu = item as ToolStripMenuItem;
                if (submenu != null && item.Text == "语音播报")
                    foreach (ToolStripItem option in submenu.DropDownItems)
                    {
                        if (option.Text == "连接异常及恢复时播报") alerts = option;
                        if (option.Text == "关闭") off = option;
                    }
            }
            Check("Tray menu offers 重启应用 directly above 退出",
                restart != null && exit != null && context.Menu.Items.IndexOf(restart) + 1 == context.Menu.Items.IndexOf(exit));
            bool alertsEnabled = alerts != null && alerts.Enabled && ((ToolStripMenuItem)alerts).Checked;
            if (off != null) off.PerformClick();
            Check("Voice menu has a connection alert option that 关闭 also silences",
                alertsEnabled && !alerts.Enabled && settings.VoiceMode == VoiceMode.Off);
            var watch = Stopwatch.StartNew();
            using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
            {
                timer.Tick += (s, e) => {
                    if (context.Current != null && restart != null) { timer.Stop(); restart.PerformClick(); }
                    else if (watch.ElapsedMilliseconds > 3000) { timer.Stop(); context.ExitThread(); }
                };
                timer.Start(); Application.Run(context);
            }
            Check("重启应用 ends the tray and releases its resources before a new instance starts",
                context.RestartRequested && voice.Disposed);
            context.Dispose();
        }
    }
}
