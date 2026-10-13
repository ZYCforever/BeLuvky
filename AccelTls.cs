using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BetterWebLauncher
{
    /// <summary>
    /// 从 TLS ClientHello 中读出 SNI。
    ///
    /// S302 能用一条 443 端口把几十个域名分流出去，靠的就是 SNI：
    /// 客户端先发 ClientHello，我们从中知道对方想访问谁，再决定往哪个上游转发。
    /// </summary>
    public static class SniReader
    {
        private const int RecordHeaderLength = 5;
        private const int HandshakeHeaderLength = 4;

        /// <summary>
        /// 读到一条完整的 ClientHello 为止。
        /// 返回 (sni, data)：data 是已经从流里读掉的全部字节，调用方必须原样转发给上游，
        /// 否则上游收到的 ClientHello 会残缺。sni 为 null 表示客户端未发送 SNI。
        /// </summary>
        public static async Task<(string? Sni, byte[] Data)> ReadAsync(
            Stream stream, int maxBytes, CancellationToken ct)
        {
            var buffer = new byte[4096];
            int filled = 0;

            while (true)
            {
                int consumed = Inspect(buffer, filled);

                if (consumed == -1)
                {
                    // 需要更多数据：扩容后继续读
                    if (filled == buffer.Length)
                    {
                        if (buffer.Length >= maxBytes) return (null, Trim(buffer, filled));
                        Array.Resize(ref buffer, Math.Min(buffer.Length * 2, maxBytes));
                    }

                    int read = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
                    if (read <= 0) return (null, Trim(buffer, filled));
                    filled += read;
                    continue;
                }

                var sni = ParseServerName(buffer.AsSpan(0, consumed));
                // 必须把已读到的全部字节都交给上游（可能有粘包），只按已解析的记录判断 SNI
                return (sni, Trim(buffer, filled));
            }
        }

        /// <summary>
        /// 返回 -1 表示数据还不够；否则返回第一条完整记录的结束位置（含记录头）。
        /// </summary>
        private static int Inspect(byte[] buffer, int filled)
        {
            int offset = 0;

            while (filled - offset >= RecordHeaderLength)
            {
                if (buffer[offset] != 0x16) return offset + RecordHeaderLength;  // 非 handshake，交给上层
                int recordLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 3, 2));
                if (recordLength > 16 * 1024) return offset + RecordHeaderLength;

                int recordEnd = offset + RecordHeaderLength + recordLength;
                if (filled < recordEnd) return -1;

                // 记录头之后是 handshake 消息
                if (buffer[offset + 5] == 0x01)
                {
                    if (recordEnd - (offset + RecordHeaderLength) < HandshakeHeaderLength) return -1;
                    int bodyLength = (buffer[offset + 6] << 16) | (buffer[offset + 7] << 8) | buffer[offset + 8];
                    int messageEnd = offset + RecordHeaderLength + HandshakeHeaderLength + bodyLength;
                    if (filled < messageEnd) return -1;               // ClientHello 跨记录，继续读
                    return messageEnd;
                }

                offset = recordEnd;
            }

            return -1;
        }

        /// <summary>
        /// 决定用哪个主机名签证书。
        /// SNI 优先；客户端没带 SNI 时退回规则里的第一个具体域名（通配写法要去掉星号）。
        /// </summary>
        public static string PickCertificateHost(string? sni, string fallbackHost)
        {
            var candidate = string.IsNullOrWhiteSpace(sni) ? fallbackHost : sni!;
            candidate = candidate.Trim().TrimEnd('.');
            return candidate.Length == 0 ? "localhost" : candidate;
        }

        private static byte[] Trim(byte[] buffer, int length) => buffer.AsSpan(0, length).ToArray();

        private static string? ParseServerName(ReadOnlySpan<byte> data)
        {
            var handshake = data.Slice(RecordHeaderLength);
            if (handshake.Length < HandshakeHeaderLength || handshake[0] != 0x01) return null;

            int bodyLength = (handshake[1] << 16) | (handshake[2] << 8) | handshake[3];
            if (handshake.Length < HandshakeHeaderLength + bodyLength) return null;

            var body = handshake.Slice(HandshakeHeaderLength, bodyLength);
            if (body.Length < 34) return null;

            int offset = 34;                                        // legacy_version(2) + random(32)

            if (offset >= body.Length) return null;
            offset += 1 + body[offset];                             // legacy_session_id

            if (offset + 2 > body.Length) return null;
            offset += 2 + BinaryPrimitives.ReadUInt16BigEndian(body.Slice(offset, 2));   // cipher_suites

            if (offset >= body.Length) return null;
            offset += 1 + body[offset];                             // legacy_compression_methods

            if (offset + 2 > body.Length) return null;
            int extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(offset, 2));
            offset += 2;

            int extensionsEnd = Math.Min(body.Length, offset + extensionsLength);
            while (offset + 4 <= extensionsEnd)
            {
                int type = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(offset, 2));
                int length = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(offset + 2, 2));
                offset += 4;
                if (offset + length > body.Length) return null;

                if (type == 0x0000 && length >= 5)                  // server_name
                {
                    var ext = body.Slice(offset, length);
                    int nameType = ext[2];
                    int nameLength = BinaryPrimitives.ReadUInt16BigEndian(ext.Slice(3, 2));
                    if (nameType == 0 && 5 + nameLength <= ext.Length)
                        return Encoding.ASCII.GetString(ext.Slice(5, nameLength));
                }

                offset += length;
            }

            return null;
        }
    }

    /// <summary>记录一次已建立 / 失败的转发，用于日志。</summary>
    public sealed class AccelLogEntry
    {
        public DateTime Time { get; init; } = DateTime.Now;
        public string Host { get; init; } = "";
        public string Upstream { get; init; } = "";
        public string RuleId { get; init; } = "";
        public bool Success { get; init; }
        public int StatusCode { get; init; }
        public long ElapsedMs { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>运行期累计统计，展示在界面上。</summary>
    public sealed class AccelStats
    {
        private long _total;
        private long _failed;
        private long _active;

        public long Total => Interlocked.Read(ref _total);
        public long Failed => Interlocked.Read(ref _failed);
        public long Active => Interlocked.Read(ref _active);

        public void AddRequest() => Interlocked.Increment(ref _total);
        public void AddFailure() => Interlocked.Increment(ref _failed);
        public void Enter() => Interlocked.Increment(ref _active);
        public void Leave() => Interlocked.Decrement(ref _active);
    }
}
