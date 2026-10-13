using System;
using System.Drawing;
using System.Windows.Forms;

namespace BetterWebLauncher
{
    /// <summary>
    /// 网站访问加速的服务选择窗口。
    ///
    /// 只开放「加速哪些站点」这一个选择：GitHub / Steam / Twitch 各一组。
    /// 具体线路、域名、负载均衡参数都已经固化在 <see cref="AccelRules"/> 里，
    /// 没有必要（也不应该）让用户手工填。
    /// </summary>
    public sealed class AccelSettingsForm : Form
    {
        private readonly AppSettings _settings;

        private static readonly Color Surface = VsTheme.EditorBg;
        private static readonly Color CardSurface = VsTheme.CardBg;

        private VsCheckBox _chkGitHub = null!;
        private VsCheckBox _chkSteam = null!;
        private VsCheckBox _chkTwitch = null!;
        private VsButton _btnOk = null!;
        private VsButton _btnCancel = null!;

        public AccelSettingsForm(AppSettings settings)
        {
            _settings = settings;

            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;

            BuildUi();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowEffects.ApplyAll(Handle, VsTheme.EditorBg, WindowEffects.AcrylicOpacity, darkTitleBar: true);
            Layout += (_, _) => Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using var brush = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        private void BuildUi()
        {
            Text = "BetterWebLauncher · 加速设置";
            ClientSize = new Size(620, 470);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = VsTheme.Ui();
            BackColor = Surface;
            Icon = AppIcon.Large;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(22, 16, 22, 14),
                BackColor = Surface,
                Margin = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 标题
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));   // 说明
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));  // 服务卡片
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));   // 证书区
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 提示
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));   // 按钮

            root.Controls.Add(new VsLabel
            {
                Text = "网站访问加速",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(13F, FontStyle.Bold),
                ForeColor = VsTheme.TextBright,
                BackColor = Surface,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            }, 0, 0);

            root.Controls.Add(new VsLabel
            {
                Text = "把线路绕远的站点交给本机代理转发，不改变浏览器设置，也不会影响其他程序。",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(9F),
                ForeColor = VsTheme.TextMuted,
                BackColor = Surface,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(1, 2, 0, 0)
            }, 0, 1);

            // ---- 服务选择卡片 ----
            var card = new VsCard
            {
                Dock = DockStyle.Fill,
                BackColor = CardSurface,
                Margin = new Padding(0, 0, 0, 10)
            };

            var cardRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = CardSurface,
                Padding = new Padding(16, 8, 16, 8),
                Margin = new Padding(0)
            };
            for (int i = 0; i < 3; i++)
                cardRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / 3F));

            _chkGitHub = new VsCheckBox
            {
                Text = "GitHub（主站 / API / 源码下载 / 头像与附件 / Pages）",
                Dock = DockStyle.Fill,
                Checked = _settings.AccelGitHub,
                BackColor = CardSurface,
                Margin = new Padding(0)
            };

            _chkSteam = new VsCheckBox
            {
                Text = "Steam（商店 / 登录 / 帮助 / 社区 / 创意工坊）",
                Dock = DockStyle.Fill,
                Checked = _settings.AccelSteam,
                BackColor = CardSurface,
                Margin = new Padding(0)
            };

            _chkTwitch = new VsCheckBox
            {
                Text = "Twitch（主站 / 播放器 / 直播流 / 视频点播）",
                Dock = DockStyle.Fill,
                Checked = _settings.AccelTwitch,
                BackColor = CardSurface,
                Margin = new Padding(0)
            };

            cardRow.Controls.Add(_chkGitHub, 0, 0);
            cardRow.Controls.Add(_chkSteam, 0, 1);
            cardRow.Controls.Add(_chkTwitch, 0, 2);
            card.Controls.Add(cardRow);
            root.Controls.Add(card, 0, 2);

            // ---- 证书说明 ----
            var certCard = new VsCard
            {
                Dock = DockStyle.Fill,
                BackColor = CardSurface,
                Margin = new Padding(0, 0, 0, 10)
            };

            var certLabel = new VsLabel
            {
                Text = "本地代理需要用自签证书解密 HTTPS，因此需要把 BetterWebLauncher 的根证书\r\n" +
                       "装进「受信任的根证书颁发机构」。证书只在本机使用，随时可以在\r\n" +
                       "certmgr.msc → 受信任的根证书颁发机构 → 证书 里删除。\r\n" +
                       $"当前状态：{(_settings.AccelCaTrusted ? "已安装" : "未安装（首次启用时会提示安装）")}",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(9F),
                ForeColor = VsTheme.TextMuted,
                BackColor = CardSurface,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };
            certCard.Controls.Add(certLabel);
            root.Controls.Add(certCard, 0, 3);

            root.Controls.Add(new VsLabel
            {
                Text = "说明：这里只加速 GitHub、Steam、Twitch 这类没有主动封锁国内访问、只是线路绕远的站点；\r\n" +
                       "已被对方主动限制的服务不在本功能范围内。关闭开关会立即清除全部 hosts 条目。",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(8.5F),
                ForeColor = VsTheme.TextMuted,
                BackColor = Surface,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(1, 4, 0, 0)
            }, 0, 4);

            // ---- 按钮 ----
            var buttons = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Surface,
                Margin = new Padding(0)
            };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

            _btnCancel = new VsButton
            {
                Text = "取消",
                Kind = VsButtonKind.Secondary,
                Dock = DockStyle.Fill,
                BackColor = Surface,
                Margin = new Padding(0, 4, 8, 4)
            };
            _btnCancel.Click += (_, _) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            _btnOk = new VsButton
            {
                Text = "保存",
                Kind = VsButtonKind.Primary,
                Dock = DockStyle.Fill,
                BackColor = Surface,
                Margin = new Padding(0, 4, 0, 4)
            };
            _btnOk.Click += (_, _) => Save();

            buttons.Controls.Add(_btnCancel, 1, 0);
            buttons.Controls.Add(_btnOk, 2, 0);
            root.Controls.Add(buttons, 0, 5);

            Controls.Add(root);
            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }

        private void Save()
        {
            if (!_chkGitHub.Checked && !_chkSteam.Checked && !_chkTwitch.Checked)
            {
                MessageBox.Show(this, "至少要选择一项要加速的服务。", "加速设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _settings.AccelGitHub = _chkGitHub.Checked;
            _settings.AccelSteam = _chkSteam.Checked;
            _settings.AccelDiscord = false;   // 暂不启用
            _settings.AccelTwitch = _chkTwitch.Checked;

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
