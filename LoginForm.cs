using System;
using System.Drawing;
using System.Windows.Forms;

namespace BetterWebLauncher
{
    /// <summary>
    /// 登录 / 设置窗口：VS Code 深色风格 + 毛玻璃（亚克力）半透明背景。
    /// 密码输入框右侧内嵌“眼睛”按钮，可切换明文 / 密文显示。
    /// </summary>
    public sealed class LoginForm : Form
    {
        private readonly AppSettings _settings;

        // 两个面：窗口面（EditorBg）与卡片面（CardBg）
        private static readonly Color Surface = VsTheme.EditorBg;
        private static readonly Color CardSurface = VsTheme.CardBg;

        // ---------- 界面控件 ----------
        private VsGlyph _glyph = null!;
        private VsInput _inpConn = null!;
        private VsInput _inpUser = null!;
        private VsInput _inpPass = null!;
        private VsInput _inpInterval = null!;
        private VsCheckBox _chkRemember = null!;
        private VsCheckBox _chkAutoDial = null!;
        private VsCheckBox _chkAutoStart = null!;
        private VsButton _btnOk = null!;

        public LoginForm(AppSettings settings)
        {
            _settings = settings;

            // 关闭自动缩放，所有坐标由代码精确控制（125%/150% DPI 下不错位）
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;

            BuildUi();
        }

        // ------------------------------------------------------------------
        // 窗口效果：亚克力毛玻璃 + 圆角 + 深色标题栏
        // ------------------------------------------------------------------
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowEffects.ApplyAll(Handle, VsTheme.EditorBg, WindowEffects.AcrylicOpacity, darkTitleBar: true);
            Layout += (_, _) => Invalidate();
        }

        /// <summary>
        /// 用主题底色铺满客户区，再叠加 DWM 亚克力染色层：
        /// 既得到深色毛玻璃半透明质感，又不会留下未绘制的残影。
        /// </summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using var brush = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        // ------------------------------------------------------------------
        // 界面构建
        // ------------------------------------------------------------------
        private void BuildUi()
        {
            Text = "BetterWebLauncher · 登录";
            ClientSize = new Size(624, 470);
            StartPosition = FormStartPosition.CenterScreen;
            Font = VsTheme.Ui();
            BackColor = Surface;
            Icon = AppIcon.Large;

            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            MinimumSize = SizeFromClientSize(ClientSize);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(20, 10, 20, 16),
                BackColor = Surface,
                Margin = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));   // 标题区
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 321));  // 设置卡片
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 弹性空白
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));   // 按钮行

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildCard(), 0, 1);
            root.Controls.Add(BuildButtons(), 0, 3);

            Controls.Add(root);
            AcceptButton = _btnOk;
        }

        // ---- 顶部标题区 ----
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

            _glyph = new VsGlyph
            {
                Dock = DockStyle.Fill,
                GlyphSize = 34,
                BackColor = Surface,
                Margin = new Padding(0, 4, 12, 6)
            };
            header.Controls.Add(_glyph, 0, 0);
            header.SetRowSpan(_glyph, 2);

            header.Controls.Add(new VsLabel
            {
                Text = "BetterWebLauncher",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(15F, FontStyle.Bold),
                ForeColor = VsTheme.TextBright,
                BackColor = Surface,
                TextAlign = ContentAlignment.BottomLeft,
                Margin = new Padding(0, 0, 0, 2)
            }, 1, 0);

            header.Controls.Add(new VsLabel
            {
                Text = "宽带自动拨号助手 · 连接设置",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(9F),
                ForeColor = VsTheme.TextMuted,
                BackColor = Surface,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(1, 2, 0, 0)
            }, 1, 1);

            return header;
        }

        // ---- 中间设置卡片 ----
        private Control BuildCard()
        {
            var card = new VsCard
            {
                Dock = DockStyle.Fill,
                CornerRadius = 8,
                CardColor = CardSurface,
                Margin = new Padding(0)
            };

            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 11,
                BackColor = CardSurface,
                Margin = new Padding(0)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); // 0 卡片标题
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));  // 1 分隔线
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); // 2 标签
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // 3 输入
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); // 4 密码标签
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // 5 密码输入
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); // 6 重连间隔标签
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // 7 重连间隔输入
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); // 8 复选框
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // 9 开机自启
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 10 说明文字

            var cardTitle = new VsLabel
            {
                Text = "连接设置",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(10.5F, FontStyle.Bold),
                ForeColor = VsTheme.TextPrimary,
                BackColor = CardSurface,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };
            t.Controls.Add(cardTitle, 0, 0);
            t.SetColumnSpan(cardTitle, 2);

            var separator = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = VsTheme.BorderSubtle,
                Margin = new Padding(0)
            };
            t.Controls.Add(separator, 0, 1);
            t.SetColumnSpan(separator, 2);

            // 行 2/3：连接名称 + 宽带账号
            t.Controls.Add(FieldLabel("宽带连接名称"), 0, 2);
            t.Controls.Add(FieldLabel("宽带账号", leftPad: 10), 1, 2);

            _inpConn = new VsInput(false, "例如：宽带连接")
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 5, 2)
            };
            _inpConn.Text = _settings.ConnectionName;
            t.Controls.Add(_inpConn, 0, 3);

            _inpUser = new VsInput(false, "运营商提供的上网账号")
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(5, 2, 0, 2)
            };
            _inpUser.Text = _settings.Username;
            t.Controls.Add(_inpUser, 1, 3);

            // 行 4/5：宽带密码（带“眼睛”按钮）
            var passLabel = FieldLabel("宽带密码");
            t.Controls.Add(passLabel, 0, 4);
            t.SetColumnSpan(passLabel, 2);

            _inpPass = new VsInput(true, "宽带密码")
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 2)
            };
            _inpPass.Text = _settings.RememberPassword ? _settings.Password : string.Empty;
            t.Controls.Add(_inpPass, 0, 5);
            t.SetColumnSpan(_inpPass, 2);

            // 行 6/7：断线重连间隔（秒）
            var intervalLabel = FieldLabel("断线重连间隔");
            t.Controls.Add(intervalLabel, 0, 6);
            t.SetColumnSpan(intervalLabel, 2);

            _inpInterval = new VsInput(false, "5")
            {
                Dock = DockStyle.Fill,
                NumericOnly = true,
                MaxDigits = 4,
                Margin = new Padding(0, 2, 5, 2)
            };
            _inpInterval.Text = AppSettings.ClampReconnectInterval(_settings.ReconnectIntervalSeconds).ToString();
            t.Controls.Add(_inpInterval, 0, 7);

            t.Controls.Add(new VsLabel
            {
                Text = "单位：秒（默认 5 秒，失败后每隔这么久重试一次）",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(8.25F),
                ForeColor = VsTheme.TextMuted,
                BackColor = CardSurface,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Margin = new Padding(6, 2, 0, 2)
            }, 1, 7);

            // 行 8：复选框
            _chkRemember = new VsCheckBox
            {
                Text = "记住密码",
                Checked = _settings.RememberPassword,
                Dock = DockStyle.Fill,
                BackColor = CardSurface,
                Margin = new Padding(0, 8, 5, 0)
            };
            _chkAutoDial = new VsCheckBox
            {
                Text = "登录后自动拨号",
                Checked = _settings.AutoDialOnStart,
                Dock = DockStyle.Fill,
                BackColor = CardSurface,
                Margin = new Padding(5, 8, 0, 0)
            };
            t.Controls.Add(_chkRemember, 0, 8);
            t.Controls.Add(_chkAutoDial, 1, 8);

            // 行 9：开机自启
            _chkAutoStart = new VsCheckBox
            {
                Text = "开机自动启动本程序",
                Checked = _settings.StartWithWindows || StartupManager.IsStartupEnabled(),
                Dock = DockStyle.Fill,
                BackColor = CardSurface,
                Margin = new Padding(0, 6, 0, 0)
            };
            t.Controls.Add(_chkAutoStart, 0, 9);
            t.SetColumnSpan(_chkAutoStart, 2);

            // 行 10：加密说明
            var hint = new VsLabel
            {
                Text = "密码使用 AES 加密后仅保存在本机，不会上传",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(8.25F),
                ForeColor = VsTheme.TextMuted,
                BackColor = CardSurface,
                TextAlign = ContentAlignment.TopRight,
                Margin = new Padding(0, 4, 0, 0)
            };
            t.Controls.Add(hint, 0, 10);
            t.SetColumnSpan(hint, 2);

            card.Controls.Add(t);
            return card;
        }

        private static VsLabel FieldLabel(string text, int leftPad = 0) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            Font = VsTheme.Ui(9F),
            ForeColor = VsTheme.TextMuted,
            BackColor = CardSurface,
            TextAlign = ContentAlignment.BottomLeft,
            Margin = new Padding(leftPad, 0, 0, 4)
        };

        // ---- 底部按钮行 ----
        private Control BuildButtons()
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Surface,
                Margin = new Padding(0)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

            row.Controls.Add(new VsLabel
            {
                Text = "按 Enter 直接连接",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(8.5F),
                ForeColor = VsTheme.TextMuted,
                BackColor = Surface,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(2, 0, 0, 0)
            }, 0, 0);

            _btnOk = new VsButton
            {
                Text = "连接",
                Kind = VsButtonKind.Primary,
                Dock = DockStyle.Fill,
                BackColor = Surface,
                Margin = new Padding(0, 4, 8, 4)
            };
            _btnOk.Click += OnOkClick;
            row.Controls.Add(_btnOk, 1, 0);

            var btnCancel = new VsButton
            {
                Text = "取消",
                Kind = VsButtonKind.Secondary,
                Dock = DockStyle.Fill,
                BackColor = Surface,
                DialogResult = DialogResult.Cancel,
                Margin = new Padding(0, 4, 0, 4)
            };
            row.Controls.Add(btnCancel, 2, 0);

            CancelButton = btnCancel;
            return row;
        }

        // ------------------------------------------------------------------
        // 保存
        // ------------------------------------------------------------------
        private void OnOkClick(object? sender, EventArgs e)
        {
            var conn = _inpConn.Text.Trim();
            if (string.IsNullOrEmpty(conn))
            {
                Warn("请填写宽带连接名称（可在「网络连接」中查看）。");
                return;
            }

            if (string.IsNullOrWhiteSpace(_inpUser.Text))
            {
                Warn("请填写宽带账号。");
                return;
            }

            if (_chkRemember.Checked && string.IsNullOrEmpty(_inpPass.Text))
            {
                Warn("勾选了「记住密码」但密码为空。");
                return;
            }

            // 重连间隔：留空或非法则回到默认值，并限制在 1 ~ 3600 秒
            int interval = AppSettings.DefaultReconnectIntervalSeconds;
            if (int.TryParse(_inpInterval.Text.Trim(), out int parsed) && parsed > 0)
                interval = AppSettings.ClampReconnectInterval(parsed);

            _settings.ConnectionName = conn;
            _settings.Username = _inpUser.Text.Trim();
            _settings.RememberPassword = _chkRemember.Checked;
            _settings.Password = _chkRemember.Checked ? _inpPass.Text : string.Empty;
            _settings.AutoDialOnStart = _chkAutoDial.Checked;
            _settings.StartWithWindows = _chkAutoStart.Checked;
            _settings.ReconnectIntervalSeconds = interval;
            _inpInterval.Text = interval.ToString();

            try
            {
                StartupManager.SetStartup(_settings.StartWithWindows);
                _settings.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存设置失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void Warn(string message)
        {
            MessageBox.Show(this, message, "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
