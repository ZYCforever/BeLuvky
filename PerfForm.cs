using System;
using System.Drawing;
using System.Windows.Forms;

namespace BetterWebLauncher
{
    /// <summary>
    /// 性能监视窗口。
    /// 主位展示「本程序」自身的 CPU 占用率与内存占用（大字号 + 进度条 + 每秒曲线），
    /// 次要位置展示整机 CPU / 内存负载作为参照。
    /// 采样全部使用 Win32 API，不依赖性能计数器。
    /// </summary>
    public sealed class PerfForm : Form
    {
        private static readonly Color Surface = VsTheme.EditorBg;
        private static readonly Color CardSurface = VsTheme.CardBg;

        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
        private readonly UsageSampler _sampler = new();
        private readonly DateTime _startedAt = DateTime.Now;

        // 本程序（主位）
        private VsLabel _lblAppCpuValue = null!;
        private VsLabel _lblAppMemValue = null!;
        private VsLabel _lblAppDetail = null!;
        private VsProgressBar _barAppCpu = null!;
        private VsProgressBar _barAppMem = null!;
        private VsSparkline _sparkAppCpu = null!;
        private VsSparkline _sparkAppMem = null!;

        // 整机（次要位置）
        private VsLabel _lblCpuValue = null!;
        private VsLabel _lblMemValue = null!;
        private VsLabel _lblMemUsedValue = null!;
        private VsLabel _lblCoresValue = null!;
        private VsProgressBar _barCpu = null!;
        private VsProgressBar _barMem = null!;

        private VsStatusBar _statusBar = null!;

        private int _tick;

        public PerfForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;

            BuildUi();

            _timer.Tick += OnTick;
        }

        // ------------------------------------------------------------------
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowEffects.ApplyAll(Handle, VsTheme.EditorBg, WindowEffects.AcrylicOpacity, darkTitleBar: true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using var brush = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateUi(_sampler.Sample());   // 第一次只做基准
            _timer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            _timer.Dispose();
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------------
        private void BuildUi()
        {
            Text = "BetterWebLauncher · 性能监视";
            ClientSize = new Size(700, 588);
            StartPosition = FormStartPosition.CenterScreen;
            Font = VsTheme.Ui();
            BackColor = Surface;
            Icon = AppIcon.Large;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            MinimumSize = SizeFromClientSize(new Size(620, 520));

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(20, 10, 20, 10),
                BackColor = Surface,
                Margin = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));   // 标题
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));  // 本程序占用（主位）
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 本程序曲线（主位）
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));  // 整机负载（次要）
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // 状态栏

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildAppCard(), 0, 1);
            root.Controls.Add(BuildChartCard(), 0, 2);
            root.Controls.Add(BuildSystemCard(), 0, 3);
            root.Controls.Add(BuildStatusBar(), 0, 4);

            Controls.Add(root);
        }

        private Control BuildHeader()
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = Surface,
                Margin = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 38));

            var glyph = new VsGlyph
            {
                Dock = DockStyle.Fill,
                GlyphSize = 34,
                BackColor = Surface,
                Margin = new Padding(0, 2, 12, 4)
            };
            header.Controls.Add(glyph, 0, 0);
            header.SetRowSpan(glyph, 2);

            header.Controls.Add(new VsLabel
            {
                Text = "性能监视",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(15F, FontStyle.Bold),
                ForeColor = VsTheme.TextBright,
                BackColor = Surface,
                TextAlign = ContentAlignment.BottomLeft,
                Margin = new Padding(0, 0, 0, 2)
            }, 1, 0);

            header.Controls.Add(new VsLabel
            {
                Text = "本程序 CPU / 内存占用 · 每秒刷新",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(9F),
                ForeColor = VsTheme.TextMuted,
                BackColor = Surface,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(1, 2, 0, 0)
            }, 1, 1);

            return header;
        }

        // ---- 主位：本程序占用 ----
        private Control BuildAppCard()
        {
            var card = new VsCard
            {
                Dock = DockStyle.Fill,
                CornerRadius = 8,
                CardColor = CardSurface,
                Margin = new Padding(0, 0, 0, 4)
            };

            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                BackColor = CardSurface,
                Margin = new Padding(0)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); // 卡片标题
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // 指标名
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); // 大数值
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 14)); // 进度条
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 明细

            var title = new VsLabel
            {
                Text = "本程序占用",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(10.5F, FontStyle.Bold),
                ForeColor = VsTheme.TextPrimary,
                BackColor = CardSurface,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };
            t.Controls.Add(title, 0, 0);

            t.Controls.Add(new VsLabel
            {
                Text = $"进程 PID {Environment.ProcessId}",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(8.5F),
                ForeColor = VsTheme.TextMuted,
                BackColor = CardSurface,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(0)
            }, 1, 0);

            t.Controls.Add(SmallLabel("CPU 占用率"), 0, 1);
            t.Controls.Add(SmallLabel("内存占用", leftPad: 10), 1, 1);

            _lblAppCpuValue = BigValue("-", VsTheme.Info);
            _lblAppMemValue = BigValue("-", VsTheme.Purple, leftPad: 10);
            t.Controls.Add(_lblAppCpuValue, 0, 2);
            t.Controls.Add(_lblAppMemValue, 1, 2);

            _barAppCpu = new VsProgressBar
            {
                Dock = DockStyle.Fill,
                BarColor = VsTheme.Info,
                BackColor = CardSurface,
                Margin = new Padding(0, 3, 6, 3)
            };
            _barAppMem = new VsProgressBar
            {
                Dock = DockStyle.Fill,
                BarColor = VsTheme.Purple,
                BackColor = CardSurface,
                Margin = new Padding(10, 3, 0, 3)
            };
            t.Controls.Add(_barAppCpu, 0, 3);
            t.Controls.Add(_barAppMem, 1, 3);

            _lblAppDetail = new VsLabel
            {
                Text = "正在采样…",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(8.5F),
                ForeColor = VsTheme.TextMuted,
                BackColor = CardSurface,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(0, 6, 0, 0)
            };
            t.Controls.Add(_lblAppDetail, 0, 4);
            t.SetColumnSpan(_lblAppDetail, 2);

            card.Controls.Add(t);
            return card;
        }

        // ---- 主位：本程序曲线 ----
        private Control BuildChartCard()
        {
            var card = new VsCard
            {
                Dock = DockStyle.Fill,
                CornerRadius = 8,
                CardColor = CardSurface,
                Margin = new Padding(0, 0, 0, 4)
            };
            card.Padding = new Padding(12, 10, 12, 12);

            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = CardSurface,
                Margin = new Padding(0)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            t.Controls.Add(new VsLabel
            {
                Text = "本程序最近 60 秒曲线",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(10F, FontStyle.Bold),
                ForeColor = VsTheme.TextPrimary,
                BackColor = CardSurface,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            }, 0, 0);

            _sparkAppCpu = new VsSparkline
            {
                Dock = DockStyle.Fill,
                Caption = "本程序 CPU 占用率",
                Unit = "%",
                MaxValue = 100,
                LineColor = VsTheme.Info,
                AreaColor = Color.FromArgb(48, VsTheme.Info),
                BackColor = CardSurface,
                Margin = new Padding(0, 2, 0, 2)
            };
            _sparkAppMem = new VsSparkline
            {
                Dock = DockStyle.Fill,
                Caption = "本程序内存占用",
                Unit = "MB",
                AutoScale = true,
                LineColor = VsTheme.Purple,
                AreaColor = Color.FromArgb(48, VsTheme.Purple),
                BackColor = CardSurface,
                Margin = new Padding(0, 2, 0, 0)
            };
            t.Controls.Add(_sparkAppCpu, 0, 1);
            t.Controls.Add(_sparkAppMem, 0, 2);

            card.Controls.Add(t);
            return card;
        }

        // ---- 次要位置：整机负载 ----
        private Control BuildSystemCard()
        {
            var card = new VsCard
            {
                Dock = DockStyle.Fill,
                CornerRadius = 8,
                CardColor = CardSurface,
                Margin = new Padding(0, 0, 0, 4)
            };
            card.Padding = new Padding(16, 10, 16, 10);

            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 3,
                BackColor = CardSurface,
                Margin = new Padding(0)
            };
            for (int i = 0; i < 4; i++)
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));

            t.Controls.Add(SmallLabel("整机 CPU"), 0, 0);
            t.Controls.Add(SmallLabel("整机内存", leftPad: 8), 1, 0);
            t.Controls.Add(SmallLabel("内存已用", leftPad: 8), 2, 0);
            t.Controls.Add(SmallLabel("逻辑处理器", leftPad: 8), 3, 0);

            _lblCpuValue = Cell("-", VsTheme.TextPrimary);
            _lblMemValue = Cell("-", VsTheme.TextPrimary);
            _lblMemUsedValue = Cell("-", VsTheme.TextPrimary);
            _lblCoresValue = Cell(Environment.ProcessorCount + " 个", VsTheme.TextMuted);
            t.Controls.Add(_lblCpuValue, 0, 1);
            t.Controls.Add(_lblMemValue, 1, 1);
            t.Controls.Add(_lblMemUsedValue, 2, 1);
            t.Controls.Add(_lblCoresValue, 3, 1);

            _barCpu = new VsProgressBar
            {
                Dock = DockStyle.Fill,
                BarColor = VsTheme.Info,
                BackColor = CardSurface,
                Margin = new Padding(0, 0, 8, 2)
            };
            _barMem = new VsProgressBar
            {
                Dock = DockStyle.Fill,
                BarColor = VsTheme.Teal,
                BackColor = CardSurface,
                Margin = new Padding(8, 0, 8, 2)
            };
            t.Controls.Add(_barCpu, 0, 2);
            t.Controls.Add(_barMem, 1, 2);

            card.Controls.Add(t);
            return card;
        }

        private Control BuildStatusBar()
        {
            _statusBar = new VsStatusBar
            {
                Dock = DockStyle.Fill,
                LeftText = "性能监视 · 每秒采样一次",
                RightText = "本程序 CPU -   内存 -",
                BarColor = VsTheme.Accent,
                DotColor = VsTheme.Info
            };
            return _statusBar;
        }

        // ---- 控件工厂 ----
        private static VsLabel SmallLabel(string text, int leftPad = 0) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            Font = VsTheme.Ui(9F),
            ForeColor = VsTheme.TextMuted,
            BackColor = CardSurface,
            TextAlign = ContentAlignment.BottomLeft,
            Margin = new Padding(leftPad, 0, 0, 2)
        };

        private static VsLabel BigValue(string text, Color color, int leftPad = 0) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            Font = VsTheme.Ui(21F, FontStyle.Bold),
            ForeColor = color,
            BackColor = CardSurface,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(leftPad, 0, 0, 0)
        };

        private static VsLabel Cell(string text, Color color) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            Font = VsTheme.Ui(11F, FontStyle.Bold),
            ForeColor = color,
            BackColor = CardSurface,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };

        // ------------------------------------------------------------------
        private void OnTick(object? sender, EventArgs e)
        {
            try { UpdateUi(_sampler.Sample()); }
            catch { }
        }

        private void UpdateUi(SystemUsage u)
        {
            _tick++;
            bool hasSample = _tick > 1;

            // ---------- 主位：本程序 ----------
            _lblAppCpuValue.Text = hasSample ? $"{u.AppCpuPercent:0.0} %" : "-- %";
            _lblAppMemValue.Text = $"{u.AppMemMB:0.0} MB";

            _barAppCpu.Value = u.AppCpuPercent;
            _barAppCpu.BarColor = VsTheme.Info;

            // 内存条形按“占物理内存比例”显示，并保证最小值可见
            _barAppMem.Value = Math.Min(100, u.AppMemPercent);

            _lblAppDetail.Text =
                $"占物理内存 {u.AppMemPercent:0.00} %　·　线程 {u.AppThreads}　·　" +
                $"运行 {FormatUptime(DateTime.Now - _startedAt)}";

            // ---------- 次要位置：整机 ----------
            _lblCpuValue.Text = hasSample ? $"{u.CpuPercent:0.0} %" : "-- %";
            _lblCpuValue.ForeColor = LoadColor(u.CpuPercent);
            _barCpu.Value = u.CpuPercent;
            _barCpu.BarColor = LoadColor(u.CpuPercent);

            _lblMemValue.Text = $"{u.MemPercent:0.0} %";
            _lblMemValue.ForeColor = LoadColor(u.MemPercent);
            _barMem.Value = u.MemPercent;
            _barMem.BarColor = LoadColor(u.MemPercent);

            _lblMemUsedValue.Text = $"{u.UsedMemGB:0.0} / {u.TotalMemGB:0.0} GB";
            _lblMemUsedValue.ForeColor = VsTheme.TextPrimary;

            // ---------- 曲线 + 状态栏 ----------
            if (hasSample)
            {
                _sparkAppCpu.Push(u.AppCpuPercent);
                _sparkAppMem.Push(u.AppMemMB);
            }

            _statusBar.RightText = $"本程序 CPU {_lblAppCpuValue.Text}　内存 {_lblAppMemValue.Text}";
            _statusBar.DotColor = LoadColor(Math.Max(u.CpuPercent, u.MemPercent));
        }

        /// <summary>负载配色：正常蓝 / 偏高黄 / 很高红。</summary>
        private static Color LoadColor(double percent)
            => percent >= 90 ? VsTheme.Error
             : percent >= 70 ? VsTheme.Warn
             : VsTheme.Info;

        private static string FormatUptime(TimeSpan ts)
            => ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分"
                : ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes} 分 {ts.Seconds} 秒"
                : $"{ts.Seconds} 秒";
    }
}
