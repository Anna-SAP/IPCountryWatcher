using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace IPCountryWatcher
{
    internal sealed class ProcessProbeResult
    {
        public ProcessIdentity Identity;
        public string Source4;
        public string Ip4;
        public string Ip6;
        public string Error4;
        public string Error6;
        public string Error;
        public bool Blocked;
        public DateTime CheckedUtc;
    }

    internal interface IProcessProbe
    {
        Task<ProcessProbeResult> QueryAsync(ProcessIdentity identity, bool proxy, CancellationToken token);
    }

    internal sealed class NativeProcessProbe : IProcessProbe
    {
        private sealed class WireResult
        {
            public int pid { get; set; }
            public string ip4 { get; set; }
            public string ip6 { get; set; }
            public uint source4 { get; set; }
            public uint error4 { get; set; }
            public uint error6 { get; set; }
            public uint error { get; set; }
            public string stage { get; set; }
        }

        public Task<ProcessProbeResult> QueryAsync(ProcessIdentity identity, bool proxy, CancellationToken token)
        { return QueryAsync(identity, proxy, token, false); }

        internal async Task<ProcessProbeResult> QueryAsync(ProcessIdentity identity, bool proxy, CancellationToken token, bool identityOnly)
        {
            token.ThrowIfCancellationRequested();
            var result = await Task.Run(() => Run(identity, proxy, identityOnly)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }

        private static ProcessProbeResult Run(ProcessIdentity identity, bool proxy, bool identityOnly)
        {
            var result = new ProcessProbeResult { Identity = identity };
            try
            {
                if (!identity.IsAlive()) throw new InvalidOperationException("进程已退出或 PID 已被复用");
                string architecture = Architecture(identity.Pid);
                string host = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IPCountryWatcher.ProbeHost." + architecture + ".exe");
                if (!File.Exists(host)) throw new FileNotFoundException("缺少进程探针组件，请重新安装完整版本");
                if (String.IsNullOrEmpty(identity.Path) || identity.Path.Contains("\"") || identity.Path.Contains("\n") || identity.Path.Contains("\r"))
                    throw new InvalidOperationException("无法核实进程路径");
                string arguments = identity.Pid + " " + new DateTime(identity.StartedUtcTicks, DateTimeKind.Utc).ToFileTimeUtc() +
                    " " + (proxy ? "1" : "0") + " \"" + identity.Path + "\" " + (identityOnly ? "0" : "1");
                using (var helper = Process.Start(new ProcessStartInfo(host, arguments) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    var output = helper.StandardOutput.ReadToEndAsync();
                    var errors = helper.StandardError.ReadToEndAsync();
                    if (!helper.WaitForExit(70000))
                    {
                        // Killing the helper does not terminate the remote thread; do not inject again in this PID lifetime.
                        helper.Kill();
                        result.Error = "探针超时；本进程本次运行期间停止自动重试";
                        result.Blocked = true;
                        return result;
                    }
                    string text = output.GetAwaiter().GetResult();
                    errors.GetAwaiter().GetResult();
                    if (text.Length > 2048) throw new FormatException("探针响应过长");
                    var wire = new JavaScriptSerializer { MaxJsonLength = 2048 }.Deserialize<WireResult>(text);
                    if (wire == null) throw new FormatException("探针响应无效");
                    if (helper.ExitCode != 0 || wire.error != 0)
                    {
                        result.Error = "探针不可用：" + (wire.error == 5 ? "访问被拒绝 / 应用保护" :
                            wire.error == 1460 ? "执行超时" : wire.error == 170 ? "已有未结束的探针" : "Windows 错误 " + wire.error) +
                            " (" + wire.stage + ")";
                        result.Blocked = true;
                        return result;
                    }
                    if (wire.pid != identity.Pid || !identity.IsAlive()) throw new InvalidOperationException("进程已变更，丢弃旧结果");
                    result.Source4 = wire.source4 == 2 ? "checkip.amazonaws.com" : "api.ipify.org";
                    result.Ip4 = ReadIp(wire.ip4, AddressFamily.InterNetwork);
                    result.Ip6 = ReadIp(wire.ip6, AddressFamily.InterNetworkV6);
                    result.Error4 = String.IsNullOrEmpty(result.Ip4) ? DescribeNetworkError(wire.error4) : null;
                    result.Error6 = String.IsNullOrEmpty(result.Ip6) ? DescribeNetworkError(wire.error6) : null;
                }
            }
            catch (Exception ex)
            {
                if (!(ex is Win32Exception || ex is IOException || ex is InvalidOperationException || ex is ArgumentException ||
                    ex is FormatException || ex is NotSupportedException)) throw;
                result.Error = ex.Message;
                result.Blocked = true;
            }
            finally { result.CheckedUtc = DateTime.UtcNow; }
            return result;
        }

        private static string ReadIp(string value, AddressFamily family)
        {
            if (String.IsNullOrEmpty(value)) return null;
            string ip = Validation.PublicIp(value);
            if (IPAddress.Parse(ip).AddressFamily != family) throw new FormatException("探针 IP 地址族与查询源不匹配");
            return ip;
        }

        internal static string DescribeNetworkError(uint error)
        {
            if (error == 0) return "未返回公网 IP";
            if (error >= 0x20000000) return "服务返回 HTTP " + (error - 0x20000000);
            if (error == 12002) return "连接超时";
            if (error == 12007) return "域名解析失败 / 此地址族不可用";
            if (error == 12029) return "无法连接 / 此地址族不可用";
            return "网络错误 " + error;
        }

        internal static string Architecture(int pid)
        {
            using (var process = Process.GetProcessById(pid))
            {
                ushort machine, native;
                if (!IsWow64Process2(process.Handle, out machine, out native)) throw new Win32Exception();
                if (native != 0x8664) throw new NotSupportedException("进程探针目前支持 x64 Windows 上的 x64 / x86 应用");
                if (machine == 0) return "x64";
                if (machine == 0x14c) return "x86";
                throw new NotSupportedException("此进程架构暂不支持");
            }
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
    }
}
