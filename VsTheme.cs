using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BetterWebLauncher
{
    /// <summary>
    /// VS Code Dark+ 主题：调色板、字体与圆角绘制工具。
    /// 颜色取自 VS Code 默认深色主题（Dark Modern）的官方配色。
    /// </summary>
    internal static class VsTheme
    {
        // ---------------- 背景 ----------------
        public static readonly Color EditorBg      = Color.FromArgb(0x1E, 0x1E, 0x1E); // 编辑器背景
        public static readonly Color SidebarBg     = Color.FromArgb(0x25, 0x25, 0x26); // 侧边栏 / 卡片
        public static readonly Color CardBg        = Color.FromArgb(0x25, 0x25, 0x26);
        public static readonly Color CardBgAlt     = Color.FromArgb(0x2D, 0x2D, 0x30);
        public static readonly Color TitleBarBg    = Color.FromArgb(0x32, 0x32, 0x33); // 标题栏
        public static readonly Color LogBg         = Color.FromArgb(0x18, 0x18, 0x18); // 输出面板
        public static readonly Color InputBg       = Color.FromArgb(0x3C, 0x3C, 0x3C); // 输入框
        public static readonly Color InputBgHover  = Color.FromArgb(0x4A, 0x4A, 0x4A);

        // ---------------- 边框 ----------------
        public static readonly Color Border        = Color.FromArgb(0x3C, 0x3C, 0x3C);
        public static readonly Color BorderSubtle  = Color.FromArgb(0x2D, 0x2D, 0x2D);
        public static readonly Color ControlBorder = Color.FromArgb(0x6B, 0x6B, 0x6B);

        // ---------------- 文字 ----------------
        public static readonly Color TextPrimary   = Color.FromArgb(0xD4, 0xD4, 0xD4);
        public static readonly Color TextBright    = Color.FromArgb(0xFF, 0xFF, 0xFF);
        public static readonly Color TextMuted     = Color.FromArgb(0x9D, 0x9D, 0x9D);
        public static readonly Color TextDisabled  = Color.FromArgb(0x6B, 0x6B, 0x6B);

        // ---------------- 强调色 / 按钮 ----------------
        public static readonly Color Accent        = Color.FromArgb(0x00, 0x7A, 0xCC); // 状态栏蓝
        public static readonly Color AccentHover   = Color.FromArgb(0x11, 0x77, 0xBB);
        public static readonly Color FocusBorder   = Color.FromArgb(0x00, 0x7F, 0xD4); // 输入框聚焦边框
        public static readonly Color ButtonBg      = Color.FromArgb(0x0E, 0x63, 0x9C); // 主按钮
        public static readonly Color ButtonHover   = Color.FromArgb(0x11, 0x77, 0xBB);
        public static readonly Color ButtonPressed = Color.FromArgb(0x0A, 0x4E, 0x7D);
        public static readonly Color SecondaryBg   = Color.FromArgb(0x3A, 0x3D, 0x41); // 次要按钮
        public static readonly Color SecondaryHover= Color.FromArgb(0x45, 0x49, 0x4E);
        public static readonly Color SecondaryDown = Color.FromArgb(0x2A, 0x2D, 0x30);
        public static readonly Color DangerBg      = Color.FromArgb(0x6E, 0x24, 0x24); // 危险按钮
        public static readonly Color DangerHover   = Color.FromArgb(0x8A, 0x2E, 0x2E);
        public static readonly Color DangerDown    = Color.FromArgb(0x54, 0x1B, 0x1B);
        public static readonly Color DangerText    = Color.FromArgb(0xF4, 0x87, 0x71);

        // ---------------- 语义色（VS Code 语法高亮色）----------------
        public static readonly Color Ok            = Color.FromArgb(0x89, 0xD1, 0x85); // 绿
        public static readonly Color Warn          = Color.FromArgb(0xDC, 0xDC, 0xAA); // 黄
        public static readonly Color Error         = Color.FromArgb(0xF4, 0x87, 0x71); // 红
        public static readonly Color Info          = Color.FromArgb(0x56, 0x9C, 0xD6); // 蓝
        public static readonly Color Teal          = Color.FromArgb(0x4E, 0xC9, 0xB0); // 青
        public static readonly Color StringColor   = Color.FromArgb(0xCE, 0x91, 0x78); // 橙
        public static readonly Color Comment       = Color.FromArgb(0x6A, 0x99, 0x55); // 注释绿
        public static readonly Color Purple        = Color.FromArgb(0xC5, 0x86, 0xC0); // 紫
        public static readonly Color LogText       = Color.FromArgb(0xCC, 0xCC, 0xCC);

        // ---------------- 字体 ----------------
        private const string UiFamily = "Microsoft YaHei UI";
        private static string? _monoFamily;

        public static Font Ui(float size = 9.75F, FontStyle style = FontStyle.Regular)
            => new(UiFamily, size, style, GraphicsUnit.Point);

        public static Font Mono(float size = 9.75F, FontStyle style = FontStyle.Regular)
            => new(MonoFamily, size, style, GraphicsUnit.Point);

        public static string MonoFamily
        {
            get
            {
                if (_monoFamily == null)
                {
                    foreach (var name in new[] { "Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console" })
                    {
                        try
                        {
                            using var ff = new FontFamily(name);
                            _monoFamily = name;
                            break;
                        }
                        catch { /* 该字体不存在，继续尝试 */ }
                    }
                    _monoFamily ??= FontFamily.GenericMonospace.Name;
                }
                return _monoFamily;
            }
        }

        // ---------------- 绘制工具 ----------------

        /// <summary>生成圆角矩形路径。</summary>
        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0 || r.Width <= 0 || r.Height <= 0)
            {
                path.AddRectangle(r);
                return path;
            }

            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// 用 GDI 填充矩形。
        ///
        /// 这里刻意不用 GDI+：GDI 只写 RGB、不动 alpha 通道，
        /// 于是窗体的毛玻璃（亚克力）效果不会被不透明像素盖住，
        /// 圆角控件四角才能和背景自然嵌合。
        /// </summary>
        public static void FillRectGdi(Graphics g, Rectangle rect, Color color)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;

            IntPtr hdc = g.GetHdc();
            IntPtr brush = IntPtr.Zero;
            try
            {
                // COLORREF = 0x00BBGGRR
                int colorRef = color.R | (color.G << 8) | (color.B << 16);
                brush = CreateSolidBrush(colorRef);
                var r = new NativeRect { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
                FillRect(hdc, ref r, brush);
            }
            finally
            {
                if (brush != IntPtr.Zero) DeleteObject(brush);
                g.ReleaseHdc(hdc);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int colorRef);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hdc, ref NativeRect rect, IntPtr brush);

        /// <summary>
        /// 擦除控件背景（用父容器的底色，行为与 WinForms 自身绘制背景一致）。
        /// 这样既不会留下重绘残影，也不会产生不透明的黑色方块。
        /// </summary>
        public static void EraseBackground(Graphics g, Control control)
        {
            var color = control.Parent?.BackColor ?? control.BackColor;
            if (color.A != 255) color = control.BackColor;
            if (color.A != 255) return;

            FillRectGdi(g, control.ClientRectangle, color);
        }

        /// <summary>填充圆角矩形。</summary>
        public static void FillRounded(Graphics g, Rectangle r, int radius, Color color)        {
            if (r.Width <= 0 || r.Height <= 0) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(r, radius);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
            g.SmoothingMode = old;
        }

        /// <summary>绘制 1px 圆角描边（内缩半个像素，保证锐利）。</summary>
        public static void DrawRoundedBorder(Graphics g, Rectangle r, int radius, Color color, float width = 1f)
        {
            if (r.Width <= 1 || r.Height <= 1) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rr = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
            using var path = RoundedRect(rr, radius);
            using var pen = new Pen(color, width);
            g.DrawPath(pen, path);
            g.SmoothingMode = old;
        }

        /// <summary>绘制抗锯齿文本。</summary>
        public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle bounds,
                                    TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
        {
            TextRenderer.DrawText(g, text, font, bounds, color, flags | TextFormatFlags.NoPadding);
        }

        /// <summary>把颜色按比例调亮/调暗（ratio &gt; 0 变亮）。</summary>
        public static Color Shade(Color c, float ratio)
        {
            if (ratio >= 0)
            {
                return Color.FromArgb(c.A,
                    (int)Math.Min(255, c.R + (255 - c.R) * ratio),
                    (int)Math.Min(255, c.G + (255 - c.G) * ratio),
                    (int)Math.Min(255, c.B + (255 - c.B) * ratio));
            }
            float f = 1f + ratio;
            return Color.FromArgb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
        }

        /// <summary>把颜色与另一颜色混合。</summary>
        public static Color Blend(Color a, Color b, float t)
            => Color.FromArgb(a.A,
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));

        // ---------------- 图标绘制 ----------------

        /// <summary>眼睛图标（密码可见 / 隐藏）。</summary>
        public static void DrawEye(Graphics g, Rectangle box, bool visible, Color color)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int w = Math.Min(box.Width - 6, 20);
            int h = Math.Max(8, (int)(w * 0.62));
            int cx = box.X + box.Width / 2;
            int cy = box.Y + box.Height / 2;
            int left = cx - w / 2, right = cx + w / 2;

            using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var path = new GraphicsPath();
            path.AddBezier(left, cy, cx - w * 0.20f, cy - h * 0.72f, cx + w * 0.20f, cy - h * 0.72f, right, cy);
            path.AddBezier(right, cy, cx + w * 0.20f, cy + h * 0.72f, cx - w * 0.20f, cy + h * 0.72f, left, cy);
            path.CloseFigure();
            g.DrawPath(pen, path);

            if (visible)
            {
                using var pupil = new SolidBrush(color);
                float r = Math.Max(2.2f, h * 0.30f);
                g.FillEllipse(pupil, cx - r, cy - r, r * 2, r * 2);
            }
            else
            {
                // 隐藏状态：瞳孔缩小 + 斜杠
                using var pupil = new SolidBrush(color);
                float r = Math.Max(1.6f, h * 0.20f);
                g.FillEllipse(pupil, cx - r, cy - r, r * 2, r * 2);
                using var slash = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(slash, left - 1, cy + h * 0.62f, right + 1, cy - h * 0.62f);
            }

            g.SmoothingMode = old;
        }

        /// <summary>VS Code 风格勾选标记。</summary>
        public static void DrawCheck(Graphics g, Rectangle box, Color color)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float x = box.X, y = box.Y, w = box.Width, h = box.Height;
            g.DrawLines(pen, new[]
            {
                new PointF(x + w * 0.22f, y + h * 0.52f),
                new PointF(x + w * 0.42f, y + h * 0.72f),
                new PointF(x + w * 0.80f, y + h * 0.28f)
            });
            g.SmoothingMode = old;
        }

        /// <summary>状态圆点。</summary>
        public static void DrawDot(Graphics g, int cx, int cy, int diameter, Color color)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(color);
            g.FillEllipse(b, cx - diameter / 2f, cy - diameter / 2f, diameter, diameter);
            g.SmoothingMode = old;
        }

        /// <summary>圆角矩形图标（应用标识：蓝色圆角方块 + 白色地球）。</summary>
        public static void DrawAppGlyph(Graphics g, Rectangle box)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var lg = new LinearGradientBrush(box, Color.FromArgb(0x1F, 0x9C, 0xEA), Color.FromArgb(0x00, 0x5A, 0x9E), 90f))
            using (var path = RoundedRect(box, Math.Max(4, box.Width / 5)))
            {
                g.FillPath(lg, path);
            }

            float s = box.Width;
            float pad = s * 0.24f;
            var inner = new RectangleF(box.X + pad, box.Y + pad, s - 2 * pad, s - 2 * pad);
            using var pen = new Pen(Color.White, Math.Max(1.3f, s * 0.075f));

            g.DrawEllipse(pen, inner);

            float cy = box.Y + s / 2f;
            g.DrawLine(pen, inner.Left, cy, inner.Right, cy);

            float ew = inner.Width * 0.42f;
            g.DrawEllipse(pen, new RectangleF(box.X + s / 2f - ew / 2f, inner.Top, ew, inner.Height));

            g.SmoothingMode = old;
        }
    }
}
