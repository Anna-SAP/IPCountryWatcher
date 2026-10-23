using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace IPCountryWatcher
{
    internal sealed class RecordingVoice : IVoice
    {
        public readonly List<string> Spoken = new List<string>();
        public bool Disposed;
        public string Name { get { return "Test voice"; } }
        public void Speak(string text, TimeSpan delay) { Spoken.Add(text); }
        public void Dispose() { Disposed = true; }
    }

    internal static partial class Tests
    {
        private static VoiceCandidate Voice(string name, string culture, bool female)
        { return new VoiceCandidate { Name = name, Culture = culture, Female = female }; }

        private static void VoiceTests()
        {
            var installed = new List<VoiceCandidate> {
                Voice("Microsoft Huihui Desktop", "zh-CN", true), Voice("Microsoft Kangkang", "zh-CN", false),
                Voice("Microsoft Yaoyao", "zh-CN", true), Voice("Microsoft Zira", "en-US", true) };
            Check("Young female Yaoyao is preferred over Huihui", VoiceSelection.Choose(installed) == "Microsoft Yaoyao");
            installed.Add(Voice("Microsoft Xiaoxiao Online (Natural) - Chinese (Mainland)", "zh-CN", true));
            Check("Online voices are skipped so the spoken IP stays on this PC", VoiceSelection.Choose(installed) == "Microsoft Yaoyao");
            installed.Add(Voice("Microsoft Xiaoxiao (Natural) - Chinese (Mainland)", "zh-CN", true));
            Check("Local natural Xiaoxiao voice is preferred when installed",
                VoiceSelection.Choose(installed) == "Microsoft Xiaoxiao (Natural) - Chinese (Mainland)");
            Check("Male, Traditional Chinese and other-language voices are never chosen",
                VoiceSelection.Choose(new[] { Voice("Microsoft Kangkang", "zh-CN", false), Voice("Microsoft Hanhan", "zh-TW", true),
                    Voice("Microsoft Tracy", "zh-HK", true), Voice("Microsoft Zira", "en-US", true) }) == null);
            Check("Another Simplified Chinese female voice is a fallback",
                VoiceSelection.Choose(new[] { Voice("Vendor Lili", "zh-CN", true) }) == "Vendor Lili");

            Check("IPv4 is read digit by digit in Chinese", VoiceText.SpokenIp("103.25.0.9") == "一零三点二五点零点九");
            Check("IPv6 is not read aloud", VoiceText.SpokenIp("2606:4700:4700::1111") == null);
            var sg = new Snapshot { Ip = "8.8.8.8", CountryCode = "SG", Country = "新加坡" };
            var jp = new Snapshot { Ip = "1.1.1.1", CountryCode = "JP", Country = "日本" };
            var jp6 = new Snapshot { Ip = "2606:4700:4700::1111", CountryCode = "JP", Country = "日本" };
            Check("Country change names both countries and the new IP",
                VoiceText.Change(new EgressChange(sg, jp)) == "网络出口已从新加坡切换到日本，新的 IP 是一点一点一点一。");
            Check("IP-only change names the new IP and the unchanged country",
                VoiceText.Change(new EgressChange(jp, new Snapshot { Ip = "8.8.4.4", CountryCode = "JP", Country = "日本" })) ==
                "公网 IP 已变更为八点八点四点四，出口仍在日本。");
            Check("IPv6 changes are announced without reading the address",
                VoiceText.Change(new EgressChange(jp, jp6)) == "公网 IP 已变更为新的 IPv6 地址，出口仍在日本。" &&
                VoiceText.Change(new EgressChange(sg, jp6)) == "网络出口已从新加坡切换到日本，现在使用 IPv6 地址。");
            Check("Preview speaks only a confirmed current result",
                VoiceText.Current(jp) == "当前网络出口在日本，公网 IP 是一点一点一点一。" &&
                VoiceText.Current(null) == "正在确认网络出口，请稍候。" &&
                VoiceText.Current(new Snapshot { Ip = "1.1.1.1" }) == "正在确认网络出口，请稍候。");

            Check("Speech markup escapes country names and inserts the chime pause",
                ChineseVoice.Markup("波斯尼亚 & 黑塞哥维那", TimeSpan.Zero) == "波斯尼亚 &amp; 黑塞哥维那" &&
                ChineseVoice.Markup("日本", TimeSpan.FromMilliseconds(600)) == "<silence msec=\"600\"/>日本");
            var serializer = new JavaScriptSerializer();
            Check("Settings saved before voice support default to IP or country announcements",
                serializer.Deserialize<Settings>("{\"PollSeconds\":5,\"NotifyOnChange\":false}").VoiceMode == VoiceMode.IpOrCountry &&
                serializer.Deserialize<Settings>(serializer.Serialize(new Settings { VoiceMode = VoiceMode.CountryOnly })).VoiceMode == VoiceMode.CountryOnly);
            Check("Settings saved before connection alerts enable them, and turning them off persists",
                serializer.Deserialize<Settings>("{\"VoiceMode\":1}").AnnounceConnection &&
                !serializer.Deserialize<Settings>(serializer.Serialize(new Settings { AnnounceConnection = false })).AnnounceConnection);
            Check("Connection alerts say what the badge shows without claiming the PC is offline",
                VoiceText.Connection(ConnectionAlert.Failed, null) == "网络连接异常，检测请求均未成功，正在等待重试。" &&
                VoiceText.Connection(ConnectionAlert.Unconfirmed, null) == "网络连接待确认，暂时无法获取公网 IP，正在等待重试。" &&
                VoiceText.Connection(ConnectionAlert.Restored, jp) == "网络连接已恢复，出口在日本。" &&
                VoiceText.Connection(ConnectionAlert.Restored, new Snapshot { Ip = "1.1.1.1" }) == "网络连接已恢复。");

            // Render to a file so the test never plays sound. CI images have no Chinese voice installed.
            string name;
            dynamic voice = ChineseVoice.Create(out name);
            if (voice == null) { Check("No Simplified Chinese female voice installed; speech rendering skipped", true); return; }
            Check("Announcements use over priority, which mixes with other speech instead of queuing or interrupting",
                (int)voice.Priority == ChineseVoice.OverPriority);
            Directory.CreateDirectory("test-results");
            string file = Path.GetFullPath(Path.Combine("test-results", "voice-preview.wav"));
            dynamic stream = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpFileStream", true));
            stream.Open(file, 3, false); // SSFMCreateForWrite
            try
            {
                voice.AudioOutputStream = stream;
                var tokyo = new Snapshot { Ip = "103.25.0.9", CountryCode = "JP", Country = "日本" };
                voice.Speak(ChineseVoice.Markup(VoiceText.Change(new EgressChange(sg, tokyo)), TimeSpan.Zero), ChineseVoice.IsXml);
                voice.Speak(ChineseVoice.Markup(VoiceText.Change(new EgressChange(tokyo, jp)), TimeSpan.FromMilliseconds(600)), ChineseVoice.IsXml);
            }
            finally { stream.Close(); Marshal.FinalReleaseComObject((object)voice); }
            Check("Installed voice " + name + " renders announcements offline", new FileInfo(file).Length > 20000);
            using (var live = new ChineseVoice())
                Check("Tray voice uses the same voice on the default audio device", live.Name == name);
            VoiceMixingTest();
        }

        // Plays on the default audio device at volume 0, so it is silent. Another program's normal-priority
        // speech is still running when the announcement must play; a normal-priority control voice has to wait.
        private static void VoiceMixingTest()
        {
            string name;
            dynamic busy = ChineseVoice.Create(out name), control = ChineseVoice.Create(out name), alert = ChineseVoice.Create(out name);
            try
            {
                busy.Priority = 0; control.Priority = 0;
                busy.Volume = control.Volume = alert.Volume = 0;
                busy.Speak(String.Concat(System.Linq.Enumerable.Repeat("这是一段占用语音设备的较长测试文本。", 12)), ChineseVoice.SpeakAsync);
                // OneCore voices take a few hundred milliseconds to start; SRSEIsSpeaking = 2.
                var starting = Stopwatch.StartNew();
                while ((int)busy.Status.RunningState != 2 && starting.ElapsedMilliseconds < 3000) busy.WaitUntilDone(50);
                if ((int)busy.Status.RunningState != 2)
                { Check("No audio output device; speech mixing check skipped", true); return; }
                control.Speak("对照语音。", ChineseVoice.SpeakAsync);
                alert.Speak("网络连接异常。", ChineseVoice.SpeakAsync);
                bool alertDone = alert.WaitUntilDone(5000);
                Check("Announcement plays while other speech continues, which queued normal voices cannot",
                    alertDone && !busy.WaitUntilDone(0) && !control.WaitUntilDone(0));
            }
            finally
            {
                foreach (dynamic voice in new[] { alert, control, busy })
                { voice.Speak("", ChineseVoice.SpeakAsync | ChineseVoice.PurgeBeforeSpeak); Marshal.FinalReleaseComObject((object)voice); }
            }
        }

        private static void ConnectionAlertTrackerTests()
        {
            DateTime now = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);
            var tracker = new ConnectionAlertTracker();
            Check("A failure before the first confirmation, as Wi-Fi connects at login, alerts only after 15 seconds",
                tracker.Observe(now, false, ConnectivityHealth.Checking) == ConnectionAlert.None &&
                tracker.Observe(now.AddSeconds(2), false, ConnectivityHealth.Unreachable) == ConnectionAlert.None &&
                tracker.Observe(now.AddSeconds(14), false, ConnectivityHealth.Unreachable) == ConnectionAlert.None &&
                tracker.Observe(now.AddSeconds(15), false, ConnectivityHealth.Unreachable) == ConnectionAlert.Failed);
            Check("Recovery is announced once",
                tracker.Observe(now.AddSeconds(20), true, ConnectivityHealth.Reachable) == ConnectionAlert.Restored &&
                tracker.Observe(now.AddSeconds(21), true, ConnectivityHealth.Limited) == ConnectionAlert.None);
            Check("The brief pending state of a refresh or network switch stays silent",
                tracker.Observe(now.AddSeconds(30), false, ConnectivityHealth.Checking) == ConnectionAlert.None &&
                tracker.Observe(now.AddSeconds(44), false, ConnectivityHealth.Checking) == ConnectionAlert.None &&
                tracker.Observe(now.AddSeconds(44), true, ConnectivityHealth.Reachable) == ConnectionAlert.None);
            Check("A failed check after a confirmed connection alerts at once",
                tracker.Observe(now.AddSeconds(50), false, ConnectivityHealth.Unreachable) == ConnectionAlert.Failed);
            bool silent = true;
            for (int second = 55; second < 600; second += 5)
                silent &= tracker.Observe(now.AddSeconds(second), false,
                    second % 10 == 0 ? ConnectivityHealth.Unreachable : ConnectivityHealth.Checking) == ConnectionAlert.None;
            Check("Red and amber badges alternating through a long outage alert only once", silent);
            tracker.Observe(now.AddMinutes(10), true, ConnectivityHealth.Reachable);
            DateTime later = now.AddMinutes(11);
            Check("A partial result or pending check without an IP alerts once it lasts 15 seconds",
                tracker.Observe(later, false, ConnectivityHealth.Limited) == ConnectionAlert.None &&
                tracker.Observe(later.AddSeconds(15), false, ConnectivityHealth.Checking) == ConnectionAlert.Unconfirmed);
            tracker.Observe(later.AddSeconds(20), true, ConnectivityHealth.Reachable);
            DateTime rolledBack = later.AddHours(-1);
            Check("A clock moved backwards restarts the wait instead of alerting early",
                tracker.Observe(later.AddSeconds(30), false, ConnectivityHealth.Checking) == ConnectionAlert.None &&
                tracker.Observe(rolledBack, false, ConnectivityHealth.Checking) == ConnectionAlert.None &&
                tracker.Observe(rolledBack.AddSeconds(14), false, ConnectivityHealth.Checking) == ConnectionAlert.None &&
                tracker.Observe(rolledBack.AddSeconds(15), false, ConnectivityHealth.Checking) == ConnectionAlert.Unconfirmed);
        }

        private static void TrayConnectionAlertTest()
        {
            string failed = "网络连接异常，检测请求均未成功，正在等待重试。", restored = "网络连接已恢复，出口在中国。";
            var cases = new[] {
                new { Mode = VoiceMode.IpOrCountry, Alerts = true, RecoveredIp = "8.8.8.8", Expected = new[] { failed, restored } },
                new { Mode = VoiceMode.IpOrCountry, Alerts = true, RecoveredIp = "1.1.1.1", Expected = new[] { failed, "公网 IP 已变更为一点一点一点一，出口仍在中国。" } },
                new { Mode = VoiceMode.CountryOnly, Alerts = true, RecoveredIp = "1.1.1.1", Expected = new[] { failed, restored } },
                new { Mode = VoiceMode.IpOrCountry, Alerts = false, RecoveredIp = "8.8.8.8", Expected = new string[0] },
                new { Mode = VoiceMode.Off, Alerts = true, RecoveredIp = "8.8.8.8", Expected = new string[0] } };
            foreach (var test in cases)
            {
                // Confirmed, failed twice, then recovered.
                int calls = 0, seen = 0;
                var lookup = new DelegateLookup { Handler = token => {
                    int call = Interlocked.Increment(ref calls);
                    if (call == 2 || call == 3) return Task.FromResult(Measured(DateTime.UtcNow, ConnectivityHealth.Unreachable, false, false));
                    Snapshot result = Measured(DateTime.UtcNow, ConnectivityHealth.Reachable);
                    if (call >= 4) result.Ip = test.RecoveredIp;
                    return Task.FromResult(result);
                } };
                var voice = new RecordingVoice();
                var context = new TrayContext(lookup, new Settings { PollSeconds = 60, NotifyOnChange = false,
                    VoiceMode = test.Mode, AnnounceConnection = test.Alerts }, true, null, voice);
                var watch = Stopwatch.StartNew();
                using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
                {
                    timer.Tick += (s, e) => {
                        if (context.Current != null && seen < calls) { seen = calls; if (seen < 4) context.RequestRefresh(); }
                        if (seen >= 4 || watch.ElapsedMilliseconds > 3000) { timer.Stop(); context.ExitThread(); }
                    };
                    timer.Start(); Application.Run(context);
                }
                context.Dispose();
                Check("Real tray in voice mode " + test.Mode + (test.Alerts ? "" : " without connection alerts") +
                    " recovering to " + test.RecoveredIp + " speaks " + test.Expected.Length + " announcements",
                    seen == 4 && String.Join("|", voice.Spoken) == String.Join("|", test.Expected));
            }
        }

        private static void TrayVoiceTest()
        {
            foreach (VoiceMode mode in new[] { VoiceMode.IpOrCountry, VoiceMode.CountryOnly, VoiceMode.Off })
            {
                Snapshot[] steps = {
                    new Snapshot { Ip = "8.8.8.8", CountryCode = "SG", Country = "新加坡" },
                    new Snapshot { Ip = "1.1.1.1", CountryCode = "SG", Country = "新加坡" },
                    new Snapshot { Ip = "1.1.1.1", CountryCode = "JP", Country = "日本" } };
                int calls = 0, seen = 0;
                var lookup = new DelegateLookup { Handler = token => {
                    Snapshot step = steps[Math.Min(Interlocked.Increment(ref calls), steps.Length) - 1];
                    return Task.FromResult(new Snapshot { Ip = step.Ip, CountryCode = step.CountryCode, Country = step.Country, CheckedUtc = DateTime.UtcNow });
                } };
                var voice = new RecordingVoice();
                var context = new TrayContext(lookup, new Settings { PollSeconds = 60, NotifyOnChange = false, VoiceMode = mode }, true, null, voice);
                var watch = Stopwatch.StartNew();
                using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
                {
                    timer.Tick += (s, e) => {
                        // Query the next step as soon as one is confirmed instead of waiting for polling.
                        if (context.Current != null && context.Current.HasCountry && seen < calls)
                        { seen = calls; if (seen < steps.Length) context.RequestRefresh(); }
                        if (seen == steps.Length || watch.ElapsedMilliseconds > 3000) { timer.Stop(); context.ExitThread(); }
                    };
                    timer.Start(); Application.Run(context);
                }
                context.Dispose();
                string ipChange = "公网 IP 已变更为一点一点一点一，出口仍在新加坡。";
                string countryChange = "网络出口已从新加坡切换到日本，新的 IP 是一点一点一点一。";
                string[] expected = mode == VoiceMode.IpOrCountry ? new[] { ipChange, countryChange } :
                    mode == VoiceMode.CountryOnly ? new[] { countryChange } : new string[0];
                Check("Real tray in voice mode " + mode + " speaks " + expected.Length + " of 2 changes",
                    seen == steps.Length && String.Join("|", voice.Spoken) == String.Join("|", expected) && voice.Disposed);
            }
        }
    }
}
