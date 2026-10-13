using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace BetterWebLauncher
{
    /// <summary>
    /// 上游连接工厂。
    ///
    /// 对照 S302（Caddy）的两个关键设置：
    ///   tls_server_name &lt;sni&gt;                       → 这里通过 SslClientAuthenticationOptions.TargetHost 实现
    ///   tls_trusted_ca_certs / skip 证书校验            → 这里始终接受上游证书（只加密、不校验链）
    /// 上游证书不校验是刻意的：SNI 前置时证书本来就和主机名对不上。
    /// </summary>
    public sealed class AccelUpstreamConnector
    {
        private readonly Func<string, string?> _resolveHost;

        /// <param name="resolveHost">
        /// 把「客户端请求的主机名」映射成实际上游主机名。
        /// hosts 已经被改写时本地 DNS 会返回 127.0.0.1，所以必须由引擎提供原始域名。
        /// </param>
        public AccelUpstreamConnector(Func<string, string?> resolveHost)
        {
            _resolveHost = resolveHost;
        }

        /// <summary>TCP 连接（未加密），host 可以是 IP 或域名。</summary>
        public async Task<TcpClient> ConnectTcpAsync(
            string host, int port, int timeoutMs, CancellationToken ct)
        {
            var client = new TcpClient { NoDelay = true };

            using var timeout = new CancellationTokenSource(Math.Max(timeoutMs, 500));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            try
            {
                await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 建立到上游的 TLS 连接。
        /// 若 <paramref name="preloaded"/> 不为空，则把其中已读到的字节先写过去，再开始 TLS 握手，
        /// 这样客户端早期的写入不会因为握手顺序问题被丢掉。
        /// </summary>
        public async Task<SslStream> ConnectTlsAsync(
            string connectHost,
            int port,
            string? sni,
            int timeoutMs,
            string? requestHost,
            CancellationToken ct,
            byte[]? preloaded = null)
        {
            var tcp = await ConnectTcpAsync(connectHost, port, timeoutMs, ct).ConfigureAwait(false);
            try
            {
                var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, AcceptAnyCertificate);

                var options = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    EncryptionPolicy = EncryptionPolicy.RequireEncryption,
                };

                if (!string.IsNullOrEmpty(sni))
                {
                    options.TargetHost = sni;
                }
                else if (!string.IsNullOrEmpty(requestHost))
                {
                    options.TargetHost = requestHost;
                }
                else
                {
                    // TargetHost 不能为空，用占位值；证书校验已被跳过，不影响握手
                    options.TargetHost = "localhost";
                }

                await ssl.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);

                if (preloaded != null && preloaded.Length > 0)
                {
                    // 正常情况下 preloaded 是空：调用方应在握手之后才发送数据
                    await ssl.WriteAsync(preloaded, ct).ConfigureAwait(false);
                }

                return ssl;
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        private static bool AcceptAnyCertificate(
            object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) => true;

        /// <summary>
        /// 把 hosts 改写后的域名还原成真实主机名。
        /// 例如 github.com 被写进 hosts 指向 127.0.0.1，但我们要连的还是 github.com 本身。
        /// </summary>
        public string? ResolveOriginalHost(string host) => _resolveHost(host);
    }

    /// <summary>
    /// 先吐出一段已经读进内存的字节，再接上底层流。
    ///
    /// 读 SNI 时我们已经把客户端的 ClientHello 从 socket 里读掉了；
    /// SslStream 接手后必须仍然看到这段 ClientHello，
    /// 否则它会一直等一条永远不会再来的握手记录（表现为握手挂死）。
    /// </summary>
    internal sealed class PrependStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _prefix;
        private int _offset;

        public PrependStream(Stream inner, byte[] prefix)
        {
            _inner = inner;
            _prefix = prefix ?? Array.Empty<byte>();
        }

        /// <summary>前缀里还没交出去的字节数（调试用）。</summary>
        public int Pending => _prefix.Length - _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_offset < _prefix.Length)
            {
                int take = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsMemory(_offset, take).CopyTo(buffer);
                _offset += take;
                return new ValueTask<int>(take);
            }

            return _inner.ReadAsync(buffer, ct);
        }

        public override void Write(byte[] buffer, int offset, int count)
            => _inner.Write(buffer, offset, count);

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
    }

    /// <summary>
    /// 只允许读取前 N 个字节的流。用于把请求体精确限制在 Content-Length 之内，
    /// 避免 HttpClient 把后续请求的字节也当成 body 读走。
    /// </summary>
    internal sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _leaveOpen;
        private long _remaining;

        public LimitedReadStream(Stream inner, long length, bool leaveOpen)
        {
            _inner = inner;
            _remaining = length;
            _leaveOpen = leaveOpen;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_remaining <= 0) return 0;
            int want = (int)Math.Min(count, _remaining);
            int read = await _inner.ReadAsync(buffer.AsMemory(offset, want), ct).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_remaining <= 0) return 0;
            int want = (int)Math.Min(buffer.Length, _remaining);
            int read = await _inner.ReadAsync(buffer.Slice(0, want), ct).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// 把 HTTP/1.1 chunked 请求体还原成连续字节流。
    /// 客户端往本地 443 发大文件时普遍用 chunked，直接当裸流转发会读到 chunk 头。
    /// </summary>
    internal sealed class ChunkedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _chunkRemaining;
        private bool _finished;

        public ChunkedReadStream(Stream inner) => _inner = inner;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => await ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_finished) return 0;

            if (_chunkRemaining == 0)
            {
                if (!await MoveToNextChunkAsync(ct).ConfigureAwait(false)) return 0;
                if (_chunkRemaining == 0) { _finished = true; return 0; }
            }

            int want = (int)Math.Min(buffer.Length, _chunkRemaining);
            int read = await _inner.ReadAsync(buffer.Slice(0, want), ct).ConfigureAwait(false);
            if (read <= 0) { _finished = true; return 0; }

            _chunkRemaining -= read;
            if (_chunkRemaining == 0) await ConsumeChunkTerminatorAsync(ct).ConfigureAwait(false);
            return read;
        }

        private async Task<bool> MoveToNextChunkAsync(CancellationToken ct)
        {
            var line = await ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) return false;

            var text = line.Trim();
            int semicolon = text.IndexOf(';');                       // 忽略 chunk extension
            if (semicolon >= 0) text = text.Substring(0, semicolon);

            text = text.Trim();
            if (text.Length == 0) return false;

            try
            {
                _chunkRemaining = Convert.ToInt64(text, 16);
            }
            catch
            {
                return false;
            }

            if (_chunkRemaining <= 0)
            {
                // 末尾 chunk：读掉 trailer，直到空行
                while (true)
                {
                    var trailer = await ReadLineAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(trailer)) break;
                    if (trailer == null) break;
                }
                _finished = true;
                return false;
            }

            return true;
        }

        private async Task ConsumeChunkTerminatorAsync(CancellationToken ct)
        {
            var line = await ReadLineAsync(ct).ConfigureAwait(false);
            _ = line;                                                // 每个 chunk 后面应有 CRLF
        }

        /// <summary>逐字节读一行（含 CRLF 已剥离），EOF 返回 null。</summary>
        private async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var buffer = new byte[1];
            var text = new System.Text.StringBuilder();

            while (true)
            {
                int read = await _inner.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
                if (read <= 0) return text.Length == 0 ? null : text.ToString();

                char c = (char)buffer[0];
                if (c == '\n')
                {
                    if (text.Length > 0 && text[^1] == '\r') text.Length -= 1;
                    return text.ToString();
                }

                text.Append(c);
                if (text.Length > 1024) return null;                 // 防御：格式不对就放弃
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
