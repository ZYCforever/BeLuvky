using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BetterWebLauncher
{
    internal enum VsButtonKind
    {
        Primary,    // 蓝色主按钮（VS Code 默认按钮）
        Secondary,  // 灰色次要按钮
        Danger,     // 红色危险按钮
        Ghost       // 无底色，仅悬停时出现浅色背景
    }

    /// <summary>
    /// VS Code 风格按钮：圆角、悬停/按下配色、无系统边框。
    /// 注意：所有控件都使用与所在面板一致的“不透明”底色，
    /// 避免 WinForms 透明控件互相重绘造成的文字重影。
    /// </summary>
    internal class VsButton : Button
    {
        private bool _hover;
        private bool _pressed;

        public VsButtonKind Kind { get; set; } = VsButtonKind.Primary;
        public int CornerRadius { get; set; } = 4;

        public VsButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = VsTheme.EditorBg;
            Font = VsTheme.Ui(9.75F);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            VsTheme.EraseBackground(g, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var r = new Rectangle(0, 0, Width, Height);
            Color back, border, fore;

            if (!Enabled)
            {
                back = Kind == VsButtonKind.Ghost ? Color.Empty : VsTheme.SecondaryDown;
                border = Color.Empty;
                fore = VsTheme.TextDisabled;
            }
            else
            {
                switch (Kind)
                {
                    case VsButtonKind.Secondary:
                        back = _pressed ? VsTheme.SecondaryDown : _hover ? VsTheme.SecondaryHover : VsTheme.SecondaryBg;
                        border = VsTheme.Border;
                        fore = VsTheme.TextPrimary;
                        break;

                    case VsButtonKind.Danger:
                        back = _pressed ? VsTheme.DangerDown : _hover ? VsTheme.DangerHover : VsTheme.DangerBg;
                        border = VsTheme.Shade(VsTheme.DangerBg, 0.25f);
                        fore = Color.FromArgb(0xFF, 0xE4, 0xDE);
                        break;

                    case VsButtonKind.Ghost:
                        back = _pressed ? VsTheme.SecondaryDown : _hover ? VsTheme.CardBgAlt : Color.Empty;
                        border = Color.Empty;
                        fore = _hover ? VsTheme.TextBright : VsTheme.TextMuted;
                        break;

                    default: // Primary
                        back = _pressed ? VsTheme.ButtonPressed : _hover ? VsTheme.ButtonHover : VsTheme.ButtonBg;
                        border = VsTheme.Shade(VsTheme.ButtonBg, 0.18f);
                        fore = Color.White;
                        break;
                }
            }

            if (back != Color.Empty) VsTheme.FillRounded(g, r, CornerRadius, back);
            if (border != Color.Empty) VsTheme.DrawRoundedBorder(g, r, CornerRadius, border);

            TextRenderer.DrawText(g, Text, Font, r, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>仅绘制图标的方形按钮（用于密码可见切换）。</summary>
    internal class VsIconButton : Button
    {
        private bool _hover;
        private bool _pressed;
        private ToolTip? _tip;

        /// <summary>true 表示“密码可见”，图标为睁眼；false 为闭眼 + 斜杠。</summary>
        public bool Toggled { get; set; }

        public string? TipText { get; set; }
        public int CornerRadius { get; set; } = 4;

        public VsIconButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = VsTheme.InputBg;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();

            if (!string.IsNullOrEmpty(TipText))
            {
                _tip ??= new ToolTip { InitialDelay = 350, ReshowDelay = 200 };
                _tip.SetToolTip(this, TipText);
            }

            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            VsTheme.EraseBackground(g, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var r = new Rectangle(0, 0, Width, Height);

            if (_hover || _pressed)
            {
                var back = _pressed ? Color.FromArgb(0x5A, 0x5A, 0x5A) : Color.FromArgb(0x4A, 0x4A, 0x4A);
                VsTheme.FillRounded(g, r, CornerRadius, back);
            }

            var iconColor = !Enabled ? VsTheme.TextDisabled
                : Toggled ? VsTheme.Info
                : _hover ? VsTheme.TextBright
                : VsTheme.TextMuted;

            VsTheme.DrawEye(g, r, Toggled, iconColor);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>VS Code 风格复选框：14px 圆角方框，勾选时填充强调蓝。</summary>
    internal class VsCheckBox : CheckBox
    {
        private bool _hover;

        public VsCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Font = VsTheme.Ui(9.75F);
            ForeColor = VsTheme.TextPrimary;
            BackColor = VsTheme.EditorBg;
            Cursor = Cursors.Hand;
            AutoSize = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            VsTheme.EraseBackground(g, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            const int box = 14;
            int y = (Height - box) / 2;
            var boxRect = new Rectangle(1, y, box, box);

            if (Checked)
            {
                VsTheme.FillRounded(g, boxRect, 3, Enabled ? VsTheme.Accent : VsTheme.ControlBorder);
                VsTheme.DrawCheck(g,
                    new Rectangle(boxRect.X + 1, boxRect.Y + 1, boxRect.Width - 2, boxRect.Height - 2),
                    Enabled ? Color.White : VsTheme.TextDisabled);
            }
            else
            {
                VsTheme.FillRounded(g, boxRect, 3, _hover ? VsTheme.InputBgHover : VsTheme.CardBgAlt);
                VsTheme.DrawRoundedBorder(g, boxRect, 3, Enabled ? VsTheme.ControlBorder : VsTheme.BorderSubtle);
            }

            var textRect = new Rectangle(box + 8, 0, Math.Max(0, Width - box - 10), Height);
            TextRenderer.DrawText(g, Text, Font, textRect,
                Enabled ? ForeColor : VsTheme.TextDisabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>
    /// VS Code 风格输入框：圆角背景 + 1px 边框 + 聚焦高亮，
    /// 密码模式下右侧内嵌“眼睛”按钮切换可见性。
    /// </summary>
    internal class VsInput : Panel
    {
        private readonly TextBox _box = new();
        private readonly VsIconButton? _eye;
        private bool _focused;
        private bool _sanitizing;

        public bool IsPassword { get; }
        public TextBox Inner => _box;

        /// <summary>只允许输入数字（用于秒数等数值配置）。</summary>
        public bool NumericOnly { get; set; }

        /// <summary>最大输入位数（仅数字模式有效）。</summary>
        public int MaxDigits { get; set; } = 5;

        /// <summary>密码当前是否明文可见。</summary>
        public bool PasswordVisible => _eye?.Toggled ?? false;

        public event EventHandler? PasswordVisibilityChanged;

        public VsInput(bool password = false, string? placeholder = null)
        {
            IsPassword = password;

            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = VsTheme.InputBg;
            Height = 32;

            _box.AutoSize = false;
            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = VsTheme.InputBg;
            _box.ForeColor = VsTheme.TextPrimary;
            _box.Font = VsTheme.Ui(10F);
            _box.UseSystemPasswordChar = password;
            if (!string.IsNullOrEmpty(placeholder)) _box.PlaceholderText = placeholder;
            _box.GotFocus += (_, _) => { _focused = true; Invalidate(); };
            _box.LostFocus += (_, _) => { _focused = false; Invalidate(); };
            _box.KeyPress += (_, e) =>
            {
                if (!NumericOnly) return;
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
            };
            _box.TextChanged += (_, _) =>
            {
                if (NumericOnly) SanitizeNumeric();
                OnTextChanged(EventArgs.Empty);
            };
            Controls.Add(_box);

            if (password)
            {
                _eye = new VsIconButton { Toggled = false, TipText = "显示密码", BackColor = VsTheme.InputBg };
                _eye.Click += (_, _) => TogglePassword();
                Controls.Add(_eye);
            }

            Click += (_, _) => _box.Focus();
        }

        /// <summary>在“密码可见 / 密码隐藏”之间切换。</summary>
        public void TogglePassword()
        {
            if (_eye == null) return;

            _eye.Toggled = !_eye.Toggled;
            _box.UseSystemPasswordChar = !_eye.Toggled;
            _eye.TipText = _eye.Toggled ? "隐藏密码" : "显示密码";
            _eye.Invalidate();

            _box.Focus();
            _box.SelectionStart = _box.TextLength;
            _box.SelectionLength = 0;

            PasswordVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }

        public override string Text
        {
            get => _box.Text;
            set => _box.Text = value ?? string.Empty;
        }

        /// <summary>只保留数字，并限制最大位数。</summary>
        private void SanitizeNumeric()
        {
            if (_sanitizing) return;

            var digits = new string(Array.FindAll(_box.Text.ToCharArray(), char.IsDigit));
            if (digits.Length > MaxDigits) digits = digits.Substring(0, MaxDigits);
            if (digits == _box.Text) return;

            _sanitizing = true;
            int caret = _box.SelectionStart;
            _box.Text = digits;
            _box.SelectionStart = Math.Min(caret, _box.TextLength);
            _sanitizing = false;
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);

            int rightPad = _eye != null ? 36 : 10;
            int h = Math.Min(_box.PreferredHeight, Height);
            _box.Bounds = new Rectangle(11, Math.Max(0, (Height - h) / 2), Math.Max(20, Width - 11 - rightPad), h);

            if (_eye != null)
            {
                const int size = 24;
                _eye.Bounds = new Rectangle(Width - size - 5, Math.Max(0, (Height - size) / 2), size, size);
                _eye.BringToFront();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            VsTheme.EraseBackground(g, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width, Height);

            VsTheme.FillRounded(g, r, 4, VsTheme.InputBg);
            VsTheme.DrawRoundedBorder(g, r, 4, _focused ? VsTheme.FocusBorder : VsTheme.Border);

            if (_focused)
            {
                // VS Code 输入框聚焦时的淡蓝描边
                var glow = new Rectangle(1, 1, Width - 3, Height - 3);
                VsTheme.DrawRoundedBorder(g, glow, 3, Color.FromArgb(90, VsTheme.FocusBorder));
            }
        }
    }

    /// <summary>圆角卡片容器（VS Code 面板底色 + 1px 边框）。</summary>
    internal class VsCard : Panel
    {
        public int CornerRadius { get; set; } = 6;
        public Color CardColor { get; set; } = VsTheme.CardBg;
        public Color BorderColor { get; set; } = VsTheme.Border;
        public bool ShowBorder { get; set; } = true;

        public VsCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = VsTheme.CardBg;
            Padding = new Padding(18, 14, 18, 14);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            VsTheme.EraseBackground(g, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width, Height);
            VsTheme.FillRounded(g, r, CornerRadius, CardColor);
            if (ShowBorder) VsTheme.DrawRoundedBorder(g, r, CornerRadius, BorderColor);
        }
    }

    /// <summary>绘制应用图标字形的小控件。</summary>
    internal class VsGlyph : Control
    {
        public int GlyphSize { get; set; } = 30;

        public VsGlyph()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = VsTheme.EditorBg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            VsTheme.EraseBackground(g, this);

            int s = Math.Min(GlyphSize, Math.Min(Width, Height));
            var box = new Rectangle((Width - s) / 2, (Height - s) / 2, s, s);
            VsTheme.DrawAppGlyph(g, box);
        }
    }

    /// <summary>VS Code 底部状态栏：左侧状态文字 + 右侧速率文字。</summary>
    internal class VsStatusBar : Control
    {
        private string _leftText = "";
        private string _rightText = "";
        private Color _barColor = VsTheme.Accent;
        private Color _dotColor = VsTheme.Ok;

        public Color BarColor { get => _barColor; set { _barColor = value; Invalidate(); } }
        public Color DotColor { get => _dotColor; set { _dotColor = value; Invalidate(); } }

        public string LeftText
        {
            get => _leftText;
            set { if (_leftText != value) { _leftText = value; Invalidate(); } }
        }

        public string RightText
        {
            get => _rightText;
            set { if (_rightText != value) { _rightText = value; Invalidate(); } }
        }

        public VsStatusBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 26;
            Font = VsTheme.Ui(9F);
            BackColor = VsTheme.Accent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(_barColor)) g.FillRectangle(b, ClientRectangle);

            int cy = Height / 2;
            int x = 12;
            if (_dotColor != Color.Empty)
            {
                VsTheme.DrawDot(g, x + 4, cy, 8, _dotColor);
                x += 16;
            }

            TextRenderer.DrawText(g, _leftText, Font, new Rectangle(x, 0, Width / 2, Height), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis);

            if (!string.IsNullOrEmpty(_rightText))
            {
                TextRenderer.DrawText(g, _rightText, Font, new Rectangle(Width / 2, 0, Width / 2 - 12, Height), Color.White,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                    TextFormatFlags.EndEllipsis);
            }
        }
    }

    /// <summary>
    /// 文本标签：底色与所在面板一致（不透明），只负责画文字，
    /// 避免透明控件互相重绘产生重影。
    /// </summary>
    internal class VsLabel : Label
    {
        public VsLabel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = VsTheme.EditorBg;
            ForeColor = VsTheme.TextPrimary;
            Font = VsTheme.Ui();
        }
    }

    /// <summary>圆角进度条（用于展示占用率）。</summary>
    internal class VsProgressBar : Control
    {
        private double _value;

        public double Value
        {
            get => _value;
            set
            {
                var v = Math.Max(0, Math.Min(100, value));
                if (Math.Abs(v - _value) < 0.05) return;
                _value = v;
                Invalidate();
            }
        }

        public Color BarColor { get; set; } = VsTheme.Accent;
        public Color TrackColor { get; set; } = VsTheme.CardBgAlt;
        public int CornerRadius { get; set; } = 3;

        public VsProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = VsTheme.CardBg;
            Height = 8;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            VsTheme.EraseBackground(g, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var r = new Rectangle(0, 0, Width, Height);
            VsTheme.FillRounded(g, r, CornerRadius, TrackColor);

            int w = (int)Math.Round(Width * (_value / 100.0));
            if (w > 0)
            {
                int min = Math.Min(Width, CornerRadius * 2 + 2);
                VsTheme.FillRounded(g, new Rectangle(0, 0, Math.Max(min, w), Height), CornerRadius, BarColor);
            }
        }
    }

    /// <summary>占用率历史曲线（最近 N 次采样），带标题与当前值。</summary>
    internal class VsSparkline : Control
    {
        private readonly double[] _values;
        private int _next;
        private int _count;

        public string Caption { get; set; } = "";
        public Color LineColor { get; set; } = VsTheme.Accent;
        public Color AreaColor { get; set; } = Color.FromArgb(56, VsTheme.Accent);
        public Color GridColor { get; set; } = VsTheme.BorderSubtle;

        /// <summary>数值单位（% 或 MB）。</summary>
        public string Unit { get; set; } = "%";

        /// <summary>固定刻度上限（AutoScale 为 false 时生效）。</summary>
        public double MaxValue { get; set; } = 100;

        /// <summary>true 时按缓冲区内最大值自动缩放（适合 MB 这类无固定上限的值）。</summary>
        public bool AutoScale { get; set; }

        /// <summary>最新一次采样值。</summary>
        public double Current { get; private set; }

        /// <summary>右上角数值用的字体（缓存，避免每次重绘都新建）。</summary>
        private readonly Font ValueFont = VsTheme.Ui(9F, FontStyle.Bold);

        public VsSparkline(int capacity = 60)
        {
            _values = new double[Math.Max(2, capacity)];
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = VsTheme.CardBg;
            Height = 70;
        }

        public void Push(double value)
        {
            value = Math.Max(0, AutoScale ? value : Math.Min(MaxValue, value));
            _values[_next] = value;
            _next = (_next + 1) % _values.Length;
            if (_count < _values.Length) _count++;
            Current = value;
            Invalidate();
        }

        /// <summary>当前实际使用的刻度上限。</summary>
        private double EffectiveMax()
        {
            if (!AutoScale) return Math.Max(0.001, MaxValue);
            double max = 0;
            int start = (_next - _count + _values.Length) % _values.Length;
            for (int i = 0; i < _count; i++) max = Math.Max(max, _values[(start + i) % _values.Length]);
            return Math.Max(1, max * 1.2);
        }

        public void Reset()
        {
            Array.Clear(_values, 0, _values.Length);
            _next = 0;
            _count = 0;
            Current = 0;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            VsTheme.EraseBackground(g, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            const int headerH = 20;
            var valueText = $"{Current:0.0} {Unit}";
            int valueW = TextRenderer.MeasureText(valueText, ValueFont).Width + 8;
            int padR = Math.Max(46, valueW);

            // 标题 + 当前值
            TextRenderer.DrawText(g, Caption, VsTheme.Ui(9F),
                new Rectangle(0, 0, Math.Max(10, Width - padR - 8), headerH), VsTheme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis);

            TextRenderer.DrawText(g, valueText, ValueFont,
                new Rectangle(Width - padR, 0, padR, headerH), LineColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            var plot = new Rectangle(0, headerH, Width, Height - headerH - 1);
            if (plot.Width < 4 || plot.Height < 4) return;

            double scale = EffectiveMax();

            // 网格：25% / 50% / 75%
            using (var pen = new Pen(GridColor, 1f))
            {
                for (int i = 1; i <= 3; i++)
                {
                    int y = plot.Bottom - plot.Height * i / 4;
                    g.DrawLine(pen, plot.Left, y, plot.Right - 1, y);
                }
            }

            if (_count < 2) return;

            var points = new PointF[_count];
            int start = (_next - _count + _values.Length) % _values.Length;
            float step = plot.Width / (float)(_values.Length - 1);

            for (int i = 0; i < _count; i++)
            {
                double v = _values[(start + i) % _values.Length];
                float x = plot.Left + i * step;
                float y = plot.Bottom - (float)(v / scale) * (plot.Height - 2) - 1;
                points[i] = new PointF(x, y);
            }

            // 面积
            using (var path = new GraphicsPath())
            {
                path.AddLines(points);
                path.AddLine(points[_count - 1].X, plot.Bottom, points[0].X, plot.Bottom);
                path.CloseFigure();
                using var brush = new SolidBrush(AreaColor);
                g.FillPath(brush, path);
            }

            // 折线
            using (var pen = new Pen(LineColor, 1.6f) { LineJoin = LineJoin.Round })
            {
                if (_count == 2) g.DrawLine(pen, points[0], points[1]);
                else g.DrawLines(pen, points);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ValueFont.Dispose();
            base.Dispose(disposing);
        }
    }
}
