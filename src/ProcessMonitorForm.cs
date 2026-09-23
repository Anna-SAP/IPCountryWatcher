using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace IPCountryWatcher
{
    internal sealed class ProcessMonitorForm : Form
    {
        private readonly ProcessMonitor monitor;
        private readonly Func<Snapshot> current;
        private readonly DataGridView grid = new DataGridView();
        private readonly Label summary = new Label();
        private readonly TextBox details = new TextBox();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        private bool rendering;
        private sealed class DisplayRow
        {
            internal ProcessMonitorRow Row;
            internal Snapshot Address;
            internal string Key;
            internal bool Fresh;
        }

        internal ProcessMonitorForm(ProcessMonitor monitor, Func<Snapshot> current, Action settings, Action refresh, bool testing)
        {
            SuspendLayout();
            this.monitor = monitor; this.current = current;
            Text = "进程 IP 监控 · IP 国旗监视器"; Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleDimensions = new SizeF(96f, 96f); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(1180, 650); MinimumSize = new Size(900, 500);
            StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var heading = new Label { Text = "各应用的公网出口", Font = new Font(Font.FontFamily, 16f, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
            summary.AutoSize = true; summary.MaximumSize = new Size(1120, 0); summary.Margin = new Padding(0, 0, 0, 12);
            grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.RowHeadersVisible = false;
            grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.FixedSingle; grid.AutoGenerateColumns = false; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            AddColumn("应用 / 进程", 125); AddColumn("PID", 65); AddColumn("地址族", 65);
            AddColumn("公网 IP", 205); grid.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; grid.Columns[3].MinimumWidth = 170;
            AddColumn("国家 / 地区", 115); AddColumn("探针模式", 100); AddColumn("状态", 210); AddColumn("检查时间", 85);
            grid.SelectionChanged += (s, e) => { if (!rendering) ShowDetails(); };
            details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Vertical; details.Dock = DockStyle.Fill;
            details.BackColor = SystemColors.Control; details.BorderStyle = BorderStyle.None; details.Margin = new Padding(0, 12, 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
            Button(actions, "应用设置…", settings);
            Button(actions, "立即重新探测", refresh);
            Button(actions, "复制所选公网 IP", () => {
                var row = Selected;
                if (row == null || !row.Fresh || row.Address == null || !row.Address.HasIp) return;
                try { Clipboard.SetText(row.Address.Ip); }
                catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "剪贴板正忙，请重试。"); }
            });
            actions.Controls.Add(new Label { Text = "关闭窗口后仍在托盘中监控；可在应用设置中停用探针。", AutoSize = true, Margin = new Padding(14, 10, 0, 0) });
            layout.Controls.Add(heading, 0, 0); layout.Controls.Add(summary, 0, 1); layout.Controls.Add(grid, 0, 2);
            layout.Controls.Add(details, 0, 3); layout.Controls.Add(actions, 0, 4); Controls.Add(layout);
            timer.Tick += (s, e) => RenderRows(); if (!testing) timer.Start();

            Shown += (s, e) => WindowLayout.Fit(this, grid);
            ResumeLayout(true);
            RenderRows();
        }
        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
        private void AddColumn(string title, int width)
        { grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable }); }
        private static void Button(Control parent, string text, Action callback)
        { var button = new Button { Text = text, AutoSize = true, Padding = new Padding(4) }; button.Click += (s, e) => callback(); parent.Controls.Add(button); }
        private DisplayRow Selected { get { return grid.CurrentRow == null ? null : grid.CurrentRow.Tag as DisplayRow; } }

        internal void RenderRows()
        {
            string selected = Selected == null ? null : Selected.Key;
            int first = grid.FirstDisplayedScrollingRowIndex;
            rendering = true;
            grid.Rows.Clear();
            DateTime now = DateTime.UtcNow;
            foreach (var row in monitor.Rows)
            {
                if (row.Identity == null || !row.Application.Enabled || row.Blocked)
                { AddRow(row, null, "—", row.Status, false); continue; }
                bool alive = row.Identity.IsAlive();
                bool fresh = alive && row.IsFresh(now, monitor.Generation);
                string status = !alive ? "进程已退出，待刷新列表" : row.Generation != monitor.Generation ? "网络或设置已变化，待重新确认" :
                    row.CheckedUtc != DateTime.MinValue && !fresh ? "结果已过期，待重新确认" : row.Status;
                AddRow(row, fresh ? row.V4 : null, "IPv4", status, fresh);
                AddRow(row, fresh ? row.V6 : null, "IPv6", status, fresh);
            }
            foreach (DataGridViewRow row in grid.Rows)
            {
                var display = (DisplayRow)row.Tag;
                if (display.Key == selected) { grid.CurrentCell = row.Cells[0]; break; }
            }
            if (first >= 0 && first < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = first;
            var own = current();
            summary.Text = (own != null && own.HasIp ? "本程序出口：" + own.Ip + "    " + (own.Country ?? "国家待识别") : "本程序出口：待确认") +
                "\n下表为所选进程访问 IP 测试服务时的实测；同一应用访问不同网站时可能走不同出口。" +
                (monitor.Error == null ? "" : "\n" + monitor.Error);
            rendering = false; ShowDetails();
        }
        private void AddRow(ProcessMonitorRow row, Snapshot address, string family, string status, bool fresh)
        {
            string country = address == null ? "—" : address.HasCountry ? address.Country + " (" + address.CountryCode + ")" : "国家暂未识别";
            if (address != null && address.CountryIsStale) country += " · 缓存待更新";
            string familyError = family == "IPv4" ? row.Error4 : row.Error6;
            if (fresh && address == null && !String.IsNullOrEmpty(familyError)) status = familyError;
            int index = grid.Rows.Add(row.Application.Name, row.Identity == null ? "—" : row.Identity.Pid.ToString(), family,
                address == null ? "—" : address.Ip, country, row.Application.UseSystemProxy ? "系统代理" : "直连（含 VPN）",
                status, row.CheckedUtc == DateTime.MinValue ? "—" : row.CheckedUtc.ToLocalTime().ToString("HH:mm:ss"));
            grid.Rows[index].Tag = new DisplayRow { Row = row, Address = address, Fresh = fresh,
                Key = (row.Key ?? row.Application.Id) + family };
            grid.Rows[index].DefaultCellStyle.ForeColor = address != null && fresh ? Color.FromArgb(20, 100, 70) : Color.FromArgb(100, 105, 115);
        }
        private void ShowDetails()
        {
            var selected = Selected;
            if (selected == null) { details.Text = "打开“应用设置”绑定目标 EXE，并启用需要监控的应用。"; return; }
            var row = selected.Row;
            details.Text = "EXE：" + (String.IsNullOrEmpty(row.Application.ExecutablePath) ? "尚未选择" : row.Application.ExecutablePath) +
                "\r\n本机连接地址（仅作连接线索，不代表公网出口）：" + (String.IsNullOrEmpty(row.LocalAddresses) ? "暂无" : row.LocalAddresses) +
                "\r\n来源：" + (selected.Address == null ? "尚无有效公网结果" : selected.Address.Source ?? "进程内 HTTPS 探针") +
                "；同一 EXE 的已建立 TCP 连接数：" + row.TcpCount +
                "\r\n" + (selected.Address != null && selected.Address.Error != null ? selected.Address.Error :
                    row.Blocked ? row.Status + "。本次进程生命周期内停止自动重试。" :
                    "结果对应当前 PID 和所选探针模式；不读取应用私有代理设置，也不代表所有目标网站的流量。");
        }
    }
}
