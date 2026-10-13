using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BetterWebLauncher
{
    public sealed class MainForm : Form
    {
        private readonly AppSettings _settings;

        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
        private readonly NotifyIcon _trayIcon = new();

        // ---------- 自动重连策略参数 ----------
        private const int ReconnectMaxAttempts = 0;   // 0 = 不限次数
        private const int ReconnectSettleMs = 1500;   // 断开旧连接后等待链路清理的时间（毫秒）

        /// <summary>当前生效的重连间隔（秒），来自设置项。</summary>
        private int ReconnectIntervalSeconds => AppSettings.ClampReconnectInterval(_settings.ReconnectIntervalSeconds);

        // ---------- 日志级别 ----------
        private enum LogLevel { Info, Success, Warn, Error }

        // 两个面：窗口面（EditorBg）与卡片面（CardBg）
        private static readonly Color Surface = VsTheme.EditorBg;
        private static readonly Color CardSurface = VsTheme.CardBg;

        // ---------- 自动重连状态 ----------
        private bool _everConnected;
        private bool _userHangup;
        private int _reconnectAttempt;
        private DateTime _nextReconnectAt = DateTime.MinValue;

        // ---------- 界面控件 ----------
        private VsLabel _lblStatus = null!;
        private VsLabel _lblConnNameValue = null!;
        private VsLabel _lblIpValue = null!;
        private VsLabel _lblDurationValue = null!;
        private VsLabel _lblUpSpeedValue = null!;
        private VsLabel _lblDownSpeedValue = null!;
        private VsLabel _lblUpTotalValue = null!;
        private VsLabel _lblDownTotalValue = null!;
        private VsLabel _lblReconnectValue = null!;
        private VsButton _btnDial = null!;
        private VsButton _btnHangup = null!;
        private VsButton _btnSettings = null!;
        private VsButton _btnPerf = null!;
        private VsCheckBox _chkAutoReconnect = null!;
        private VsLabel _lblIntervalValue = null!;
        private RichTextBox _logBox = null!;
        private VsStatusBar _statusBar = null!;

        /// <summary>性能监视窗口（非模态，单例）。</summary>
        private PerfForm? _perfForm;

        // ---------- 网站访问加速 ----------
        private VsCheckBox _chkAcceleration = null!;
        private VsLabel _lblAccelState = null!;
        private VsButton _btnAccelCert = null!;
        private AccelEngine? _accel;
        private CertificateAuthority? _ca;
        private bool _accelBusy;
        private ToolStripMenuItem? _trayAccel;

        private bool _connected;
        private bool _busy;
        private bool _reallyExit;
        private bool _trayTipShown;

        private DateTime? _connectedSince;
        private DateTime _lastSampleTime = DateTime.MinValue;
        private long _lastBytesSent;
        private long _lastBytesReceived;
        private double _upSpeed;
        private double _downSpeed;

        public MainForm(AppSettings settings)
        {
            _settings = settings;

            // 完全关闭自动缩放，所有像素由代码精确控制
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;

            BuildUi();
            BuildTray();

            _timer.Tick += OnTick;
            _timer.Start();
        }

        // ------------------------------------------------------------------
        // 窗口视觉效果：亚克力毛玻璃 + 深色标题栏 + DWM 圆角
        // ------------------------------------------------------------------
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            WindowEffects.ApplyAll(Handle, VsTheme.EditorBg, WindowEffects.AcrylicOpacity, darkTitleBar: true);

            if (_logBox != null && _logBox.IsHandleCreated)
                WindowEffects.ApplyDarkScrollBars(_logBox.Handle);
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
            Text = "BetterWebLauncher · 宽带自动拨号助手";
            ClientSize = new Size(880, 726);
            StartPosition = FormStartPosition.CenterScreen;
            Font = VsTheme.Ui();
            BackColor = Surface;
            Icon = AppIcon.Large;

            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            MinimumSize = SizeFromClientSize(new Size(780, 640));

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(20, 10, 20, 10),
                BackColor = Surface,
                Margin = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));   // 标题
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));  // 连接信息卡片
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));   // 网站加速
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));   // 操作按钮
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 日志
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // 状态栏

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildInfoCard(), 0, 1);
            root.Controls.Add(BuildAccelCard(), 0, 2);
            root.Controls.Add(BuildActionBar(), 0, 3);
            root.Controls.Add(BuildLogPanel(), 0, 4);
            root.Controls.Add(BuildStatusBar(), 0, 5);

            Controls.Add(root);
        }

        // ---- 顶部标题区 ----
        private Control BuildHeader()
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2,
                BackColor = Surface,
                Margin = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
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
                Text = "宽带自动拨号助手 · 断线自动重连",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(9F),
                ForeColor = VsTheme.TextMuted,
                BackColor = Surface,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(1, 2, 0, 0)
            }, 1, 1);

            var connName = new VsLabel
            {
                Text = _settings.ConnectionName,
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(9.75F, FontStyle.Bold),
                ForeColor = VsTheme.TextPrimary,
                BackColor = Surface,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(0)
            };
            header.Controls.Add(connName, 2, 0);
            header.SetRowSpan(connName, 2);

            return header;
        }

        // ---- 连接信息卡片 ----
        private Control BuildInfoCard()
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
                ColumnCount = 1,
                RowCount = 3,
                BackColor = CardSurface,
                Margin = new Padding(0)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // 状态大字
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));   // 分隔线
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 信息网格

            // 状态行
            var statusRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = CardSurface,
                Margin = new Padding(0)
            };
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));

            _lblStatus = new VsLabel
            {
                Text = "● 未连接",
                ForeColor = VsTheme.Error,
                Font = VsTheme.Ui(15F, FontStyle.Bold),
                Dock = DockStyle.Fill,
                BackColor = CardSurface,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };
            statusRow.Controls.Add(_lblStatus, 0, 0);

            statusRow.Controls.Add(new VsLabel
            {
                Text = "在线监测中 · 每秒刷新",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(8.5F),
                ForeColor = VsTheme.TextMuted,
                BackColor = CardSurface,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(0)
            }, 1, 0);

            t.Controls.Add(statusRow, 0, 0);

            var separator = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = VsTheme.BorderSubtle,
                Margin = new Padding(0)
            };
            t.Controls.Add(separator, 0, 1);

            // 信息网格：4 列 × 5 行
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 5,
                BackColor = CardSurface,
                Padding = new Padding(0, 6, 0, 0),
                Margin = new Padding(0)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 5; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 20));

            _lblConnNameValue = Cell("", bold: true);
            _lblIpValue = Cell("-", bold: true, color: VsTheme.Info);
            _lblDurationValue = Cell("--", bold: true);
            _lblUpSpeedValue = Cell("0 B/s", bold: true, color: VsTheme.Info);
            _lblDownSpeedValue = Cell("0 B/s", bold: true, color: VsTheme.Teal);
            _lblUpTotalValue = Cell("0 B", bold: true);
            _lblDownTotalValue = Cell("0 B", bold: true);
            _lblReconnectValue = Cell("就绪", bold: true, color: VsTheme.TextMuted);

            grid.Controls.Add(Cell("连接名称", false, muted: true), 0, 0);
            grid.Controls.Add(_lblConnNameValue, 1, 0);
            grid.Controls.Add(Cell("IP 地址", false, muted: true), 2, 0);
            grid.Controls.Add(_lblIpValue, 3, 0);

            grid.Controls.Add(Cell("连接时长", false, muted: true), 0, 1);
            grid.Controls.Add(_lblDurationValue, 1, 1);
            grid.Controls.Add(Cell("连接状态", false, muted: true), 2, 1);
            grid.Controls.Add(Cell("在线监测中", bold: true, color: VsTheme.Ok), 3, 1);

            grid.Controls.Add(Cell("上传速率", false, muted: true), 0, 2);
            grid.Controls.Add(_lblUpSpeedValue, 1, 2);
            grid.Controls.Add(Cell("下载速率", false, muted: true), 2, 2);
            grid.Controls.Add(_lblDownSpeedValue, 3, 2);

            grid.Controls.Add(Cell("上传总量", false, muted: true), 0, 3);
            grid.Controls.Add(_lblUpTotalValue, 1, 3);
            grid.Controls.Add(Cell("下载总量", false, muted: true), 2, 3);
            grid.Controls.Add(_lblDownTotalValue, 3, 3);

            grid.Controls.Add(Cell("重连状态", false, muted: true), 0, 4);
            grid.Controls.Add(_lblReconnectValue, 1, 4);
            grid.Controls.Add(Cell("重连间隔", false, muted: true), 2, 4);
            _lblIntervalValue = Cell(ReconnectIntervalSeconds + " 秒", bold: true);
            grid.Controls.Add(_lblIntervalValue, 3, 4);

            t.Controls.Add(grid, 0, 2);
            card.Controls.Add(t);
            return card;
        }

        private static VsLabel Cell(string text, bool bold, Color? color = null, bool muted = false) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 0, 8, 0),
            BackColor = CardSurface,
            Font = VsTheme.Ui(bold ? 9.75F : 9F, bold ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = color ?? (muted ? VsTheme.TextMuted : VsTheme.TextPrimary)
        };

        // ---- 操作按钮行 ----
        private Control BuildActionBar()
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 6,
                RowCount = 1,
                BackColor = Surface,
                Margin = new Padding(0)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));

            _btnDial = new VsButton
            {
                Text = "立即拨号",
                Kind = VsButtonKind.Primary,
                Dock = DockStyle.Fill,
                BackColor = Surface,
                Margin = new Padding(0, 6, 8, 6)
            };
            _btnDial.Click += async (_, _) =>
            {
                _userHangup = false;
                _reconnectAttempt = 0;
                await DialAsync();
            };

            _btnHangup = new VsButton
            {
                Text = "断开连接",
                Kind = VsButtonKind.Danger,
                Dock = DockStyle.Fill,
                BackColor = Surface,
                Margin = new Padding(0, 6, 8, 6)
            };
            _btnHangup.Click += async (_, _) => await HangupAsync();

            _btnSettings = new VsButton
            {
                Text = "设置…",
                Kind = VsButtonKind.Secondary,
                Dock = DockStyle.Fill,
                BackColor = Surface,
                Margin = new Padding(0, 6, 8, 6)
            };
            _btnSettings.Click += OnSettingsClick;

            _btnPerf = new VsButton
            {
                Text = "性能…",
                Kind = VsButtonKind.Secondary,
                Dock = DockStyle.Fill,
                BackColor = Surface,
                Margin = new Padding(0, 6, 8, 6)
            };
            _btnPerf.Click += (_, _) => ShowPerfWindow();

            _chkAutoReconnect = new VsCheckBox
            {
                Text = "断线自动重连",
                Dock = DockStyle.Fill,
                Checked = _settings.AutoReconnect,
                BackColor = Surface,
                Margin = new Padding(0, 8, 0, 6)
            };
            _chkAutoReconnect.CheckedChanged += OnAutoReconnectChanged;

            row.Controls.Add(_btnDial, 0, 0);
            row.Controls.Add(_btnHangup, 1, 0);
            row.Controls.Add(_btnSettings, 2, 0);
            row.Controls.Add(_btnPerf, 3, 0);
            row.Controls.Add(_chkAutoReconnect, 5, 0);
            return row;
        }

        // ---- 网站访问加速 ----
        private Control BuildAccelCard()
        {
            var card = new VsCard
            {
                Dock = DockStyle.Fill,
                BackColor = CardSurface,
                Margin = new Padding(0, 0, 0, 14)
            };

            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 1,
                BackColor = CardSurface,
                Padding = new Padding(16, 0, 16, 0),
                Margin = new Padding(0)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 208));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));

            _chkAcceleration = new VsCheckBox
            {
                Text = "网站访问加速 (GitHub / Discord)",
                Dock = DockStyle.Fill,
                Checked = _settings.AccelerationEnabled,
                BackColor = CardSurface,
                Margin = new Padding(0, 0, 8, 0)
            };
            _chkAcceleration.CheckedChanged += async (_, _) => await ToggleAccelerationAsync();

            _lblAccelState = new VsLabel
            {
                Text = "未启用",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(9F),
                ForeColor = VsTheme.TextMuted,
                BackColor = CardSurface,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(8, 0, 8, 0)
            };

            _btnAccelCert = new VsButton
            {
                Text = "安装根证书",
                Kind = VsButtonKind.Secondary,
                Dock = DockStyle.Fill,
                BackColor = CardSurface,
                Margin = new Padding(0, 7, 8, 7)
            };
            _btnAccelCert.Click += (_, _) => InstallRootCertificate();

            var btnDetails = new VsButton
            {
                Text = "加速设置…",
                Kind = VsButtonKind.Secondary,
                Dock = DockStyle.Fill,
                BackColor = CardSurface,
                Margin = new Padding(0, 7, 0, 7)
            };
            btnDetails.Click += (_, _) => EditAccelServices();

            row.Controls.Add(_chkAcceleration, 0, 0);
            row.Controls.Add(_lblAccelState, 1, 0);
            row.Controls.Add(_btnAccelCert, 2, 0);
            row.Controls.Add(btnDetails, 3, 0);
            card.Controls.Add(row);

            return card;
        }

        // ------------------------------------------------------------------
        // 网站访问加速（GitHub / Discord）
        //
        // 做法：
        //   1. hosts 把目标域名指到 127.0.0.1；
        //   2. 本进程在 443 端口接住请求，用本地 CA 现场签发证书；
        //   3. 按域名选一条当前可用的上游线路转发出去（国内外均可直连的站点才这么做）。
        // 没有额外的驱动、没有常驻服务，关掉开关就把 hosts 恢复原样。
        // ------------------------------------------------------------------
        private async Task ToggleAccelerationAsync()
        {
            if (_accelBusy) return;
            _accelBusy = true;

            _chkAcceleration.Enabled = false;
            try
            {
                if (_chkAcceleration.Checked)
                    await StartAccelerationAsync();
                else
                    await StopAccelerationAsync();
            }
            finally
            {
                _chkAcceleration.Enabled = true;
                _accelBusy = false;
            }
        }

        private async Task StartAccelerationAsync()
        {
            if (_accel != null) return;

            // 写 hosts 需要管理员权限，先确认能写再启动监听，避免出现半开状态
            if (!HostsFile.CanWrite())
            {
                Log("无法修改 hosts 文件：请用管理员身份重新运行本程序。", LogLevel.Error);
                _chkAcceleration.Checked = false;
                UpdateAccelState();
                return;
            }

            try
            {
                _ca ??= CertificateAuthority.LoadOrCreate();

                _accel = new AccelEngine(_ca);
                _accel.SetRules(SelectedAccelRules());
                _accel.Log += OnAccelLog;
                _accel.Start(_settings.AccelPort);

                var domains = SelectedAccelDomains();
                var written = HostsFile.Apply(domains);

                _settings.AccelerationEnabled = true;
                try { _settings.Save(); } catch { }

                Log($"网站访问加速已启动：{written} 个域名 → 127.0.0.1:{_settings.AccelPort}" +
                    $"（{(_settings.AccelGitHub ? "GitHub" : "")}{(_settings.AccelGitHub && _settings.AccelDiscord ? " + " : "")}{(_settings.AccelDiscord ? "Discord" : "")}）。",
                    LogLevel.Success);
            }
            catch (Exception ex)
            {
                Log("启动网站加速失败：" + ex.Message, LogLevel.Error);
                await StopAccelerationAsync();
                _chkAcceleration.Checked = false;
            }

            UpdateAccelState();
        }

        private async Task StopAccelerationAsync()
        {
            try
            {
                if (_accel != null)
                {
                    _accel.Log -= OnAccelLog;
                    await _accel.StopAsync();
                    _accel.Dispose();
                    _accel = null;
                }

                HostsFile.Remove();
            }
            catch (Exception ex)
            {
                Log("关闭网站加速时出错：" + ex.Message, LogLevel.Warn);
            }

            _settings.AccelerationEnabled = false;
            try { _settings.Save(); } catch { }

            Log("网站访问加速已停止，hosts 已恢复原样。");
            UpdateAccelState();
        }

        /// <summary>按当前勾选的服务拼出规则列表。</summary>
        private AccelRule[] SelectedAccelRules()
        {
            var list = new System.Collections.Generic.List<AccelRule>();
            foreach (var rule in AccelRules.All)
            {
                if (rule.Group == "GitHub" && !_settings.AccelGitHub) continue;
                if (rule.Group == "Discord" && !_settings.AccelDiscord) continue;
                list.Add(rule);
            }

            return list.ToArray();
        }

        private System.Collections.Generic.List<string> SelectedAccelDomains()
        {
            var list = new System.Collections.Generic.List<string>();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in SelectedAccelRules())
            {
                if (!rule.EnableHosts) continue;
                foreach (var domain in rule.Domains)
                {
                    var d = domain.Trim().ToLowerInvariant();
                    if (d.Length == 0) continue;
                    if (d.StartsWith("*.", StringComparison.Ordinal)) continue;
                    if (seen.Add(d)) list.Add(d);
                }
            }

            return list;
        }

        private void UpdateAccelState()
        {
            if (_lblAccelState == null) return;

            if (_accel is { IsRunning: true })
            {
                var stats = _accel.Stats;
                _lblAccelState.Text = $"运行中 · {HostsFile.CountApplied()} 个域名 · " +
                                      $"{stats.Total} 次请求";
                _lblAccelState.ForeColor = VsTheme.Accent;
            }
            else
            {
                _lblAccelState.Text = "未启用";
                _lblAccelState.ForeColor = VsTheme.TextMuted;
            }

            if (_btnAccelCert != null && _ca != null)
            {
                bool trusted = _ca.IsTrustedForCurrentUser();
                _btnAccelCert.Text = trusted ? "根证书已安装" : "安装根证书";
                _btnAccelCert.Enabled = !trusted;
            }

            if (_trayAccel != null)
                _trayAccel.Checked = _accel is { IsRunning: true };
        }

        private void InstallRootCertificate()
        {
            try
            {
                _ca ??= CertificateAuthority.LoadOrCreate();
                _ca.TrustForCurrentUser();

                _settings.AccelCaTrusted = _ca.IsTrustedForCurrentUser();
                try { _settings.Save(); } catch { }

                if (_settings.AccelCaTrusted)
                {
                    Log("本地根证书已安装到「受信任的根证书颁发机构」，浏览器不会再报证书错误。", LogLevel.Success);
                }
                else
                {
                    var file = _ca.ExportRootToFile();
                    Log($"自动安装未成功，已导出证书到 {file}，请双击后选择「安装证书 → 受信任的根证书颁发机构」。", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                Log("安装根证书失败：" + ex.Message, LogLevel.Error);
            }

            UpdateAccelState();
        }

        /// <summary>打开加速服务选择对话框（GitHub / Discord 分开开关）。</summary>
        private void EditAccelServices()
        {
            using var dlg = new AccelSettingsForm(_settings);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try { _settings.Save(); } catch { }

            if (_accel is { IsRunning: true })
            {
                // 服务范围变了：重建 hosts 与规则
                _accel.SetRules(SelectedAccelRules());
                HostsFile.Apply(SelectedAccelDomains());
                Log($"加速范围已更新：{HostsFile.CountApplied()} 个域名。");
            }

            UpdateAccelState();
        }

        private void OnAccelLog(AccelLogEntry entry)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => OnAccelLog(entry))); } catch { }
                return;
            }

            if (!entry.Success && !string.IsNullOrEmpty(entry.Error))
                Log($"加速 {entry.Host} 失败：{entry.Error}", LogLevel.Warn);

            UpdateAccelState();
        }

        // ---- 性能监视窗口 ----
        private void ShowPerfWindow()
        {
            if (_perfForm == null || _perfForm.IsDisposed)
            {
                _perfForm = new PerfForm();

                // 放在主窗口右侧（放不下就放在左侧），避免两个窗口完全重叠
                var wa = Screen.FromControl(this).WorkingArea;
                int x = Right + 12;
                if (x + _perfForm.Width > wa.Right) x = Math.Max(wa.Left, wa.Right - _perfForm.Width - 12);
                if (x < wa.Left) x = wa.Left;
                int y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - _perfForm.Height - 12));

                _perfForm.StartPosition = FormStartPosition.Manual;
                _perfForm.Location = new Point(x, y);
            }

            if (!_perfForm.Visible) _perfForm.Show();
            if (_perfForm.WindowState == FormWindowState.Minimized)
                _perfForm.WindowState = FormWindowState.Normal;

            _perfForm.Activate();
            _perfForm.BringToFront();
        }

        // ---- 日志面板 ----
        private Control BuildLogPanel()
        {
            var card = new VsCard
            {
                Dock = DockStyle.Fill,
                CornerRadius = 6,
                CardColor = CardSurface,
                Margin = new Padding(0, 6, 0, 6)
            };
            card.Padding = new Padding(12, 8, 12, 12);

            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = CardSurface,
                Margin = new Padding(0)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var head = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = CardSurface,
                Margin = new Padding(0)
            };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));

            head.Controls.Add(new VsLabel
            {
                Text = "▸",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(10F, FontStyle.Bold),
                ForeColor = VsTheme.Accent,
                BackColor = CardSurface,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            }, 0, 0);

            head.Controls.Add(new VsLabel
            {
                Text = "运行日志",
                Dock = DockStyle.Fill,
                Font = VsTheme.Ui(10F, FontStyle.Bold),
                ForeColor = VsTheme.TextPrimary,
                BackColor = CardSurface,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            }, 1, 0);

            var btnClear = new VsButton
            {
                Text = "清空",
                Kind = VsButtonKind.Ghost,
                Dock = DockStyle.Fill,
                BackColor = CardSurface,
                Margin = new Padding(4, 2, 0, 2),
                Font = VsTheme.Ui(8.5F)
            };
            btnClear.Click += (_, _) => { if (!_logBox.IsDisposed) _logBox.Clear(); };
            head.Controls.Add(btnClear, 2, 0);

            t.Controls.Add(head, 0, 0);

            var logHost = new VsCard
            {
                Dock = DockStyle.Fill,
                CornerRadius = 4,
                CardColor = VsTheme.LogBg,
                BorderColor = VsTheme.Border,
                Padding = new Padding(1),
                Margin = new Padding(0)
            };

            _logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = VsTheme.LogBg,
                ForeColor = VsTheme.LogText,
                BorderStyle = BorderStyle.None,
                Font = VsTheme.Mono(9.75F),
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = false,
                Margin = new Padding(0)
            };

            logHost.Controls.Add(_logBox);
            t.Controls.Add(logHost, 0, 1);

            card.Controls.Add(t);
            return card;
        }

        // ---- 底部状态栏 ----
        private Control BuildStatusBar()
        {
            _statusBar = new VsStatusBar
            {
                Dock = DockStyle.Fill,
                LeftText = "未连接",
                RightText = "↑ 0 B/s   ↓ 0 B/s",
                BarColor = VsTheme.SecondaryBg,
                DotColor = VsTheme.Error
            };
            return _statusBar;
        }

        // ------------------------------------------------------------------
        // 托盘
        // ------------------------------------------------------------------
        private void BuildTray()
        {
            _trayIcon.Icon = AppIcon.Small;
            _trayIcon.Text = "BetterWebLauncher";
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

            var menu = new ContextMenuStrip
            {
                BackColor = VsTheme.SidebarBg,
                ForeColor = VsTheme.TextPrimary,
                Renderer = new ToolStripProfessionalRenderer(new VsMenuColorTable()) { RoundedEdges = false },
                ShowImageMargin = false
            };

            menu.Items.Add(MenuItem("显示主界面", (_, _) => RestoreFromTray()));
            menu.Items.Add(MenuItem("性能监视…", (_, _) => ShowPerfWindow()));

            _trayAccel = MenuItem("网站加速", (_, _) =>
            {
                _chkAcceleration.Checked = !_chkAcceleration.Checked;
            });
            _trayAccel.CheckOnClick = false;
            _trayAccel.Checked = _settings.AccelerationEnabled;
            menu.Items.Add(_trayAccel);
            menu.Items.Add(MenuItem("立即拨号", async (_, _) =>
            {
                _userHangup = false;
                _reconnectAttempt = 0;
                await DialAsync();
            }));
            menu.Items.Add(MenuItem("断开连接", async (_, _) => await HangupAsync()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MenuItem("退出", (_, _) =>
            {
                _reallyExit = true;
                Close();
            }));

            _trayIcon.ContextMenuStrip = menu;
        }

        private static ToolStripMenuItem MenuItem(string text, EventHandler onClick)
            => new(text, null, onClick) { ForeColor = VsTheme.TextPrimary };

        /// <summary>托盘右键菜单的深色配色（VS Code 风格）。</summary>
        private sealed class VsMenuColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => VsTheme.SidebarBg;
            public override Color MenuItemSelected => VsTheme.Accent;
            public override Color MenuItemSelectedGradientBegin => VsTheme.Accent;
            public override Color MenuItemSelectedGradientEnd => VsTheme.Accent;
            public override Color MenuItemBorder => VsTheme.Accent;
            public override Color MenuBorder => VsTheme.Border;
            public override Color SeparatorDark => VsTheme.Border;
            public override Color SeparatorLight => VsTheme.Border;
            public override Color ImageMarginGradientBegin => VsTheme.SidebarBg;
            public override Color ImageMarginGradientMiddle => VsTheme.SidebarBg;
            public override Color ImageMarginGradientEnd => VsTheme.SidebarBg;
        }

        // ------------------------------------------------------------------
        // 生命周期
        // ------------------------------------------------------------------
        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);

            Log("BetterWebLauncher 已启动。");
            UpdateNetworkInfo();
            UpdateAccelState();

            // 上次退出时加速是开着的，这里自动恢复（hosts 可能已被系统还原，重新写入即可）
            if (_settings.AccelerationEnabled && !_chkAcceleration.Checked)
                _chkAcceleration.Checked = true;
            else if (_settings.AccelerationEnabled)
                await StartAccelerationAsync();

            if (_settings.AutoDialOnStart)
            {
                await Task.Delay(1200);
                if (!_connected && !_busy)
                    await DialAsync();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            _timer.Stop();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();

            // 退出前收尾：停掉本地代理并清掉 hosts 里我们加的条目，
            // 否则用户关了程序，浏览器还会被指向一个已经没人监听的 127.0.0.1。
            try
            {
                if (_accel != null)
                {
                    _accel.Log -= OnAccelLog;
                    _accel.StopAsync().GetAwaiter().GetResult();
                    _accel.Dispose();
                    _accel = null;
                }

                HostsFile.Remove();
            }
            catch { }

            // 退出时一并关闭性能监视窗口
            try
            {
                if (_perfForm is { IsDisposed: false }) { _perfForm.Close(); _perfForm.Dispose(); }
            }
            catch { }

            base.OnFormClosing(e);
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;

            if (!_trayTipShown)
            {
                _trayTipShown = true;
                _trayIcon.BalloonTipTitle = "BetterWebLauncher";
                _trayIcon.BalloonTipText = "程序已最小化到托盘，仍会在后台监控网络并自动重连。";
                _trayIcon.ShowBalloonTip(3000);
            }
        }

        private void RestoreFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // ------------------------------------------------------------------
        // 定时监控 + 自动重连
        // ------------------------------------------------------------------
        private async void OnTick(object? sender, EventArgs e)
        {
            try
            {
                UpdateNetworkInfo();
                await TryAutoReconnectAsync();
            }
            catch { }
        }

        private void UpdateNetworkInfo()
        {
            var info = NetworkMonitor.GetStatus(_settings.ConnectionName);

            if (info.Connected && !_connected)
            {
                _connected = true;
                _everConnected = true;
                _connectedSince = DateTime.Now;
                _lastBytesSent = info.BytesSent;
                _lastBytesReceived = info.BytesReceived;
                _lastSampleTime = DateTime.UtcNow;
                _upSpeed = 0;
                _downSpeed = 0;

                if (_reconnectAttempt > 0)
                    Log($"自动重连成功（此前累计尝试 {_reconnectAttempt} 次）。", LogLevel.Success);

                _reconnectAttempt = 0;
                _nextReconnectAt = DateTime.MinValue;
                _lblReconnectValue.Text = "就绪";
                _lblReconnectValue.ForeColor = VsTheme.TextMuted;

                SetStatus("已连接", VsTheme.Ok);
                Log($"网络已连接，IP：{info.Ip}", LogLevel.Success);
                _trayIcon.Text = "BetterWebLauncher - 已连接";
            }
            else if (!info.Connected && _connected)
            {
                _connected = false;
                _connectedSince = null;
                _upSpeed = 0;
                _downSpeed = 0;

                SetStatus("未连接", VsTheme.Error);
                Log("网络连接已断开。", LogLevel.Warn);
                _trayIcon.Text = "BetterWebLauncher - 未连接";

                if (_userHangup)
                {
                    _lblReconnectValue.Text = "已暂停（用户主动断开）";
                    _lblReconnectValue.ForeColor = VsTheme.TextMuted;
                    Log("您已主动断开连接，自动重连已暂停。");
                }
                else if (!_settings.AutoReconnect)
                {
                    _lblReconnectValue.Text = "已关闭";
                    _lblReconnectValue.ForeColor = VsTheme.TextMuted;
                    Log("自动重连处于关闭状态。");
                }
                else if (_everConnected)
                {
                    _reconnectAttempt = 0;
                    _nextReconnectAt = DateTime.UtcNow.AddSeconds(ReconnectIntervalSeconds);
                    _lblReconnectValue.Text = $"等待重连（{ReconnectIntervalSeconds} 秒后）";
                    _lblReconnectValue.ForeColor = VsTheme.Warn;
                    Log($"检测到网络异常断开，{ReconnectIntervalSeconds} 秒后开始自动重连" +
                        $"（重连前会先断开系统拨号连接）。", LogLevel.Warn);
                }
            }

            _lblConnNameValue.Text = _settings.ConnectionName;

            if (info.Connected)
            {
                var now = DateTime.UtcNow;
                double secs = (now - _lastSampleTime).TotalSeconds;

                if (secs >= 0.5)
                {
                    long dSent = info.BytesSent - _lastBytesSent;
                    long dRecv = info.BytesReceived - _lastBytesReceived;

                    if (dSent >= 0) _upSpeed = dSent / secs;
                    if (dRecv >= 0) _downSpeed = dRecv / secs;

                    _lastBytesSent = info.BytesSent;
                    _lastBytesReceived = info.BytesReceived;
                    _lastSampleTime = now;
                }

                _lblIpValue.Text = info.Ip;
                _lblUpSpeedValue.Text = FormatBytes(_upSpeed) + "/s";
                _lblDownSpeedValue.Text = FormatBytes(_downSpeed) + "/s";
                _lblUpTotalValue.Text = FormatBytes(info.BytesSent);
                _lblDownTotalValue.Text = FormatBytes(info.BytesReceived);
                _lblDurationValue.Text = _connectedSince.HasValue
                    ? FormatDuration(DateTime.Now - _connectedSince.Value)
                    : "--";

                _statusBar.LeftText = $"已连接 · {info.Ip}";
                _statusBar.DotColor = VsTheme.Ok;
                _statusBar.BarColor = VsTheme.Accent;
            }
            else
            {
                _lblIpValue.Text = "-";
                _lblUpSpeedValue.Text = "0 B/s";
                _lblDownSpeedValue.Text = "0 B/s";
                _lblUpTotalValue.Text = "0 B";
                _lblDownTotalValue.Text = "0 B";
                _lblDurationValue.Text = "--";

                _statusBar.LeftText = "未连接";
                _statusBar.DotColor = VsTheme.Error;
                _statusBar.BarColor = VsTheme.SecondaryBg;
            }

            _statusBar.RightText = $"↑ {FormatBytes(_upSpeed)}/s   ↓ {FormatBytes(_downSpeed)}/s";
            UpdateButtons();
        }

        private async Task TryAutoReconnectAsync()
        {
            if (_connected || _busy) return;
            if (!_settings.AutoReconnect) return;
            if (_userHangup) return;
            if (!_everConnected) return;
            if (DateTime.UtcNow < _nextReconnectAt) return;

            if (ReconnectMaxAttempts > 0 && _reconnectAttempt >= ReconnectMaxAttempts)
            {
                _lblReconnectValue.Text = $"已达最大重试次数（{ReconnectMaxAttempts}）";
                _lblReconnectValue.ForeColor = VsTheme.Error;
                Log($"自动重连已尝试 {_reconnectAttempt} 次仍失败，停止自动重连。可点击「立即拨号」手动重试。", LogLevel.Error);
                _nextReconnectAt = DateTime.MaxValue;
                return;
            }

            _reconnectAttempt++;

            int interval = ReconnectIntervalSeconds;

            SetStatus($"重连中 ({_reconnectAttempt})", VsTheme.Warn);
            _lblReconnectValue.Text = $"正在尝试第 {_reconnectAttempt} 次重连…";
            _lblReconnectValue.ForeColor = VsTheme.Warn;
            _trayIcon.Text = $"BetterWebLauncher - 重连中 {_reconnectAttempt}";

            _statusBar.LeftText = $"重连中（第 {_reconnectAttempt} 次）";
            _statusBar.DotColor = VsTheme.Warn;
            _statusBar.BarColor = Color.FromArgb(0x8A, 0x6D, 0x00);

            // 断线重连策略：先断开系统里的宽带连接，清理掉半死的 PPPoE 会话，再重新拨号
            Log($"开始第 {_reconnectAttempt} 次自动重连：先断开「{_settings.ConnectionName}」，再重新拨号。");
            bool ok = await ReconnectAsync();

            if (!ok)
            {
                _nextReconnectAt = DateTime.UtcNow.AddSeconds(interval);
                _lblReconnectValue.Text = $"第 {_reconnectAttempt} 次失败，{interval} 秒后重试";
                _lblReconnectValue.ForeColor = VsTheme.Warn;
                Log($"自动重连第 {_reconnectAttempt} 次失败，{interval} 秒后重试。", LogLevel.Warn);

                if (!_connected) SetStatus("未连接", VsTheme.Error);
            }
        }

        private void SetStatus(string text, Color color)
        {
            _lblStatus.Text = "● " + text;
            _lblStatus.ForeColor = color;
        }

        /// <summary>刷新“重连间隔”显示。</summary>
        private void RefreshIntervalText()
        {
            if (_lblIntervalValue != null && !_lblIntervalValue.IsDisposed)
                _lblIntervalValue.Text = ReconnectIntervalSeconds + " 秒";
        }

        private void UpdateButtons()
        {
            _btnDial.Enabled = !_busy && !_connected;
            _btnHangup.Enabled = !_busy && _connected;
            _btnSettings.Enabled = !_busy;
        }

        // ------------------------------------------------------------------
        // 拨号 / 断开
        // ------------------------------------------------------------------

        /// <summary>手动 / 启动时拨号：带互斥保护。</summary>
        private async Task<bool> DialAsync()
        {
            if (_busy) return false;

            _busy = true;
            UpdateButtons();
            try
            {
                return await DialCoreAsync();
            }
            finally
            {
                _busy = false;
                UpdateButtons();
            }
        }

        /// <summary>
        /// 自动重连：先在系统里断开宽带拨号连接（清理半死的 PPPoE 会话），
        /// 等待链路释放后再重新拨号。
        /// </summary>
        private async Task<bool> ReconnectAsync()
        {
            if (_busy) return false;

            _busy = true;
            UpdateButtons();
            try
            {
                Log($"正在断开系统拨号连接「{_settings.ConnectionName}」…");
                var hang = await Task.Run(() => Dialer.Disconnect(_settings.ConnectionName));

                if (hang.Success)
                    Log("旧连接已断开。", LogLevel.Success);
                else
                    Log($"断开旧连接未成功（{hang.Message}），仍继续尝试重新拨号。", LogLevel.Warn);

                // 给系统一点时间释放 PPPoE 接口
                await Task.Delay(ReconnectSettleMs);

                return await DialCoreAsync();
            }
            finally
            {
                _busy = false;
                UpdateButtons();
            }
        }

        /// <summary>真正执行 rasdial 拨号（不含互斥保护）。</summary>
        private async Task<bool> DialCoreAsync()
        {
            if (string.IsNullOrWhiteSpace(_settings.Username) ||
                string.IsNullOrWhiteSpace(_settings.Password))
            {
                Log("尚未配置宽带账号或密码，请点击「设置…」填写。", LogLevel.Warn);
                return false;
            }

            Log($"正在拨号：{_settings.ConnectionName} …");

            var result = await Task.Run(() =>
                Dialer.Connect(_settings.ConnectionName, _settings.Username, _settings.Password));

            Log(result.Message, result.Success ? LogLevel.Success : LogLevel.Error);

            if (result.Success)
            {
                await Task.Delay(1200);
                UpdateNetworkInfo();
            }

            return result.Success && _connected;
        }

        private async Task HangupAsync()
        {
            if (_busy) return;

            _userHangup = true;
            _reconnectAttempt = 0;
            _nextReconnectAt = DateTime.MinValue;

            _busy = true;
            UpdateButtons();
            Log("正在断开连接…");

            var result = await Task.Run(() => Dialer.Disconnect(_settings.ConnectionName));
            Log(result.Success ? "已断开连接。" : result.Message, result.Success ? LogLevel.Info : LogLevel.Error);

            _busy = false;
            UpdateButtons();
            UpdateNetworkInfo();
        }

        // ------------------------------------------------------------------
        // 事件
        // ------------------------------------------------------------------
        private void OnAutoReconnectChanged(object? sender, EventArgs e)
        {
            _settings.AutoReconnect = _chkAutoReconnect.Checked;
            try { _settings.Save(); } catch { }

            if (_settings.AutoReconnect)
            {
                _userHangup = false;
                _reconnectAttempt = 0;
                _nextReconnectAt = DateTime.MinValue;
                _lblReconnectValue.Text = "就绪";
                _lblReconnectValue.ForeColor = VsTheme.TextMuted;
                Log("已开启断线自动重连。", LogLevel.Success);
            }
            else
            {
                _lblReconnectValue.Text = "已关闭";
                _lblReconnectValue.ForeColor = VsTheme.TextMuted;
                Log("已关闭断线自动重连。");
            }
        }

        private void OnSettingsClick(object? sender, EventArgs e)
        {
            using var dlg = new LoginForm(_settings);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                Log("设置已更新。");
                _chkAutoReconnect.Checked = _settings.AutoReconnect;
                RefreshIntervalText();
                UpdateNetworkInfo();

                _userHangup = false;
                _reconnectAttempt = 0;
                _nextReconnectAt = DateTime.MinValue;
            }
        }

        // ------------------------------------------------------------------
        // 日志与格式化
        // ------------------------------------------------------------------
        private void Log(string message, LogLevel level = LogLevel.Info)
        {
            if (_logBox.IsDisposed) return;

            if (_logBox.InvokeRequired)
            {
                try { _logBox.BeginInvoke(new Action(() => Log(message, level))); }
                catch { }
                return;
            }

            if (_logBox.TextLength > 40_000)
            {
                _logBox.Clear();
                AppendLine("…（日志过长，已清空历史记录）", LogLevel.Info);
            }

            AppendLine(message, level);
        }

        private void AppendLine(string message, LogLevel level)
        {
            Color color = level switch
            {
                LogLevel.Success => VsTheme.Ok,
                LogLevel.Warn    => VsTheme.Warn,
                LogLevel.Error   => VsTheme.Error,
                _                => VsTheme.LogText
            };

            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionLength = 0;
            _logBox.SelectionColor = VsTheme.Comment;
            _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] ");

            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionColor = color;
            _logBox.AppendText(message + Environment.NewLine);

            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionLength = 0;
            _logBox.ScrollToCaret();
        }

        private static string FormatBytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (bytes >= 1024 && i < units.Length - 1)
            {
                bytes /= 1024;
                i++;
            }
            return $"{bytes:0.##} {units[i]}";
        }

        private static string FormatDuration(TimeSpan ts)
        {
            return ts.TotalDays >= 1
                ? $"{(int)ts.TotalDays}天 {ts:hh\\:mm\\:ss}"
                : ts.ToString(@"hh\:mm\:ss");
        }
    }
}
