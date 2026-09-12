using System;
using System.Drawing;
using System.Windows.Forms;

namespace IPCountryWatcher
{
    internal static class WindowLayout
    {
        internal static void Fit(Form form, DataGridView grid)
        {
            // WinForms scales controls, but DataGridView column widths are not controls.
            float factor;
            using (var graphics = form.CreateGraphics()) factor = graphics.DpiX / 96f;
            foreach (DataGridViewColumn column in grid.Columns)
            {
                int width = column.Width;
                column.MinimumWidth = Math.Max(5, (int)Math.Round(column.MinimumWidth * factor));
                if (column.AutoSizeMode != DataGridViewAutoSizeColumnMode.Fill)
                    column.Width = Math.Max(column.MinimumWidth, (int)Math.Round(width * factor));
            }
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            var area = Screen.FromControl(form).WorkingArea;
            form.Size = new Size(Math.Min(form.Width, area.Width - 32), Math.Min(form.Height, area.Height - 32));
            form.Location = new Point(area.Left + (area.Width - form.Width) / 2, area.Top + (area.Height - form.Height) / 2);
        }
    }
}
