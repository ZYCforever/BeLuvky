using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BetterWebLauncher
{
    /// <summary>
    /// 本地加速引擎：在 127.0.0.1:443 上监听，按 SNI 把请求转发到可用的上游。
    ///
    /// 工作流程：
    ///   1. 客户端因为 hosts 被改写而连到 127.0.0.1:443；
    ///   2. 读出 ClientHello 里的 SNI，得到真实目标主机名；
    ///   3. 用本地 CA 现场签发该域名的证书，完成 TLS 握手（客户端看到的是合法证书）；
    ///   4. 按规则挑一个上游：直连 IP（可带 SNI 前置）或加速中继域名；
    ///   5. 把 HTTP 报文逐字节转发过去，响应原样回写。
    ///
    /// 这里刻意不使用 HttpClient：
    /// 我们需要精确控制「连哪个 IP、发什么 SNI、Host 头写什么」，
    /// 而且握手 + 早期数据必须复用同一条连接，HttpClientHandler 无法满足。
    /// </summary>
    public sealed class AccelEngine : IDisposable, IAsyncDisposable
    {
        private const long MaxRequestBodyBytes = 64L * 1024 * 1024;
        private const int ClientHelloMaxBytes = 32 * 1024;
        private const int MaxHeaderBytes = 128 * 1024;

        private readonly CertificateAuthority _ca;
        private readonly AccelUpstreamConnector _connector;

        private readonly ConcurrentDictionary<string, EndpointHealth> _health =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly SemaphoreSlim _slots = new(256, 256);

        /// <summary>换下一条线路前的等待。测试可设为 0。</summary>
        internal int RetryDelayMs { get; set; } = 150;

        /// <summary>
        /// 被 hosts 指向 127.0.0.1 的主机名。
        /// 连接这些名字时必须跳过系统解析，否则会连回自己。
        /// </summary>
        private readonly ConcurrentDictionary<string, byte> _loopbackHosts =
            new(StringComparer.OrdinalIgnoreCase);

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptLoop;

        private AccelRule[] _rules = Array.Empty<AccelRule>();
        private AccelHostMap _map = AccelHostMap.Empty;

        public AccelEngine(CertificateAuthority ca)
        {
            _ca = ca;
            _connector = new AccelUpstreamConnector(ResolveFromMap);
        }

        /// <summary>本机监听端口，固定 443（hosts 只能指向端口 443）。</summary>
        public int Port { get; private set; } = 443;

        public bool IsRunning { get; private set; }

        public AccelStats Stats { get; } = new();

        public event Action<AccelLogEntry>? Log;

        public IReadOnlyList<AccelRule> Rules => _rules;

        public void SetRules(IEnumerable<AccelRule> rules)
        {
            var list = rules
                .Where(r => r.Domains.Length > 0 && r.Endpoints.Length > 0)
                .ToArray();

            var map = new AccelHostMap();
            foreach (var rule in list)
                foreach (var domain in rule.Domains)
                    map.Add(domain, rule);

            // 同步更新「会被 hosts 劫持到本机」的名字集合
            _loopbackHosts.Clear();
            foreach (var rule in list)
            {
                if (!rule.EnableHosts) continue;
                foreach (var domain in rule.Domains)
                {
                    var normalized = AccelHostMap.Normalize(domain);
                    if (normalized.Length > 0)
                        _loopbackHosts[normalized] = 0;
                }
            }

            _rules = list;
            _map = map;
        }

        // ------------------------------------------------------------------
        // 生命周期
        // ------------------------------------------------------------------
        public void Start(int port = 443)
        {
            if (IsRunning) return;

            Port = port;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start(512);

            // 传 0 时由系统分配端口，回读真实端口（测试与端口占用重试都会用到）
            if (_listener.LocalEndpoint is IPEndPoint bound) Port = bound.Port;

            IsRunning = true;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public async Task StopAsync()
        {
            if (!IsRunning) return;
            IsRunning = false;

            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }

            if (_acceptLoop != null)
            {
                try { await Task.WhenAny(_acceptLoop, Task.Delay(2000)).ConfigureAwait(false); } catch { }
                _acceptLoop = null;
            }

            _listener = null;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            var listener = _listener;
            if (listener == null) return;

            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException)
                {
                    if (ct.IsCancellationRequested) break;
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                _ = HandleClientAsync(client, ct);
            }
        }

        // ------------------------------------------------------------------
        // 单个连接
        // ------------------------------------------------------------------
        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            if (!await _slots.WaitAsync(0, ct).ConfigureAwait(false))
            {
                try { client.Dispose(); } catch { }
                return;
            }

            Stats.Enter();
            try
            {
                client.NoDelay = true;
                using (client)
                using (var stream = client.GetStream())
                {
                    await ProcessAsync(client, stream, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Report("", null, null, false, 0, 0, ex.Message);
            }
            finally
            {
                Stats.Leave();
                _slots.Release();
            }
        }

        private async Task ProcessAsync(TcpClient client, Stream clientStream, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var token = timeout.Token;

            // 读 SNI 会把 ClientHello 从 socket 里吃掉，这段字节必须还给 SslStream
            var (sni, replayed) = await SniReader.ReadAsync(clientStream, ClientHelloMaxBytes, token)
                .ConfigureAwait(false);

            var rule = _map.Find(sni);
            if (rule == null)
            {
                // 没有对应规则：多半是 hosts 里的残留条目，直接断开，不做任意转发
                Report(sni ?? "(no-sni)", null, null, false, 0, 0, "no matching rule");
                return;
            }

            var fallbackHost = rule.Domains[0].TrimStart('*', '.');

            // SNI 前置时客户端发给本机的是真实域名；只有客户端没给 SNI 时才退化成固定值
            var certificateHost = SniReader.PickCertificateHost(sni, fallbackHost);

            var leaf = _ca.GetLeaf(certificateHost);
            using var ssl = new SslStream(replayed.Length > 0
                ? new PrependStream(clientStream, replayed)
                : clientStream, leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = leaf,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                          System.Security.Authentication.SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report(certificateHost, null, rule.Id, false, 0, 0, "TLS handshake failed: " + ex.Message);
                return;
            }

            HttpRequestHead request;
            try
            {
                request = await HttpRequestHead.ReadAsync(ssl, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report(certificateHost, null, rule.Id, false, 0, 0, "bad request: " + ex.Message);
                return;
            }

            if (rule.RedirectTo != null)
            {
                // 直接回一个 302，例如 images-ext-2.discordapp.net → media.discordapp.net
                await WriteRedirectAsync(ssl, rule.RedirectTo + request.Path, token).ConfigureAwait(false);
                Report(request.Host, rule.RedirectTo, rule.Id, true, 302, 0, null);
                return;
            }

            if (request.IsUpgrade)
            {
                await HandleUpgradeAsync(ssl, request, rule, token).ConfigureAwait(false);
                return;
            }

            await HandleHttpAsync(ssl, request, rule, client.Client.RemoteEndPoint, token).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        // 普通 HTTP 转发（原始字节转发，不上 HttpClient）
        // ------------------------------------------------------------------
        private async Task HandleHttpAsync(
            SslStream client, HttpRequestHead request, AccelRule rule, EndPoint? remote, CancellationToken ct)
        {
            var started = Environment.TickCount64;
            string? usedEndpoint = null;

            // 响应头一旦回写给浏览器，就不能再补发 502 了。
            bool headSent = false;
            int answerStatus = 0;

            Stream? requestBody = null;
            try
            {
                if (request.ContentLength > MaxRequestBodyBytes)
                {
                    await WriteSimpleAsync(client, 413, "Payload Too Large", "request body too large", ct)
                        .ConfigureAwait(false);
                    return;
                }

                if (request.ContentLength > 0)
                    requestBody = new LimitedReadStream(client, request.ContentLength, leaveOpen: true);
                else if (request.IsChunked)
                    requestBody = new ChunkedReadStream(client);

                var headBytes = request.ToWireBytes(rule.UpstreamHost ?? request.Host, rule.StripCookies,
                    remote is IPEndPoint ip ? ip.Address.ToString() : null);

                var conn = await ConnectUpstreamAsync(rule, request.Host, ct).ConfigureAwait(false);
                usedEndpoint = conn.Endpoint;

                // 上游连接上读到的字节全部记下来。SslStream 的 ReadAsync 可能读回
                // 整整一个 TLS 记录（远远多于请求的那 1 个字节），所以必须是把
                // "已经消费掉的字节"记下来再补齐，而不是去猜上游会回多少。
                HeadRecordingStream? recorded = new HeadRecordingStream(conn.Stream, Array.Empty<byte>());

                int status = 0;
                bool sent = false;

                // 先原样把请求发出去，再拿回响应头。上游回 5xx（Cloudflare 522 /
                // 网关超时之类）说明这条线路当下不行：本地上游连接还开着，请求
                // 原样重放到下一条线路，浏览器那边一点都感觉不到。
                var attempts = Math.Max(1, Math.Min(AccelRules.MaxAttempts, rule.Endpoints.Length));
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    try
                    {
                        if (!sent)
                        {
                            await recorded.WriteAsync(headBytes, ct).ConfigureAwait(false);
                            await recorded.FlushAsync(ct).ConfigureAwait(false);

                            if (requestBody != null)
                                await CopyStreamAsync(requestBody, recorded, ct).ConfigureAwait(false);

                            sent = true;
                        }
                    }
                    catch (IOException) when (headSent)
                    {
                        // 响应头已经回写给浏览器了，写请求体时才断开。
                        break;
                    }

                    int headStatus;
                    try
                    {
                        var probeHead = await ReadHeaderBlockFromAsync(recorded, ct).ConfigureAwait(false);

                        if (probeHead.Length >= 5 &&
                            probeHead[0] == (byte)'H' && probeHead[1] == (byte)'T' && probeHead[2] == (byte)'T' &&
                            probeHead[3] == (byte)'P')
                            headStatus = ParseStatusCode(Encoding.ASCII.GetString(probeHead));
                        else
                        {
                            // 响应开头不是 HTTP 状态行，当成普通连接问题换线路。
                            headStatus = 0;
                            throw new IOException("上游返回了无法识别的响应");
                        }

                        // 上游回 5xx 说明这条线路当下不行：还没往浏览器写过一个字节，
                        // 直接换下一条线路重来，浏览器那边一点都感觉不到。
                        if (headStatus >= 500 && attempt < attempts)
                        {
                            MarkUnhealthy(usedEndpoint);
                            Report(request.Host, usedEndpoint, rule.Id, false, headStatus,
                                Environment.TickCount64 - started, "upstream " + headStatus + ", retrying");

                            await recorded.DisposeAsync().ConfigureAwait(false);
                            conn = await ConnectUpstreamAsync(rule, request.Host, ct).ConfigureAwait(false);
                            usedEndpoint = conn.Endpoint;
                            recorded = new HeadRecordingStream(conn.Stream, Array.Empty<byte>());
                            continue;
                        }

                        // 把已经替浏览器读掉的那段字节原样还回去。这里绝对不能做任何
                        // 头部与正文的切分：空行分隔符里也含有连续换行，按连续换行切会
                        // 把状态行之后的内容整段丢掉，浏览器就会看到残缺响应。
                        await client.WriteAsync(probeHead, ct).ConfigureAwait(false);
                        await client.FlushAsync(ct).ConfigureAwait(false);

                        headSent = true;
                        status = headStatus;
                        answerStatus = headStatus;
                        break;
                    }
                    catch (IOException ex)
                    {
                        if (headSent) break;

                        if (attempt >= attempts)
                            throw new IOException("上游没有返回有效响应：" + ex.Message, ex);

                        MarkUnhealthy(usedEndpoint);
                        Report(request.Host, usedEndpoint, rule.Id, false, 0,
                            Environment.TickCount64 - started, ex.Message);

                        await recorded.DisposeAsync().ConfigureAwait(false);
                        conn = await ConnectUpstreamAsync(rule, request.Host, ct).ConfigureAwait(false);
                        usedEndpoint = conn.Endpoint;
                        recorded = new HeadRecordingStream(conn.Stream, Array.Empty<byte>());
                        continue;
                    }
                }

                // 把响应体原样透传给浏览器，然后收工。
                try
                {
                    await CopyStreamAsync(recorded, client, ct).ConfigureAwait(false);
                    await client.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // 上游传完就 RST，或者浏览器提前断开：响应已经发完了，忽略。
                }

                MarkHealthy(usedEndpoint);
                Report(request.Host, usedEndpoint, rule.Id, status > 0, status,
                    Environment.TickCount64 - started, null);
            }
            catch (Exception ex)
            {
                MarkUnhealthy(usedEndpoint);
                Report(request.Host, usedEndpoint, rule.Id, false, 0,
                    Environment.TickCount64 - started, ex.Message);

                if (!headSent)
                {
                    try
                    {
                        await WriteSimpleAsync(client, 502, "Bad Gateway",
                            "BetterWebLauncher 无法连接上游：" + ex.Message, ct).ConfigureAwait(false);
                    }
                    catch { /* 客户端可能已断开 */ }
                }
            }
        }

        /// <summary>把响应头块拆成"状态行 + 中间的头"和"最后一个 CRLF 之后的字节"。</summary>
        private static (byte[] Head, byte[] Body) SplitHead(byte[] block)
        {
            int end = -1;
            for (int i = 0; i + 1 < block.Length; i++)
            {
                if (block[i] == (byte)'\n' && block[i + 1] == (byte)'\n') { end = i + 2; break; }
                if (i + 3 < block.Length &&
                    block[i] == (byte)'\r' && block[i + 1] == (byte)'\n' &&
                    block[i + 2] == (byte)'\r' && block[i + 3] == (byte)'\n') { end = i + 4; break; }
            }

            if (end < 0) return (block, Array.Empty<byte>());

            var head = new byte[end];
            Buffer.BlockCopy(block, 0, head, 0, end);

            if (end >= block.Length) return (head, Array.Empty<byte>());

            var rest = new byte[block.Length - end];
            Buffer.BlockCopy(block, end, rest, 0, rest.Length);
            return (head, rest);
        }

        private static int ParseStatusCode(string head)
        {
            int space = head.IndexOf(' ');
            if (space < 0 || head.Length < space + 4) return 0;
            return int.TryParse(head.AsSpan(space + 1, 3), out var code) ? code : 0;
        }

        private static string StripSetCookie(string head)
        {
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var builder = new StringBuilder(head.Length);
            bool first = true;
            foreach (var line in lines)
            {
                if (line.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!first) builder.Append("\r\n");
                builder.Append(line);
                first = false;
            }
            return builder.ToString();
        }

        /// <summary>读到空行为止，返回包含结尾空行的原始字节。</summary>
        private static async Task<byte[]> ReadHeaderBlockAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[1];
            var collected = new List<byte>(2048);
            int consecutiveNewlines = 0;

            while (collected.Count < MaxHeaderBytes)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
                if (read <= 0) break;

                collected.Add(buffer[0]);
                if (buffer[0] == (byte)'\n')
                {
                    consecutiveNewlines++;
                    if (consecutiveNewlines == 2) break;
                }
                else if (buffer[0] != (byte)'\r')
                {
                    consecutiveNewlines = 0;
                }
            }

            return collected.ToArray();
        }

        private static async Task CopyStreamAsync(Stream source, Stream destination, CancellationToken ct)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (true)
                {
                    int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read <= 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static async Task WriteSimpleAsync(
            Stream client, int status, string reason, string message, CancellationToken ct)
        {
            var body = Encoding.UTF8.GetBytes(message);
            var header = $"HTTP/1.1 {status} {reason}\r\n" +
                         "Content-Type: text/plain; charset=utf-8\r\n" +
                         $"Content-Length: {body.Length}\r\n" +
                         "Connection: close\r\n\r\n";

            await client.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
            await client.WriteAsync(body, ct).ConfigureAwait(false);
            await client.FlushAsync(ct).ConfigureAwait(false);
        }

        private static async Task WriteRedirectAsync(Stream client, string location, CancellationToken ct)
        {
            var body = Encoding.UTF8.GetBytes("Redirecting to " + location);
            var header = $"HTTP/1.1 302 Found\r\n" +
                         $"Location: {location}\r\n" +
                         "Content-Type: text/plain; charset=utf-8\r\n" +
                         $"Content-Length: {body.Length}\r\n" +
                         "Cache-Control: no-store\r\n" +
                         "Connection: close\r\n\r\n";

            await client.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
            await client.WriteAsync(body, ct).ConfigureAwait(false);
            await client.FlushAsync(ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        // WebSocket / 协议升级：原始字节双向中继
        // ------------------------------------------------------------------
        private async Task HandleUpgradeAsync(
            SslStream client, HttpRequestHead request, AccelRule rule, CancellationToken ct)
        {
            var started = Environment.TickCount64;
            string? usedEndpoint = null;

            try
            {
                var headBytes = request.ToWireBytes(rule.UpstreamHost ?? request.Host, rule.StripCookies, null);

                var conn = await ConnectUpstreamAsync(rule, request.Host, ct).ConfigureAwait(false);
                usedEndpoint = conn.Endpoint;

                // 和普通请求走同一套"先记录、再原样回写"的逻辑：升级响应
                // 也不能丢字节，否则 WebSocket 的 101 会被截断。
                HeadRecordingStream? recorded = new HeadRecordingStream(conn.Stream, Array.Empty<byte>());

                var attempts = Math.Max(1, Math.Min(AccelRules.MaxAttempts, rule.Endpoints.Length));
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    await recorded.WriteAsync(headBytes, ct).ConfigureAwait(false);
                    await recorded.FlushAsync(ct).ConfigureAwait(false);

                    byte[] probeHead;
                    try
                    {
                        probeHead = await ReadHeaderBlockFromAsync(recorded, ct).ConfigureAwait(false);
                    }
                    catch (IOException) when (attempt < attempts)
                    {
                        MarkUnhealthy(usedEndpoint);
                        await recorded.DisposeAsync().ConfigureAwait(false);
                        conn = await ConnectUpstreamAsync(rule, request.Host, ct).ConfigureAwait(false);
                        usedEndpoint = conn.Endpoint;
                        recorded = new HeadRecordingStream(conn.Stream, Array.Empty<byte>());
                        continue;
                    }

                    await client.WriteAsync(probeHead, ct).ConfigureAwait(false);
                    await client.FlushAsync(ct).ConfigureAwait(false);
                    break;
                }

                using (conn.Stream)
                {
                    await RelayAsync(client, recorded, ct).ConfigureAwait(false);
                }

                MarkHealthy(usedEndpoint);
                Report(request.Host, conn.Endpoint, rule.Id, true, 101,
                    Environment.TickCount64 - started, null);
            }
            catch (Exception ex)
            {
                MarkUnhealthy(usedEndpoint);
                Report(request.Host, usedEndpoint, rule.Id, false, 0,
                    Environment.TickCount64 - started, ex.Message);
            }
        }

        private static async Task RelayAsync(Stream a, Stream b, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var forward = PumpAsync(a, b, linked.Token);
            var backward = PumpAsync(b, a, linked.Token);

            await Task.WhenAny(forward, backward).ConfigureAwait(false);
            try { linked.Cancel(); } catch { }
        }

        private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            try
            {
                while (true)
                {
                    int read = await from.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read <= 0) break;
                    await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    await to.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // 对端关闭属正常
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // ------------------------------------------------------------------
        // 上游选择与失败冷却
        // ------------------------------------------------------------------

        /// <summary>
        /// 把规则里的端点写法拆成「主机 + 端口」。
        ///
        /// 规则表里的端点写成 https://20.205.243.166 或 https://relay.example.com
        /// 这种带协议的 URL；TcpClient 只认主机名，所以这里统一剥掉协议和路径，
        /// 未显式写端口时用规则上的 UpstreamPort（默认 443）。
        /// </summary>
        internal static (string Host, int Port) ParseEndpoint(string endpoint, int defaultPort)
        {
            var port = defaultPort > 0 ? defaultPort : 443;
            var text = (endpoint ?? "").Trim();
            if (text.Length == 0) return ("", port);

            int scheme = text.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) text = text.Substring(scheme + 3);

            int slash = text.IndexOfAny(new[] { '/', '?', '#' });
            if (slash >= 0) text = text.Substring(0, slash);

            // IPv6 字面量形如 [::1]:443
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                int close = text.IndexOf(']');
                if (close > 0)
                {
                    var v6 = text.Substring(1, close - 1);
                    var rest = text.Substring(close + 1);
                    if (rest.StartsWith(":", StringComparison.Ordinal) &&
                        int.TryParse(rest.Substring(1), out var v6Port) && v6Port > 0)
                        port = v6Port;
                    return (v6, port);
                }
            }

            int colon = text.LastIndexOf(':');
            if (colon > 0 && colon == text.IndexOf(':'))
            {
                if (int.TryParse(text.Substring(colon + 1), out var explicitPort) && explicitPort > 0)
                {
                    port = explicitPort;
                    text = text.Substring(0, colon);
                }
            }

            return (text, port);
        }

        /// <summary>
        /// 换线路的判定：连接失败（异常），或者上游直接回了一个"临时故障"状态码。
        ///
        /// 之前只在连接异常时换线路，像 Cloudflare / AWS 边缘偶尔回的 502 会被原样转给浏览器，
        /// 看起来就是"这个站打不开"。现在这类响应也当作线路故障处理，换一条再试。
        /// </summary>
        private static bool ShouldRetryStatus(int status) =>
            status >= 500 && status != 501 && status != 505;

        /// <summary>只识别响应开头是不是 5xx，不占用上游连接上的任何字节。</summary>

        /// <summary>
        /// 包一层记下从上游读到的所有字节。做成"泄漏出来"的形式，
        /// 这样任何一次 ReadAsync 想多读多少都不会把响应头的开头吞掉。
        /// </summary>
        private sealed class HeadRecordingStream : Stream
        {
            private readonly Stream _inner;
            private readonly MemoryStream _prefix;
            private int _offset;

            public HeadRecordingStream(Stream inner, byte[] prefix)
            {
                _inner = inner;
                _prefix = new MemoryStream();
                _prefix.Write(prefix, 0, prefix.Length);
                _prefix.Position = 0;
            }

            public byte[] Captured => _prefix.ToArray();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
                => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (_offset < _prefix.Length)
                {
                    _prefix.Position = _offset;
                    int want = (int)Math.Min(buffer.Length, _prefix.Length - _offset);
                    int got = await _prefix.ReadAsync(buffer.Slice(0, want), ct).ConfigureAwait(false);
                    if (got > 0) { _offset += got; return got; }
                    return 0;
                }

                int n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n > 0)
                {
                    // 记下来的字节同时也算"已经被读走"，_offset 必须一起前进，
                    // 否则这些字节会被当成待回放的缓冲再返回一遍（每个字节重复两次）。
                    _prefix.Position = _prefix.Length;
                    await _prefix.WriteAsync(buffer.Slice(0, n), ct).ConfigureAwait(false);
                    _offset += n;
                }
                return n;
            }

            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
                => _inner.WriteAsync(buffer, offset, count, ct);
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
                => _inner.WriteAsync(buffer, ct);
            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
            public override bool CanTimeout => _inner.CanTimeout;
            public override int ReadTimeout { get => _inner.ReadTimeout; set => _inner.ReadTimeout = value; }
            public override int WriteTimeout { get => _inner.WriteTimeout; set => _inner.WriteTimeout = value; }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }

        private static async Task<byte[]> ReadHeaderBlockFromAsync(Stream stream, CancellationToken ct)
            => await ReadHeaderBlockAsync(stream, ct).ConfigureAwait(false);

        /// <summary>已经建立好的上游连接。</summary>
        private sealed class UpstreamConnection
        {
            public SslStream Stream { get; init; } = null!;
            public string Endpoint { get; init; } = "";
        }

        /// <summary>
        /// 连上游，并且要求它给出一个"能看"的响应。
        ///
        /// - 连接/握手失败 → 换下一条线路；
        /// - 上游回了 5xx（网关超时、Cloudflare 522 这类）→ 也换下一条线路，
        ///   但只在规则有多条线路时才换，免得把 Twitch 那些必然回 4xx/5xx 的流媒体接口也一起重试。
        /// </summary>
        private async Task<UpstreamConnection> ConnectUpstreamAsync(
            AccelRule rule, string clientHost, CancellationToken ct)
        {
            var candidates = HealthyEndpoints(rule);
            if (candidates.Count == 0) candidates = rule.Endpoints.ToList();

            Exception? last = null;
            int attempts = 0;

            foreach (var endpoint in candidates)
            {
                if (attempts++ >= Math.Max(1, AccelRules.MaxAttempts)) break;
                if (attempts > 1 && RetryDelayMs > 0)
                {
                    try { await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }

                try
                {
                    var (host, port) = ParseEndpoint(endpoint, rule.UpstreamPort);
                    SslStream stream;

                    if (rule.Kind == AccelUpstreamKind.DirectIp)
                    {
                        var sni = rule.UpstreamSni ?? clientHost;
                        stream = await _connector.ConnectTlsAsync(
                            host, port, sni, rule.DialTimeoutMs, clientHost, ct)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        // 加速中继：endpoint 是域名，连它本身，SNI 用规则里的值（没有就用域名）
                        var relayHost = _connector.ResolveOriginalHost(host) ?? host;
                        var relaySni = rule.UpstreamSni ?? relayHost;
                        stream = await _connector.ConnectTlsAsync(
                            relayHost, port, relaySni, rule.DialTimeoutMs, clientHost, ct)
                            .ConfigureAwait(false);
                    }

                    return new UpstreamConnection { Stream = stream, Endpoint = endpoint };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    MarkUnhealthy(endpoint);
                }
            }

            throw new IOException("所有上游均不可用：" + (last?.Message ?? "未知错误"), last);
        }

        /// <summary>
        /// 把请求发给上游，然后只看响应状态码的第一个数字。
        ///
        /// SslStream 是带缓冲的：读 1 个字节不会让后面的响应头丢失，
        /// 所以看出是 5xx 时可以直接丢掉这条连接、换下一条线路重来；
        /// 请求体这时还没开始从客户端抽（LimitedReadStream 是惰性的），重发不会出错。
        /// </summary>

        /// <summary>
        /// 从上游读走完整的状态行 + 响应头，写进 <paramref name="buffer"/>。
        /// 返回状态码；<c>false</c> 表示还没读到状态行就没有响应体了（拿不到可用响应）。
        /// </summary>
        private static async Task<(int Status, bool Ok)> ReadUpstreamHeadAsync(
            Stream upstream, MemoryStream buffer, CancellationToken ct)
        {
            var pending = new List<byte>(512);
            var line = new List<byte>(256);
            bool first = true;
            int status = 0;

            while (buffer.Length < MaxHeaderBytes)
            {
                var one = new byte[1];
                int n = await upstream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
                if (n <= 0) return (status, false);

                line.Add(one[0]);
                pending.Add(one[0]);

                if (one[0] != (byte)'\n') continue;

                var text = System.Text.Encoding.ASCII.GetString(line.ToArray()).TrimEnd('\r', '\n');
                line.Clear();

                if (first)
                {
                    var parts = text.Split(' ');
                    if (parts.Length < 2 || !int.TryParse(parts[1], out status)) return (0, false);

                    first = false;
                    continue;
                }

                if (text.Length == 0)
                {
                    buffer.Write(pending.ToArray(), 0, pending.Count);
                    return (status, true);
                }
            }

            return (status, false);
        }

        private List<string> HealthyEndpoints(AccelRule rule)
        {
            var now = DateTime.UtcNow;
            var list = new List<string>();
            foreach (var endpoint in rule.Endpoints)
            {
                if (_health.TryGetValue(rule.Id + "|" + endpoint, out var state) &&
                    state.Failures >= AccelRules.MaxFails &&
                    state.CooldownUntil > now)
                {
                    continue;
                }
                list.Add(endpoint);
            }

            if (list.Count > 1)
            {
                var random = new Random(Guid.NewGuid().GetHashCode());
                list = list.OrderBy(_ => random.Next()).ToList();
            }

            return list;
        }

        private void MarkHealthy(string? endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return;
            foreach (var rule in _rules)
            {
                var key = rule.Id + "|" + endpoint;
                if (_health.TryGetValue(key, out var state))
                {
                    state.Failures = 0;
                    state.CooldownUntil = DateTime.MinValue;
                }
            }
        }

        private void MarkUnhealthy(string? endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return;
            foreach (var rule in _rules)
            {
                if (!rule.Endpoints.Contains(endpoint)) continue;
                var state = _health.GetOrAdd(rule.Id + "|" + endpoint, _ => new EndpointHealth());
                state.Failures++;
                if (state.Failures >= AccelRules.MaxFails)
                    state.CooldownUntil = DateTime.UtcNow.AddMilliseconds(AccelRules.FailDurationMs);
            }
        }

        /// <summary>
        /// 把要连接的主机名还原成真正可以解析的名字。
        ///
        /// 被 hosts 写进 127.0.0.1 的域名直接解析会连回自己，所以这里统一改写：
        /// 叶子域名保留最左边的标签，其余部分换成规则里的真实域名。
        /// 例：accela.ghproxy.steam302.xyz → accela.steam302.xyz
        /// </summary>
        private string? ResolveFromMap(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;

            var name = AccelHostMap.Normalize(host);

            if (!_loopbackHosts.ContainsKey(name))
                return name;                                     // 没被劫持，正常解析

            int dot = name.IndexOf('.');
            string tail = dot >= 0 ? name.Substring(dot + 1) : name;

            if (_map.Find(tail) != null) return tail;

            // 回到规则里第一个真实域名，保证一定解析得到
            foreach (var rule in _rules)
            {
                if (!rule.EnableHosts) continue;
                if (rule.Kind != AccelUpstreamKind.RelayDomain) continue;
                foreach (var domain in rule.Domains)
                {
                    var candidate = AccelHostMap.Normalize(domain);
                    if (candidate.Length > 0 && _loopbackHosts.ContainsKey(candidate))
                        return candidate;
                }
            }

            return null;
        }

        private void Report(string host, string? upstream, string? ruleId, bool success,
            int status, long elapsed, string? error)
        {
            Log?.Invoke(new AccelLogEntry
            {
                Host = host,
                Upstream = upstream ?? "",
                RuleId = ruleId ?? "",
                Success = success,
                StatusCode = status,
                ElapsedMs = elapsed,
                Error = error,
            });

            Stats.AddRequest();
            if (!success) Stats.AddFailure();
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _cts?.Dispose(); } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            Dispose();
        }

        private sealed class EndpointHealth
        {
            public int Failures;
            public DateTime CooldownUntil = DateTime.MinValue;
        }
    }

    /// <summary>主机名 → 规则（精确 + 通配）的查找表。</summary>
    public sealed class AccelHostMap
    {
        public static readonly AccelHostMap Empty = new();

        private readonly Dictionary<string, AccelRule> _exact = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<KeyValuePair<string, AccelRule>> _wildcards = new();

        public void Add(string domain, AccelRule rule)
        {
            var d = (domain ?? "").Trim().ToLowerInvariant().TrimEnd('.');
            if (d.Length == 0) return;

            if (d.StartsWith("*.", StringComparison.Ordinal))
                _wildcards.Add(new KeyValuePair<string, AccelRule>(d.Substring(1), rule));
            else
                _exact[d] = rule;
        }

        /// <summary>去掉端口、统一小写、去掉结尾的点。</summary>
        public static string Normalize(string? host)
        {
            if (string.IsNullOrWhiteSpace(host)) return "";
            var h = host.Trim().TrimEnd('.');
            int colon = h.IndexOf(':');
            if (colon >= 0) h = h.Substring(0, colon);
            return h.ToLowerInvariant();
        }

        public AccelRule? Find(string? host)
        {
            var h = Normalize(host);
            if (h.Length == 0) return null;

            if (_exact.TryGetValue(h, out var exact)) return exact;

            foreach (var pair in _wildcards)
            {
                if (h.Length > pair.Key.Length && h.EndsWith(pair.Key, StringComparison.Ordinal))
                    return pair.Value;
            }

            return null;
        }
    }

    /// <summary>已经解析好的 HTTP/1.1 请求行与请求头。</summary>
    public sealed class HttpRequestHead
    {
        public string Method { get; private set; } = "GET";
        public string Path { get; private set; } = "/";
        public string Host { get; private set; } = "";
        public string Version { get; private set; } = "HTTP/1.1";
        public List<KeyValuePair<string, string>> Headers { get; } = new();
        public long ContentLength { get; private set; }
        public bool IsChunked { get; private set; }

        public bool IsUpgrade =>
            GetHeader("Upgrade") != null &&
            (GetHeader("Connection")?.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;

        public string? GetHeader(string name)
        {
            foreach (var header in Headers)
                if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return header.Value;
            return null;
        }

        public static async Task<HttpRequestHead> ReadAsync(Stream stream, CancellationToken ct)
        {
            var head = new HttpRequestHead();
            var line = await ReadLineAsync(stream, ct).ConfigureAwait(false)
                ?? throw new IOException("连接在发送请求前就被关闭");

            var parts = line.Split(' ');
            if (parts.Length >= 3)
            {
                head.Method = parts[0];
                head.Path = parts[1];
                head.Version = parts[2];
            }
            else if (parts.Length == 2)
            {
                head.Method = parts[0];
                head.Path = parts[1];
            }
            else
            {
                throw new IOException("无法解析请求行：" + line);
            }

            if (head.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(head.Path);                     // 绝对形式
                head.Host = uri.Host;
                head.Path = uri.PathAndQuery;
            }

            while (true)
            {
                var headerLine = await ReadLineAsync(stream, ct).ConfigureAwait(false);
                if (headerLine == null) throw new IOException("请求头不完整");
                if (headerLine.Length == 0) break;

                int colon = headerLine.IndexOf(':');
                if (colon <= 0) continue;

                var name = headerLine.Substring(0, colon).Trim();
                var value = headerLine.Substring(colon + 1).Trim();
                head.Headers.Add(new KeyValuePair<string, string>(name, value));

                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) head.Host = value;
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(value, out var length)) head.ContentLength = length;
                if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                    value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) head.IsChunked = true;
            }

            if (head.Host.Length == 0) throw new IOException("请求缺少 Host 头");
            if (head.Host.Contains(':')) head.Host = head.Host.Substring(0, head.Host.IndexOf(':'));

            return head;
        }

        /// <summary>
        /// 按原始格式重新拼出请求头，供转发生效。
        /// 会顺手处理 Host 覆盖、剥 Cookie、补 X-Forwarded-For，并统一成 Connection: close
        /// （保持简单可靠：每条客户端连接对应一条上游连接，由 Connection: close 收尾）。
        /// </summary>
        public byte[] ToWireBytes(string? overrideHost, bool stripCookies, string? clientIp)
        {
            var builder = new StringBuilder(1024);
            builder.Append(Method).Append(' ').Append(Path).Append(' ').Append(Version).Append("\r\n");

            bool hostWritten = false;
            bool forwardedWritten = false;

            foreach (var header in Headers)
            {
                var name = header.Key;

                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append("Host: ").Append(overrideHost ?? header.Value).Append("\r\n");
                    hostWritten = true;
                    continue;
                }

                if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase))
                {
                    continue;                                        // 由我们统一指定
                }

                if (stripCookies && name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue;

                if (name.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append("X-Forwarded-For: ").Append(header.Value);
                    if (!string.IsNullOrEmpty(clientIp)) builder.Append(", ").Append(clientIp);
                    builder.Append("\r\n");
                    forwardedWritten = true;
                    continue;
                }

                builder.Append(name).Append(": ").Append(header.Value).Append("\r\n");
            }

            if (!hostWritten) builder.Append("Host: ").Append(overrideHost ?? Host).Append("\r\n");
            if (!forwardedWritten && !string.IsNullOrEmpty(clientIp))
                builder.Append("X-Forwarded-For: ").Append(clientIp).Append("\r\n");

            builder.Append("Connection: close\r\n");
            builder.Append("\r\n");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[1];
            var text = new StringBuilder();

            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
                if (read <= 0) return text.Length == 0 ? null : text.ToString();

                char c = (char)buffer[0];
                if (c == '\n')
                {
                    if (text.Length > 0 && text[^1] == '\r') text.Length -= 1;
                    return text.ToString();
                }

                text.Append(c);
                if (text.Length > 16 * 1024) throw new IOException("请求头过长");
            }
        }
    }
}
