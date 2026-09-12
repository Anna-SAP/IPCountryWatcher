using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IPCountryWatcher
{
    internal sealed class ApplicationSettingsForm : Form
    {
        private readonly DataGridView grid = new DataGridView();
        private readonly List<MonitoredApplication> entries;
        private readonly ComboBox interval = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        internal List<MonitoredApplication> Applications { get { return entries; } }
        internal int PollSeconds { get { return (int)interval.SelectedItem; } }

        internal ApplicationSettingsForm(Settings settings)
        {
            SuspendLayout();
            Text = "监控应用设置";
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleDimensions = new SizeF(96f, 96f); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1060, 610); MinimumSize = new Size(860, 510);
            StartPosition = FormStartPosition.CenterParent;
            entries = settings.MonitoredApplications.Select(a => a.Copy()).ToList();
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(16) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var description = new Label { AutoSize = true, MaximumSize = new Size(990, 0), Margin = new Padding(0, 0, 0, 12),
                Text = "按完整 EXE 路径监控，可随时增加或删除。勾选“启用探针”后会向匹配进程加载本地 DLL 并发起 IP 查询。\n直连模式遵循该进程的 VPN 分流；系统代理模式使用 Windows 代理。应用私有代理和不同网站的出口可能不同。" };
            grid.Dock = DockStyle.Fill; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
            grid.AutoGenerateColumns = false; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.FixedSingle;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "enabled", HeaderText = "启用探针", Width = 85 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "应用名称", Width = 150 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "path", HeaderText = "EXE 路径", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "proxy", HeaderText = "系统代理", Width = 85 });
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (s, e) => SyncRows();
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 4) };
            AddButton(actions, "增加应用", () => { SyncRows(); entries.Add(new MonitoredApplication { Name = "新应用" }); Reload(entries.Count - 1); });
            AddButton(actions, "删除所选", () => { int index = SelectedIndex; if (index >= 0) { SyncRows(); entries.RemoveAt(index); Reload(Math.Min(index, entries.Count - 1)); } });
            AddButton(actions, "选择 EXE…", Browse);
            AddButton(actions, "从运行进程选择…", ChooseRunning);
            AddButton(actions, "匹配已运行的预置应用", MatchRunning);
            var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
            footer.Controls.Add(new Label { Text = "探测间隔", AutoSize = true, Margin = new Padding(0, 9, 8, 0) });
            interval.Items.AddRange(new object[] { 15, 30, 60 }); interval.SelectedItem = settings.ProcessPollSeconds;
            footer.Controls.Add(interval);
            footer.Controls.Add(new Label { Text = "秒（后台持续运行；同一时刻最多 2 个探针）", AutoSize = true, Margin = new Padding(8, 9, 30, 0) });
            AddButton(footer, "保存", () => {
                try { grid.EndEdit(); SyncRows(); MonitoredApplication.Validate(entries); DialogResult = DialogResult.OK; Close(); }
                catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "请检查设置", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            });
            var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
            footer.Controls.Add(cancel); CancelButton = cancel;
            layout.Controls.Add(description, 0, 0); layout.Controls.Add(grid, 0, 1); layout.Controls.Add(actions, 0, 2); layout.Controls.Add(footer, 0, 3);
            Controls.Add(layout); Reload(0); Shown += (s, e) => WindowLayout.Fit(this, grid); ResumeLayout(true);
        }

        private static void AddButton(Control parent, string text, Action action)
        { var button = new Button { Text = text, AutoSize = true, Padding = new Padding(5, 2, 5, 2) }; button.Click += (s, e) => action(); parent.Controls.Add(button); }

        private int SelectedIndex { get { return grid.CurrentRow == null ? -1 : grid.CurrentRow.Index; } }
        private bool reloading;
        private void Reload(int selected)
        {
            reloading = true;
            grid.Rows.Clear();
            foreach (var entry in entries) grid.Rows.Add(entry.Enabled, entry.Name, entry.ExecutablePath, entry.UseSystemProxy);
            if (selected >= 0 && selected < grid.Rows.Count) grid.CurrentCell = grid.Rows[selected].Cells[1];
            reloading = false;
        }
        private void SyncRows()
        {
            if (reloading || grid.Rows.Count != entries.Count) return;
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].Enabled = Convert.ToBoolean(grid.Rows[i].Cells[0].Value);
                entries[i].Name = Convert.ToString(grid.Rows[i].Cells[1].Value);
                entries[i].UseSystemProxy = Convert.ToBoolean(grid.Rows[i].Cells[3].Value);
            }
        }
        private void Browse()
        {
            int index = SelectedIndex; if (index < 0) return;
            using (var picker = new OpenFileDialog { Filter = "应用程序 (*.exe)|*.exe", CheckFileExists = true, Title = "选择需要监控的应用 EXE" })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                SyncRows(); entries[index].ExecutablePath = picker.FileName; Reload(index);
            }
        }
        private async void ChooseRunning()
        {
            int index = SelectedIndex; if (index < 0) return;
            try
            {
                var apps = await Task.Run(() => ProcessNetwork.Applications());
                if (IsDisposed) return;
                using (var picker = new RunningApplicationForm(apps))
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    SyncRows(); entries[index].ExecutablePath = picker.Selected.Path;
                    if (entries[index].Name == "新应用") entries[index].Name = picker.Selected.Name;
                    Reload(index);
                }
            }
            catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "读取进程失败"); }
        }
        private async void MatchRunning()
        {
            try
            {
                var apps = await Task.Run(() => ProcessNetwork.Applications());
                if (IsDisposed) return;
                SyncRows();
                string[][] names = { new[] { "QyClient", "QiyiClient" }, new[] { "QQLive", "QQVideo" }, new[] { "Youku" },
                    new[] { "QQMusic" }, new[] { "bilibili" }, new[] { "Weixin", "WeChat" }, new[] { "ChatGPT" },
                    new[] { "Claude" }, new[] { "GrokBot", "Grok Bot", "grok-bot" } };
                var defaults = MonitoredApplication.Defaults();
                int found = 0;
                foreach (var entry in entries.Where(a => String.IsNullOrEmpty(a.ExecutablePath)))
                {
                    int index = defaults.FindIndex(a => a.Name == entry.Name);
                    if (index < 0) continue;
                    var app = apps.FirstOrDefault(a => !String.IsNullOrEmpty(a.Path) &&
                        names[index].Contains(a.Name, StringComparer.OrdinalIgnoreCase) &&
                        a.Path.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) < 0 &&
                        !entries.Any(existing => String.Equals(existing.ExecutablePath, a.Path, StringComparison.OrdinalIgnoreCase)));
                    if (app != null) { entry.ExecutablePath = app.Path; found++; }
                }
                Reload(SelectedIndex);
                MessageBox.Show(this, "已匹配 " + found + " 个应用。检查路径后，勾选需要启用的探针并保存。\n其余应用可先启动再匹配，或手动选择 EXE。", "匹配完成");
            }
            catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "读取进程失败"); }
        }
    }

    internal sealed class RunningApplicationForm : Form
    {
        internal ProcessApplication Selected { get; private set; }
        internal RunningApplicationForm(List<ProcessApplication> apps)
        {
            SuspendLayout();
            Text = "选择正在运行的应用"; Font = new Font("Microsoft YaHei UI", 9f); AutoScaleDimensions = new SizeF(96f, 96f); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(970, 480); MinimumSize = new Size(700, 350); StartPosition = FormStartPosition.CenterParent;
            var search = new TextBox { Dock = DockStyle.Top };
            var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false,
                MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White, AutoGenerateColumns = false };
            grid.Columns.Add("name", "应用"); grid.Columns[0].Width = 160;
            grid.Columns.Add("pids", "PID"); grid.Columns[1].Width = 130;
            grid.Columns.Add("path", "完整路径"); grid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            Action populate = () => {
                grid.Rows.Clear();
                foreach (var app in apps.Where(a => !String.IsNullOrEmpty(a.Path) &&
                    (a.Name + " " + a.Path).IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    int index = grid.Rows.Add(app.Name, String.Join(", ", app.Pids), app.Path);
                    grid.Rows[index].Tag = app;
                }
            };
            Action choose = () => { if (grid.CurrentRow == null) return; Selected = (ProcessApplication)grid.CurrentRow.Tag; DialogResult = DialogResult.OK; Close(); };
            var button = new Button { Text = "选择此应用", Dock = DockStyle.Bottom, Height = 38 };
            button.Click += (s, e) => choose(); grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) choose(); };
            search.TextChanged += (s, e) => populate();
            Controls.Add(grid); Controls.Add(search); Controls.Add(button); populate(); Shown += (s, e) => WindowLayout.Fit(this, grid); ResumeLayout(true);
        }
    }
}
