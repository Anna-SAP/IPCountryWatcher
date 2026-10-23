using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;

namespace IPCountryWatcher
{
    internal interface IVoice : IDisposable
    {
        // Null when no Simplified Chinese female voice is installed; Speak is then ignored.
        string Name { get; }
        // Replaces an announcement still playing, so a burst of changes ends on the latest state.
        void Speak(string text, TimeSpan delay);
    }

    internal sealed class VoiceCandidate
    {
        public string Name;
        public string Culture;
        public bool Female;
    }

    internal static class VoiceSelection
    {
        // Gentle young female voices first: the Xiaoxiao / Xiaoyi natural voices when a local SAPI adapter
        // provides them, then Yaoyao, then Huihui. Online voices would send the spoken IP to a cloud service.
        private static readonly string[] Preferred = { "Xiaoxiao", "Xiaoyi", "Yaoyao", "Huihui" };

        internal static string Choose(IEnumerable<VoiceCandidate> voices)
        {
            string best = null;
            int bestRank = int.MaxValue;
            foreach (var voice in voices)
            {
                if (!voice.Female || !String.Equals(voice.Culture, "zh-CN", StringComparison.OrdinalIgnoreCase) ||
                    voice.Name.IndexOf("Online", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                int rank = Preferred.Length;
                for (int i = 0; i < Preferred.Length; i++)
                    if (voice.Name.IndexOf(Preferred[i], StringComparison.OrdinalIgnoreCase) >= 0) { rank = i; break; }
                if (rank < bestRank) { best = voice.Name; bestRank = rank; }
            }
            return best;
        }
    }

    internal static class VoiceText
    {
        private const string Digits = "零一二三四五六七八九";

        // Chinese digits are read one by one, as people read addresses aloud; Arabic numerals would be read
        // as quantities. An IPv6 address is too long to be useful spoken.
        internal static string SpokenIp(string ip)
        {
            IPAddress address;
            if (!IPAddress.TryParse(ip ?? "", out address) || address.AddressFamily != AddressFamily.InterNetwork) return null;
            var text = new StringBuilder();
            foreach (char c in address.ToString()) text.Append(c == '.' ? '点' : Digits[c - '0']);
            return text.ToString();
        }

        internal static string Change(EgressChange change)
        {
            string ip = SpokenIp(change.Current.Ip);
            if (change.CountryChanged)
                return "网络出口已从" + change.Previous.Country + "切换到" + change.Current.Country + "，" +
                    (ip == null ? "现在使用 IPv6 地址。" : "新的 IP 是" + ip + "。");
            return (ip == null ? "公网 IP 已变更为新的 IPv6 地址" : "公网 IP 已变更为" + ip) + "，出口仍在" + change.Current.Country + "。";
        }

        internal static string Current(Snapshot current)
        {
            if (current == null || !current.HasCountry) return "正在确认网络出口，请稍候。";
            string ip = SpokenIp(current.Ip);
            return "当前网络出口在" + current.Country + "，" + (ip == null ? "使用 IPv6 地址。" : "公网 IP 是" + ip + "。");
        }

        // Says what the tray badge shows; a failed check is not proof that the whole PC is offline.
        internal static string Connection(ConnectionAlert alert, Snapshot current)
        {
            if (alert == ConnectionAlert.Failed) return "网络连接异常，检测请求均未成功，正在等待重试。";
            if (alert == ConnectionAlert.Unconfirmed) return "网络连接待确认，暂时无法获取公网 IP，正在等待重试。";
            return current != null && current.HasCountry ? "网络连接已恢复，出口在" + current.Country + "。" : "网络连接已恢复。";
        }
    }

    // Speaks through SAPI with the voices installed in Windows, offline; nothing is sent to a speech service.
    // SAPI is used directly because the .NET Framework System.Speech wrapper lists only the legacy desktop
    // voices, not Yaoyao and the other OneCore voices that Windows 10/11 installs with Chinese speech.
    internal sealed class ChineseVoice : IVoice
    {
        private static readonly string[] Categories = {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices" };
        internal const int SpeakAsync = 1, PurgeBeforeSpeak = 2, IsXml = 8;
        // SVPOver. A normal-priority SAPI voice waits until every other normal SAPI voice on the audio device,
        // in any process, has finished, and an alert-priority voice interrupts them. Over priority mixes with
        // them instead; the stream is shared-mode like music and video, never exclusive.
        internal const int OverPriority = 2;
        private readonly dynamic voice;
        public string Name { get; private set; }

        internal ChineseVoice()
        {
            try
            {
                string name;
                voice = Create(out name);
                Name = name;
            }
            catch (Exception ex)
            {
                if (!Expected(ex)) throw;
                voice = null;
                Name = null;
            }
        }

        // Returns an SpVoice using the preferred voice with a slightly slower, softer delivery, or null.
        internal static dynamic Create(out string name)
        {
            var tokens = new Dictionary<string, object>();
            var candidates = new List<VoiceCandidate>();
            foreach (string id in Categories)
            {
                dynamic list;
                try
                {
                    dynamic category = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpObjectTokenCategory", true));
                    category.SetId(id, false);
                    list = category.EnumerateTokens();
                }
                catch (COMException) { continue; } // Older Windows has no OneCore voice category.
                for (int i = 0; i < list.Count; i++)
                {
                    dynamic token = list.Item(i);
                    string voiceName = token.GetAttribute("Name");
                    if (String.IsNullOrEmpty(voiceName) || tokens.ContainsKey(voiceName)) continue;
                    tokens[voiceName] = token;
                    candidates.Add(new VoiceCandidate { Name = voiceName,
                        Culture = Culture((string)token.GetAttribute("Language")), Female = (string)token.GetAttribute("Gender") == "Female" });
                }
            }
            name = VoiceSelection.Choose(candidates);
            if (name == null) return null;
            dynamic voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice", true));
            voice.Voice = tokens[name];
            voice.Rate = -1;
            voice.Volume = 90;
            voice.Priority = OverPriority;
            return voice;
        }

        // SAPI lists languages as hexadecimal LCIDs, for example "804" for zh-CN.
        private static string Culture(string languages)
        {
            int lcid;
            string first = (languages ?? "").Split(';')[0];
            if (!Int32.TryParse(first, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out lcid)) return "";
            try { return CultureInfo.GetCultureInfo(lcid).Name; }
            catch (CultureNotFoundException) { return ""; }
        }

        internal static string Markup(string text, TimeSpan delay)
        {
            string escaped = SecurityElement.Escape(text);
            return delay > TimeSpan.Zero ? "<silence msec=\"" + (int)delay.TotalMilliseconds + "\"/>" + escaped : escaped;
        }

        public void Speak(string text, TimeSpan delay)
        {
            if (voice == null) return;
            // Announcements are a side channel: an audio failure must never disturb monitoring.
            try { voice.Speak(Markup(text, delay), SpeakAsync | PurgeBeforeSpeak | IsXml); }
            catch (Exception ex) { if (!Expected(ex)) throw; }
        }

        private static bool Expected(Exception ex)
        {
            return ex is ExternalException || ex is InvalidOperationException || ex is ArgumentException ||
                ex is InvalidCastException || ex is UnauthorizedAccessException || ex is RuntimeBinderException;
        }

        public void Dispose()
        {
            if (voice == null) return;
            try { voice.Speak("", SpeakAsync | PurgeBeforeSpeak); }
            catch (Exception ex) { if (!Expected(ex)) throw; }
            Marshal.FinalReleaseComObject((object)voice);
        }
    }
}
