using System;
using System.Collections.Generic;

namespace BetterWebLauncher
{
    /// <summary>上游选取策略。</summary>
    public enum AccelUpstreamKind
    {
        /// <summary>直连到列表中的 IP，可选地用伪造 SNI 前置，绕过 SNI 阻断。</summary>
        DirectIp,

        /// <summary>直连到列表中的域名（加速中继 / CDN），SNI 默认用该域名自身。</summary>
        RelayDomain,
    }

    /// <summary>
    /// 一条加速规则：把若干域名映射到一组上游端点。
    ///
    /// 三个可调维度：
    ///   DirectIp     → 直连边缘 IP，并指定 SNI 前置
    ///   RelayDomain  → 走中继域名（可选改写 Host）
    ///   StripCookies → 转发时不带 Cookie（上下行都剥掉）
    ///   RedirectTo   → redir &lt;url&gt;
    /// 负载均衡与失败判定取值：dial_timeout 2s / fail_duration 30s / max_fails 2。
    /// </summary>
    public sealed class AccelRule
    {
        public string Id { get; init; } = "";

        /// <summary>界面分组名，例如 GitHub / Discord。</summary>
        public string Group { get; init; } = "";

        /// <summary>规则说明，显示在日志里。</summary>
        public string Description { get; init; } = "";

        /// <summary>该规则负责的域名。支持 *.example.com 通配写法。</summary>
        public string[] Domains { get; init; } = Array.Empty<string>();

        public AccelUpstreamKind Kind { get; init; } = AccelUpstreamKind.DirectIp;

        /// <summary>上游端点：DirectIp 时是 IP，RelayDomain 时是域名。</summary>
        public string[] Endpoints { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 连上游时使用的 TLS SNI：
        ///   null → 使用客户端请求的原始主机名；
        ///   其他 → 固定值（SNI 前置）。
        /// </summary>
        public string? UpstreamSni { get; init; }

        /// <summary>
        /// 发给上游的 Host 头：
        ///   null → 保持客户端原始主机名（{host}）。
        /// </summary>
        public string? UpstreamHost { get; init; }

        /// <summary>非空时直接把请求重定向（HTTP 302）到该地址，不走上游。</summary>
        public string? RedirectTo { get; init; }

        /// <summary>上游 TCP 端口。</summary>
        public int UpstreamPort { get; init; } = 443;

        /// <summary>请求 / 响应都把 Cookie 剥掉（下载类中继需要）。</summary>
        public bool StripCookies { get; init; }

        /// <summary>上游连接超时（毫秒）。</summary>
        public int DialTimeoutMs { get; init; } = 2000;

        /// <summary>是否写入 hosts。个别域名（如 Google 存储）只做 SNI 分流，不改 hosts。</summary>
        public bool EnableHosts { get; init; } = true;
    }

    /// <summary>GitHub + Discord 的加速规则集合，随程序编译进来，不依赖外部配置文件。</summary>
    public static class AccelRules
    {
        /// <summary>上游轮询总时长：在这个时间窗内不断换下一条线路重试。</summary>
        public const int TryDurationMs = 8000;

        /// <summary>上游失败后冷却时长：这段时间内不再选它。</summary>
        public const int FailDurationMs = 30000;

        /// <summary>失败多少次后判定不可用。线路本来就不多，一次失败就该换。</summary>
        public const int MaxFails = 1;

        /// <summary>每条上游最多尝试次数。</summary>
        public const int MaxAttempts = 3;

        public static readonly AccelRule[] All = BuildAll();

        private static readonly AccelHostMap Map = BuildMap();

        private static AccelHostMap BuildMap()
        {
            var map = new AccelHostMap();
            foreach (var rule in All)
                foreach (var domain in rule.Domains)
                    map.Add(domain, rule);
            return map;
        }

        /// <summary>按主机名查规则，未命中返回 null。</summary>
        public static AccelRule? Find(string? host) => Map.Find(host);

        /// <summary>需要写进 hosts 的域名（去重、保序，已剔除通配写法）。</summary>
        public static IReadOnlyList<string> HostsDomains()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();

            foreach (var rule in All)
            {
                if (!rule.EnableHosts) continue;
                foreach (var domain in rule.Domains)
                {
                    var d = domain.Trim().ToLowerInvariant();
                    if (d.Length == 0) continue;
                    if (d.StartsWith("*.", StringComparison.Ordinal)) continue;   // hosts 不支持通配
                    if (seen.Add(d)) list.Add(d);
                }
            }

            return list;
        }

        // ------------------------------------------------------------------
        // 规则定义
        // 每条记录 = 一组域名 + 一组上游端点 + 连接参数（SNI / Host 改写 / Cookie 处理）。
        // ------------------------------------------------------------------
        private static AccelRule[] BuildAll() => new[]
        {
            // ============================ GitHub ============================

            new AccelRule
            {
                Id = "github.com",
                Group = "GitHub",
                Description = "GitHub 主站 / Gist（SNI 前置到可用边缘 IP）",
                Domains = new[] { "github.com", "www.github.com", "gist.github.com" },
                Kind = AccelUpstreamKind.DirectIp,
                Endpoints = new[]
                {
                    "20.200.245.247", "20.205.243.166", "20.207.73.82",
                    "20.27.177.113", "20.233.83.145",
                },
                UpstreamSni = "Github",
            },

            new AccelRule
            {
                Id = "api.github.com",
                Group = "GitHub",
                Description = "GitHub API（经加速中继）",
                Domains = new[] { "api.github.com" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[]
                {
                    "1.ghapi.steam302.xyz", "2.ghapi.steam302.xyz",
                    "3.ghapi.steam302.xyz", "4.ghapi.steam302.xyz",
                },
                UpstreamSni = "Github",
                UpstreamHost = "api.github.com",
            },

            new AccelRule
            {
                Id = "codeload.github.com",
                Group = "GitHub",
                Description = "GitHub 源码打包下载（经加速中继）",
                Domains = new[] { "codeload.github.com" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[]
                {
                    "1.ghcodel.steam302.xyz", "2.ghcodel.steam302.xyz",
                    "3.ghcodel.steam302.xyz", "4.ghcodel.steam302.xyz",
                },
                UpstreamSni = "Github",
                UpstreamHost = "codeload.github.com",
            },

            new AccelRule
            {
                Id = "support.github.com",
                Group = "GitHub",
                Description = "GitHub 支持站点（经加速中继）",
                Domains = new[] { "support.github.com" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[]
                {
                    "1.ghsp.steam302.xyz", "2.ghsp.steam302.xyz",
                    "3.ghsp.steam302.xyz", "4.ghsp.steam302.xyz",
                },
                UpstreamSni = "Github",
                UpstreamHost = "support.github.com",
            },

            new AccelRule
            {
                Id = "githubusercontent",
                Group = "GitHub",
                Description = "raw 内容 / 头像 / 附件（经内容中继）",
                Domains = new[]
                {
                    "raw.githubusercontent.com", "raw.github.com", "camo.githubusercontent.com",
                    "cloud.githubusercontent.com", "avatars.githubusercontent.com",
                    "avatars0.githubusercontent.com", "avatars1.githubusercontent.com",
                    "avatars2.githubusercontent.com", "avatars3.githubusercontent.com",
                    "user-images.githubusercontent.com", "private-user-images.githubusercontent.com",
                    "github-releases.githubusercontent.com", "desktop.githubusercontent.com",
                    "objects.githubusercontent.com", "media.githubusercontent.com",
                    "release-assets.githubusercontent.com",
                },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[]
                {
                    "1.ghcontent.steam302.xyz", "2.ghcontent.steam302.xyz",
                    "3.ghcontent.steam302.xyz", "4.ghcontent.steam302.xyz",
                },
                // 这个块没有 tls_server_name，上游 SNI 保持客户端原始域名
                UpstreamHost = null,
            },

            new AccelRule
            {
                Id = "github-pages",
                Group = "GitHub",
                Description = "GitHub Pages 与静态资源（经内容中继）",
                Domains = new[]
                {
                    "github.io", "www.github.io", "*.github.io",
                    "lab.github.com", "pages.github.com", "resources.github.com",
                    "developer.github.com", "partner.github.com", "desktop.github.com",
                    "guides.github.com", "analytics.githubassets.com",
                },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[]
                {
                    "1.ghcontent.steam302.xyz", "2.ghcontent.steam302.xyz",
                    "3.ghcontent.steam302.xyz", "4.ghcontent.steam302.xyz",
                },
                UpstreamSni = "Github",
            },

            new AccelRule
            {
                Id = "githubassets",
                Group = "GitHub",
                Description = "GitHub 静态资源与文档（经资源中继）",
                Domains = new[]
                {
                    "github.githubassets.com", "docs.github.com", "support-assets.githubassets.com",
                },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[]
                {
                    "1.ghassets.steam302.xyz", "2.ghassets.steam302.xyz",
                    "3.ghassets.steam302.xyz", "4.ghassets.steam302.xyz",
                },
                UpstreamSni = "Github",
            },

            new AccelRule
            {
                Id = "copilot.github.com",
                Group = "GitHub",
                Description = "GitHub Copilot（经加速中继）",
                Domains = new[] { "copilot.github.com" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[]
                {
                    "1.ghcl.steam302.xyz", "2.ghcl.steam302.xyz",
                    "3.ghcl.steam302.xyz", "4.ghcl.steam302.xyz",
                },
                UpstreamSni = "Github",
            },

            new AccelRule
            {
                Id = "services.github.com",
                Group = "GitHub",
                Description = "GitHub 服务站点（经加速中继）",
                Domains = new[] { "services.github.com" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[]
                {
                    "1.ghsvc.steam302.xyz", "2.ghsvc.steam302.xyz",
                    "3.ghsvc.steam302.xyz", "4.ghsvc.steam302.xyz",
                },
                UpstreamSni = "Github",
            },

            new AccelRule
            {
                Id = "github-edu",
                Group = "GitHub",
                Description = "GitHub 教育 / 企业版站点（经教育中继）",
                Domains = new[]
                {
                    "education.github.com", "enterprise.github.com",
                    "classroom.github.com", "central.github.com",
                },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[]
                {
                    "1.ghedu.steam302.xyz", "2.ghedu.steam302.xyz",
                    "3.ghedu.steam302.xyz", "4.ghedu.steam302.xyz",
                },
                UpstreamSni = "Github",
            },

            // ============================ Steam ============================

            new AccelRule
            {
                Id = "steam-store",
                Group = "Steam",
                Description = "Steam 商店 / 登录 / 帮助 / 结算（Akamai 边缘，SNI 前置）",
                Domains = new[]
                {
                    "store.steampowered.com", "help.steampowered.com", "login.steampowered.com",
                    "checkout.steampowered.com", "store.akamai.steamstatic.com",
                },
                Kind = AccelUpstreamKind.DirectIp,
                Endpoints = new[]
                {
                    "steamuserimages-a.akamaihd.net.edgesuite.net",
                    "steamstore-a.akamaihd.net.edgesuite.net",
                    "steamcommunity-a.akamaihd.net.edgesuite.net",
                    "eaassets-a.akamaihd.net.edgesuite.net",
                },
                UpstreamSni = "img-s-msn-com.akamaized.net",
            },

            new AccelRule
            {
                Id = "steam-community",
                Group = "Steam",
                Description = "Steam 社区 / 个人主页 / 创意工坊（Akamai 边缘，SNI 前置）",
                Domains = new[] { "steamcommunity.com", "www.steamcommunity.com" },
                Kind = AccelUpstreamKind.DirectIp,
                Endpoints = new[]
                {
                    "steamcommunity-a.akamaihd.net.edgesuite.net",
                    "steamstore-a.akamaihd.net.edgesuite.net",
                    "steamuserimages-a.akamaihd.net.edgesuite.net",
                },
                UpstreamSni = "img-s-msn-com.akamaized.net",
            },

            // ============================ Twitch ============================

            new AccelRule
            {
                Id = "twitch-main",
                Group = "Twitch",
                Description = "Twitch 主站 / 播放器 / 接口 / 直播聊天（Fastly 边缘）",
                Domains = new[]
                {
                    "twitch.tv", "www.twitch.tv", "m.twitch.tv", "app.twitch.tv", "auth.twitch.tv",
                    "clips.twitch.tv", "dashboard.twitch.tv", "appeals.twitch.tv", "gql.twitch.tv",
                    "player.twitch.tv", "api.twitch.tv", "panels.twitch.tv", "spade.twitch.tv",
                    "trowel.twitch.tv", "irc-ws.chat.twitch.tv", "irc-ws-r.chat.twitch.tv",
                },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[] { "twitch.map.fastly.net" },
            },

            new AccelRule
            {
                Id = "twitch-ash",
                Group = "Twitch",
                Description = "Twitch 视频分片接入（AWS 边缘）",
                Domains = new[] { "*.pdx01.abs.hls.ttvnw.net" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[] { "science-edge-external-prod-73889260.us-west-2.elb.amazonaws.com" },
                EnableHosts = false,
            },

            new AccelRule
            {
                Id = "twitch-cdn-group",
                Group = "Twitch",
                Description = "Twitch 开发者 / 帮助 / 品牌等站点（CloudFront 边缘，SNI 前置）",
                Domains = new[]
                {
                    "vod-storyboards.twitch.tv", "vod-metro.twitch.tv", "music.twitch.tv",
                    "player.m7g.twitch.tv", "blog.twitch.tv", "help.twitch.tv", "brand.twitch.tv",
                    "inspector.twitch.tv", "dev.twitch.tv", "extensions-discovery-images.twitch.tv",
                },
                Kind = AccelUpstreamKind.DirectIp,
                Endpoints = new[]
                {
                    "c.media-amazon.com", "ml.media-amazon.com",
                    "a0.awsstatic.com", "pv.media-amazon.com",
                },
                UpstreamSni = "d2mb634i1sflda.cloudfront.net",
            },

            new AccelRule
            {
                Id = "twitch-vod-group",
                Group = "Twitch",
                Description = "Twitch 点播与视频素材（CloudFront 边缘，SNI 前置）",
                Domains = new[]
                {
                    "vod-secure.twitch.tv", "production.assets.clips.twitchcdn.net",
                },
                Kind = AccelUpstreamKind.DirectIp,
                Endpoints = new[]
                {
                    "c.media-amazon.com", "ml.media-amazon.com",
                    "a0.awsstatic.com", "pv.media-amazon.com",
                },
                UpstreamSni = "ds0h3roq6wcgc.cloudfront.net",
            },

            new AccelRule
            {
                Id = "twitch-assets",
                Group = "Twitch",
                Description = "Twitch 站点素材（CloudFront 边缘，固定 Host）",
                Domains = new[] { "assets.twitch.tv" },
                Kind = AccelUpstreamKind.DirectIp,
                Endpoints = new[]
                {
                    "c.media-amazon.com", "ml.media-amazon.com",
                    "a0.awsstatic.com", "pv.media-amazon.com",
                },
                UpstreamSni = "static.twitchcdn.net",
                UpstreamHost = "static.twitchcdn.net",
            },

            new AccelRule
            {
                Id = "twitch-cdn-rt",
                Group = "Twitch",
                Description = "Twitch 直播流分发（AWS 边缘）",
                Domains = new[] { "static-cdn.jtvnw.net" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[] { "science-edge-external-prod-73889260.us-west-2.elb.amazonaws.com" },
                UpstreamSni = "static-cdn.jtvnw.net",
            },

            new AccelRule
            {
                Id = "twitch-relay-mail",
                Group = "Twitch",
                Description = "Twitch 邮件 / 登录 / 账号 CDN（加速中继）",
                Domains = new[] { "assets.help.twitch.tv", "passport.twitch.tv", "id-cdn.twitch.tv" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[]
                {
                    "ashelpttv.steam302.xyz", "passttv.steam302.xyz", "idcdnttv.steam302.xyz",
                },
            },

            new AccelRule
            {
                Id = "twitch-ttv",
                Group = "Twitch",
                Description = "Twitch 账号 / 事件订阅 / 推流入口 / 聊天（加速中继）",
                Domains = new[]
                {
                    "id.twitch.tv", "pubsub-edge.twitch.tv", "ingest.twitch.tv",
                    "discuss.dev.twitch.tv",
                },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[]
                {
                    "2973c6ca0e111662ed293b57dbae9fbf.steam302.xyz",
                    "522c432cc10e237a02fa1d6481d7d247.steam302.xyz",
                    "b3d696173d52b66e1caab6145cb5058b.steam302.xyz",
                    "890c88446f94f25bd32a3f1e0df6c120.steam302.xyz",
                },
                UpstreamSni = "ttv",
            },

            new AccelRule
            {
                Id = "twitch-usher",
                Group = "Twitch",
                Description = "Twitch 播放列表获取（加速中继）",
                Domains = new[] { "usher.ttvnw.net" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[] { "634d6519809b2734de8cc8aa317c7f28.steam302.xyz" },
                UpstreamSni = "usher.ttvnw.net",
            },

            new AccelRule
            {
                Id = "twitch-cvp",
                Group = "Twitch",
                Description = "Twitch 视频云处理（Akamai 边缘）",
                Domains = new[] { "cvp.twitch.tv" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[] { "e9221.i.akamaiedge.net" },
            },

            new AccelRule
            {
                Id = "twitch-safety",
                Group = "Twitch",
                Description = "Twitch 安全中心（专用边缘）",
                Domains = new[] { "safety.twitch.tv" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[] { "accessibility.acapqa.net" },
            },

            // ============================ Discord ============================

            new AccelRule
            {
                Id = "discordapp.com",
                Group = "Discord",
                Description = "Discord 旧域名（直连 Cloudflare 边缘）",
                Domains = new[] { "discordapp.com" },
                Kind = AccelUpstreamKind.DirectIp,
                Endpoints = new[]
                {
                    "162.159.129.233", "162.159.135.233", "162.159.130.233",
                    "162.159.133.233", "162.159.134.233",
                },
                UpstreamHost = "discordapp.com",
            },

            new AccelRule
            {
                Id = "discord.com",
                Group = "Discord",
                Description = "Discord 主站 / API（直连 Cloudflare 边缘）",
                Domains = new[] { "discord.com" },
                Kind = AccelUpstreamKind.DirectIp,
                Endpoints = new[]
                {
                    "162.159.138.232", "162.159.135.232", "162.159.137.232",
                    "162.159.128.233", "162.159.136.232",
                },
                UpstreamHost = "discord.com",
            },

            new AccelRule
            {
                Id = "updates.discord.com",
                Group = "Discord",
                Description = "Discord 桌面端更新（直连 Cloudflare 边缘）",
                Domains = new[] { "updates.discord.com" },
                Kind = AccelUpstreamKind.DirectIp,
                Endpoints = new[]
                {
                    "162.159.138.232", "162.159.135.232", "162.159.137.232",
                    "162.159.128.233", "162.159.136.232",
                },
                UpstreamHost = "updates.discord.com",
            },

            new AccelRule
            {
                Id = "dl.discordapp.net",
                Group = "Discord",
                Description = "Discord 客户端下载（经下载中继）",
                Domains = new[] { "dl.discordapp.net" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[] { "dlds.steam302.xyz" },
                UpstreamHost = "dlds.steam302.xyz",
                StripCookies = true,
            },

            new AccelRule
            {
                Id = "dl2.discordapp.net",
                Group = "Discord",
                Description = "Discord 客户端下载第二线路（经下载中继）",
                Domains = new[] { "dl2.discordapp.net", "*.dl2.discordapp.net" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[] { "dlds2.steam302.xyz" },
                UpstreamSni = "update",
                StripCookies = true,
            },

            new AccelRule
            {
                Id = "discord.gg",
                Group = "Discord",
                Description = "Discord 邀请链接 / 实时网关（SNI 前置）",
                Domains = new[] { "*.discord.gg", "discord.gg" },
                Kind = AccelUpstreamKind.DirectIp,
                Endpoints = new[]
                {
                    "162.159.136.234", "162.159.134.234", "162.159.133.234",
                    "162.159.130.234", "162.159.135.234",
                },
                UpstreamSni = "dcgg",
            },

            new AccelRule
            {
                Id = "media.discordapp.net",
                Group = "Discord",
                Description = "Discord 图片 / 媒体（直连 Cloudflare 边缘）",
                Domains = new[] { "media.discordapp.net" },
                Kind = AccelUpstreamKind.DirectIp,
                Endpoints = new[]
                {
                    "162.159.133.232", "162.159.134.232", "162.159.129.232",
                    "162.159.130.232", "162.159.128.232",
                },
                UpstreamHost = "media.discordapp.net",
            },

            new AccelRule
            {
                Id = "images-ext.discordapp.net",
                Group = "Discord",
                Description = "Discord 外链图片反向代理（重定向到 media 域名）",
                Domains = new[] { "images-ext-2.discordapp.net", "images-ext-1.discordapp.net" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[] { "media.discordapp.net" },   // 不会真正连接，只用于占位
                RedirectTo = "https://media.discordapp.net",
            },

            new AccelRule
            {
                Id = "discord-support",
                Group = "Discord",
                Description = "Discord 支持 / 安全站点（经支持中继）",
                Domains = new[]
                {
                    "support.discord.com", "safety.discord.com", "support-dev.discord.com",
                },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[]
                {
                    "supportds.steam302.xyz", "safetyds.steam302.xyz", "support-devds.steam302.xyz",
                },
                UpstreamHost = "supportds.steam302.xyz",
            },

            new AccelRule
            {
                Id = "discord-misc",
                Group = "Discord",
                Description = "Discord 状态 / 社区等站点（直连 Cloudflare 边缘）",
                Domains = new[]
                {
                    "status.discord.com", "streamkit.discord.com", "feedback.discord.com",
                    "click.discord.com", "pax.discord.com", "printer.discord.com",
                    "ptb.discord.com", "canary.discord.com", "bugs.discord.com",
                    "i18n.discord.com", "docs.discord.com",
                },
                Kind = AccelUpstreamKind.DirectIp,
                Endpoints = new[]
                {
                    "162.159.135.232", "162.159.136.232", "162.159.137.232",
                    "162.159.138.232", "162.159.128.233",
                },
            },

            new AccelRule
            {
                Id = "discordapp-cdn",
                Group = "Discord",
                Description = "Discord CDN / 客户端接口（直连 Cloudflare 边缘）",
                Domains = new[]
                {
                    "url9177.discordapp.com", "canary-api.discordapp.com", "cdn-ptb.discordapp.com",
                    "ptb.discordapp.com", "status.discordapp.com", "cdn-canary.discordapp.com",
                    "cdn.discordapp.com", "streamkit.discordapp.com", "i18n.discordapp.com",
                    "url9624.discordapp.com", "url7195.discordapp.com", "merch.discordapp.com",
                    "printer.discordapp.com", "canary.discordapp.com", "apps.discordapp.com",
                    "pax.discordapp.com",
                },
                Kind = AccelUpstreamKind.DirectIp,
                Endpoints = new[]
                {
                    "162.159.133.233", "162.159.129.233", "162.159.130.233",
                    "162.159.134.233", "162.159.135.233",
                },
            },

            new AccelRule
            {
                Id = "discord-attachments",
                Group = "Discord",
                Description = "Discord 附件上传（Google 存储，SNI 前置）",
                Domains = new[] { "discord-attachments-uploads-prd.storage.googleapis.com" },
                Kind = AccelUpstreamKind.RelayDomain,
                Endpoints = new[]
                {
                    "storage.googleapis.com", "steamcloud-ugc.storage.googleapis.com",
                    "steamcloud-singapore.storage.googleapis.com",
                    "steamcloud-taiwan.storage.googleapis.com",
                    "chromedriver.storage.googleapis.com", "mongodb.storage.googleapis.com",
                },
                UpstreamSni = "uploads",
                EnableHosts = false,   // 该域名不做 hosts 改写，只按 SNI 分流
            },
        };
    }
}
