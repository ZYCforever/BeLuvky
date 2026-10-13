# BetterWebLauncher

Windows 宽带（PPPoE）自动拨号 + 断线自动重连小工具，采用 VS Code 风格深色界面。

程序启动后自动调用系统 `rasdial` 拨号；运行期间每秒检测一次网络接口状态，
一旦掉线就按设定的间隔自动重拨，并把日志写到界面上。除了拨号本身，
还带一个整机 / 本程序的资源占用监视面板，以及内置的 **网站访问加速**
（把 Steamcommunity 302 的能力直接做进了程序，不需要另装工具）。

---

## 功能特性

| 功能 | 说明 |
| --- | --- |
| 自动拨号 | 启动即拨号，连接名 / 账号 / 密码可配置 |
| 断线自动重连 | 每秒检测接口状态，掉线后按设定间隔重试（1–3600 秒可调） |
| 网站访问加速 | 内置本地代理，加速 GitHub / Discord 等线路绕远的站点 |
| 记住密码 | 密码 AES 加密后存入本地配置，不落明文 |
| 在线状态显示 | 实时显示连接状态、IP 地址、收发流量 |
| 系统资源监控 | 整机 CPU / 内存占用、本程序 CPU / 内存 / 线程数 |
| 开机自启 | 写入 `HKCU\...\Run`，仅当前用户 |
| 托盘常驻 | 关闭窗口后最小化到托盘，继续后台监控与重连 |
| 深色主题 | 自绘控件库，VS Code 配色 |

---

## 网站访问加速

内置了 Steamcommunity 302 的核心能力，**不需要再单独运行 302 工具**。

与 302 相同的三步做法：

1. 把目标域名写进 `hosts` 指向 `127.0.0.1`；
2. 程序自己在 `443` 端口接住这些请求，用本地 CA 现场签发对应域名的证书完成 TLS；
3. 按域名挑一条当前能通的线路转发出去（直连边缘 IP + SNI 前置，或走加速中继）。

覆盖范围（规则见 `AccelRules.cs`，全部对照 Steamcommunity 302 v15 的实际配置）：

| 服务 | 覆盖的域名 |
| --- | --- |
| GitHub | `github.com`、`api.github.com`、`codeload`、`raw/avatars/objects.githubusercontent.com`、`*.github.io`、`github.githubassets.com` 等 |
| Discord | `discord.com`、`discordapp.com`、`*.discord.gg`、`cdn/media/images-ext.discordapp.net`、`dl/dl2.discordapp.net`、各状态与支持站点 |

只用**宿主文件 + 本地代理**这一种机制，没有驱动、没有系统服务、没有常驻后台进程：

- 关闭开关会立即删除自己在 `hosts` 里写的全部条目（只删带 `#BWL` 后缀的，
  以及自己 `BEGIN/END` 标记之间的内容），不会留下残留；
- 程序退出时同样会自动清理并停止监听。

### 首次使用

因为要在本地解密 HTTPS，需要把程序的自签根证书装进
「受信任的根证书颁发机构」。点界面上的 **安装根证书** 即可，
也可以随时在 `certmgr.msc` 里删除。

程序需要以**管理员身份**运行（改写 `hosts` 与监听 443 端口），
`app.manifest` 里已声明 `requireAdministrator`，双击时会自动弹 UAC。

> 说明：这里只加速 GitHub、Discord 这类并没有主动封锁国内访问、只是线路绕远的站点；
> 已被对方主动限制的服务不在本功能范围内。

---

## 下载与运行

现成可执行文件（自包含单文件，**无需安装 .NET 运行时**）：

```
bin/Release/net8.0-windows/win-x64/publish/BetterWebLauncher.exe
```

> 直接双击运行即可。首次运行会弹出登录窗口填写宽带账号密码。

**环境要求**：Windows x64。程序通过系统自带的 `rasdial` 命令拨号，
因此需要系统中已经建好「宽带连接」（或其它 PPPoE 连接）。

---

## 从源码构建

需要 .NET 8 SDK：

```powershell
# 发布自包含单文件 exe（输出到 bin\Release\net8.0-windows\win-x64\publish\）
dotnet publish -c Release
```

项目已启用 `PublishSingleFile`、`SelfContained`、`EnableCompressionInSingleFile`，
产物是一个压缩过的独立 exe。

---

## 配置文件位置

```
%APPDATA%\BetterWebLauncher\settings.json
```

可配置项（对应 `AppSettings.cs`）：

| 字段 | 默认值 | 含义 |
| --- | --- | --- |
| `ConnectionName` | `宽带连接` | 宽带连接名称 |
| `Username` / `PasswordCipher` | 空 | 账号 / 加密后的密码 |
| `RememberPassword` | `true` | 记住密码 |
| `AutoDialOnStart` | `true` | 启动时自动拨号 |
| `AutoReconnect` | `true` | 断线自动重连 |
| `StartWithWindows` | `false` | 开机自启 |
| `ReconnectIntervalSeconds` | `5` | 重连重试间隔（秒） |
| `AccelerationEnabled` | `false` | 网站访问加速总开关 |
| `AccelGitHub` | `true` | 加速 GitHub 系列域名 |
| `AccelDiscord` | `true` | 加速 Discord 系列域名 |
| `AccelPort` | `443` | 本地代理监听端口（hosts 只能指向 443，一般不改） |
| `AccelCaTrusted` | `false` | 本地根证书是否已装进受信任根 |

`RememberPassword` + `AutoDialOnStart` 且账号密码均非空时，
程序会跳过登录窗口直接进入主界面（见 `Program.cs`）。

---

## 项目结构

| 文件 | 作用 |
| --- | --- |
| `Program.cs` | 入口：DPI / 视觉样式初始化、自动登录分支 |
| `LoginForm.cs` | 登录窗口：填写连接名、账号、密码 |
| `MainForm.cs` | 主界面：拨号控制、托盘、每秒监控与自动重连 |
| `Dialer.cs` | `rasdial` 封装，含常见错误码中文翻译 |
| `NetworkMonitor.cs` | 用 `NetworkInterface` 判断 PPPoE 是否在线，读取 IP 与流量 |
| `SystemMonitor.cs` | 用 `GetSystemTimes` / `GlobalMemoryStatusEx` 采样 CPU、内存 |
| `AppSettings.cs` | 配置读写 + 密码 AES 加解密 |
| `AccelRules.cs` | 加速规则表：域名 → 上游线路 / SNI / Host 改写 |
| `AccelEngine.cs` | 本地加速代理：443 监听、SNI 分流、原始字节转发 |
| `AccelCertificates.cs` | 本地 CA 与按需签发叶证书（对应 Caddy 的 internal issuer） |
| `AccelUpstream.cs` | 上游连接：指定 IP/SNI 建 TLS，绕过被污染的解析结果 |
| `AccelTls.cs` | ClientHello 解析（读 SNI）、加速日志与统计 |
| `HostsFile.cs` | 带标记地改写 `hosts`，只动自己的条目并即时刷新 DNS 缓存 |
| `AccelSettingsForm.cs` | 加速服务选择窗口（GitHub / Discord 分开开关） |
| `StartupManager.cs` | 通过 `HKCU` 的 `Run` 项实现开机自启 |
| `PerfForm.cs` | 性能监视窗口（整机 + 本程序资源曲线） |
| `VsTheme.cs` | VS Code 风格配色与主题常量 |
| `VsControls.cs` | 自绘控件库（按钮、输入框、卡片、状态栏、迷你折线图等） |
| `WindowEffects.cs` | 窗口特效：暗色标题栏、圆角、亚克力、深色滚动条 |
| `AppIcon.cs` | 从嵌入资源加载应用图标 |

---

## 安全说明

密码不以明文保存：使用 AES 加密后以 Base64 存入 `settings.json`，
密钥由 `PBKDF2-SHA256(机器名 | 用户名, 固定盐, 50000 次)` 派生 32 字节。

这意味着：

- 配置**无法跨机器或跨用户解密**——换电脑或换 Windows 账户后需要重新输入密码；
- 由于盐是硬编码的，这属于「避免明文落盘」的防护级别，**不是**强加密保险箱。
  请勿把 `settings.json` 上传到任何公开位置。

---

## 常见拨号错误码

程序会自动把 `rasdial` 的返回码翻译成中文提示（见 `Dialer.cs`）：

| 代码 | 含义 |
| --- | --- |
| `691` | 身份验证失败：用户名或密码错误 |
| `678` | 服务器无响应：检查网线、光猫或宽带线路 |
| `651` | 调制解调器报告错误，设备无响应 |
| `623` | 找不到电话簿条目：连接名称可能不正确 |
| `629` | 连接被远程计算机断开 |
| `720` | PPP 协商失败，可能是协议或驱动问题 |
| `619` / `692` / `868` | 端口已断开 / 硬件故障 / 找不到域名 |
| `813` | 连接已存在（视为拨号成功） |

---

## 说明

- 版本管理：`bin/`、`obj/`、`_backup_before_ui_update/` 已在 `.gitignore` 中忽略，
  仓库内保留的唯一二进制是上面那个已发布的 Release exe。
- 本项目未附带开源许可证文件；如需他人复用请先补充 `LICENSE`。
