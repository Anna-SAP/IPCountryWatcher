using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace IPCountryWatcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\IPCountryWatcher.Desktop.v1", out created))
            {
                if (!created)
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
                    using (var context = new TrayContext(new LookupService(new HttpTransport(), () => DateTime.UtcNow), Settings.Load(), false))
                        Application.Run(context);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("程序无法启动：" + ex.Message, "IP 国旗监视器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
