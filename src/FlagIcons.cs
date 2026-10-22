using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace IPCountryWatcher
{
    internal enum ConnectionBadge { None, Pending, Limited, Unconfirmed }

    internal static class FlagIcons
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Create(string countryCode, int size)
        { return Create(countryCode, size, ConnectionBadge.None); }

        public static Icon Create(string countryCode, int size, ConnectionBadge badge)
        {
            using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                bool drawn = false;
                if (!String.IsNullOrEmpty(countryCode))
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Flags." + countryCode.ToLowerInvariant() + ".png"))
                    {
                        if (stream != null)
                        {
                            using (var flag = Image.FromStream(stream))
                            {
                                float scale = Math.Min((float)size / flag.Width, (float)size / flag.Height);
                                float w = flag.Width * scale, h = flag.Height * scale;
                                g.DrawImage(flag, (size - w) / 2, (size - h) / 2, w, h);
                                drawn = true;
                            }
                        }
                    }
                }
                if (!drawn)
                {
                    using (var brush = new SolidBrush(Color.FromArgb(75, 90, 108)))
                        g.FillEllipse(brush, 0, 0, size - 1, size - 1);
                    using (var pen = new Pen(Color.White, Math.Max(1, size / 20f)))
                    {
                        if (String.IsNullOrEmpty(countryCode))
                        {
                            g.DrawEllipse(pen, size * .13f, size * .13f, size * .74f, size * .74f);
                            g.DrawEllipse(pen, size * .33f, size * .13f, size * .34f, size * .74f);
                            g.DrawLine(pen, size * .13f, size * .5f, size * .87f, size * .5f);
                        }
                        else
                        {
                            using (var font = new Font("Segoe UI", size * .34f, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                                g.DrawString(countryCode, font, Brushes.White, new RectangleF(0, 0, size, size), format);
                        }
                    }
                }
                if (badge != ConnectionBadge.None)
                {
                    // A shape as well as a color keeps stale/failed results recognizable at 16px.
                    float diameter = Math.Max(9, size * .58f);
                    float x = size - diameter, y = size - diameter;
                    using (var fill = new SolidBrush(badge == ConnectionBadge.Unconfirmed ? Color.FromArgb(196, 42, 48) : Color.FromArgb(255, 194, 49)))
                    using (var border = new Pen(Color.White, Math.Max(1, size / 24f)))
                    {
                        g.FillEllipse(fill, x, y, diameter - 1, diameter - 1);
                        g.DrawEllipse(border, x, y, diameter - 1, diameter - 1);
                    }
                    Color ink = badge == ConnectionBadge.Unconfirmed ? Color.White : Color.FromArgb(50, 42, 24);
                    using (var pen = new Pen(ink, Math.Max(1.3f, size / 12f)))
                    using (var dot = new SolidBrush(ink))
                    {
                        float cx = x + diameter * .45f;
                        if (badge == ConnectionBadge.Unconfirmed)
                        {
                            g.DrawLine(pen, x + diameter * .3f, y + diameter * .3f, x + diameter * .65f, y + diameter * .65f);
                            g.DrawLine(pen, x + diameter * .65f, y + diameter * .3f, x + diameter * .3f, y + diameter * .65f);
                        }
                        else
                        {
                            g.DrawLine(pen, cx, y + diameter * .23f, cx, y + diameter * .48f);
                            g.FillEllipse(dot, cx - .7f, y + diameter * .65f, 1.5f, 1.5f);
                        }
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon native = Icon.FromHandle(handle)) return (Icon)native.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
    }
}
