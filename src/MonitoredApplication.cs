using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IPCountryWatcher
{
    public sealed class MonitoredApplication
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ExecutablePath { get; set; }
        public bool Enabled { get; set; }
        public bool UseSystemProxy { get; set; }

        public MonitoredApplication()
        { Id = Guid.NewGuid().ToString("N"); Name = ""; ExecutablePath = ""; Enabled = false; }

        internal static List<MonitoredApplication> Defaults()
        {
            return new[] { "爱奇艺", "腾讯视频", "优酷视频", "QQ音乐", "哔哩哔哩", "微信 WeChat", "ChatGPT", "Claude", "Grok Bot" }
                .Select(name => new MonitoredApplication { Name = name }).ToList();
        }

        internal MonitoredApplication Copy()
        {
            return new MonitoredApplication { Id = Id, Name = Name, ExecutablePath = ExecutablePath,
                Enabled = Enabled, UseSystemProxy = UseSystemProxy };
        }

        internal static void Validate(List<MonitoredApplication> apps)
        {
            if (apps == null || apps.Count > 64) throw new ArgumentException("最多监控 64 个应用。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in apps)
            {
                if (app == null || String.IsNullOrWhiteSpace(app.Name) || app.Name.Length > 80)
                    throw new ArgumentException("请输入不超过 80 字的应用名称。");
                if (String.IsNullOrEmpty(app.Id) || !ids.Add(app.Id)) throw new ArgumentException("应用标识重复。");
                string file = app.ExecutablePath ?? "";
                if (file.Length != 0)
                {
                    if (file.Length > 1024 || !Path.IsPathRooted(file) || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        file.IndexOf('"') >= 0 || file.IndexOf('\r') >= 0 || file.IndexOf('\n') >= 0)
                        throw new ArgumentException("请选择有效的 EXE 绝对路径。");
                    app.ExecutablePath = Path.GetFullPath(file);
                    if (!paths.Add(app.ExecutablePath)) throw new ArgumentException("同一 EXE 已在监控列表中。");
                }
                else if (app.Enabled) throw new ArgumentException("启用探针前请先选择应用 EXE。");
            }
        }
    }
}
