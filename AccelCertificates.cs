using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BetterWebLauncher
{
    /// <summary>
    /// 本地根证书 + 按需签发叶证书。
    ///
    /// 要点：
    ///   本地 CA 自签根证书，叶证书有效期 72 小时、按需签发（连到哪个域名就签哪个，
    ///   不预先签一整套）；
    /// 用 .NET 的 <see cref="CertificateRequest"/> 直接实现，不依赖外部代理程序。
    /// </summary>
    public sealed class CertificateAuthority
    {
        /// <summary>CA 主体名，装进“受信任的根证书颁发机构”后用户能看到这个名字。</summary>
        public const string CaCommonName = "BetterWebLauncher Local CA";

        /// <summary>叶证书有效期（小时）。</summary>
        private const int LeafLifetimeHours = 72;

        private const int CaLifetimeYears = 10;

        private readonly ConcurrentDictionary<string, X509Certificate2> _leafCache =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly object _signLock = new();

        private CertificateAuthority(X509Certificate2 root, string certPath, string keyPath)
        {
            Root = root;
            CertificatePath = certPath;
            KeyPath = keyPath;
        }

        /// <summary>根证书（含私钥）。</summary>
        public X509Certificate2 Root { get; }

        public string CertificatePath { get; }

        public string KeyPath { get; }

        /// <summary>根证书的 Base64 DER，可用于导出 .cer 给用户手工安装。</summary>
        public string RootBase64 => Convert.ToBase64String(Root.Export(X509ContentType.Cert));

        public string RootFingerprint => Root.Thumbprint;

        public DateTime RootNotAfter => Root.NotAfter;

        public static string Directory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterWebLauncher", "certs");

        public static string DefaultCertificatePath => Path.Combine(Directory, "bwl-ca.crt");
        public static string DefaultKeyPath => Path.Combine(Directory, "bwl-ca.key");

        /// <summary>加载已有 CA；不存在或损坏时重新生成（此时需要用户重新信任根证书）。</summary>
        public static CertificateAuthority LoadOrCreate()
        {
            var certPath = DefaultCertificatePath;
            var keyPath = DefaultKeyPath;

            try
            {
                if (File.Exists(certPath) && File.Exists(keyPath))
                {
                    var loaded = LoadFromPem(certPath, keyPath);
                    if (loaded != null && loaded.HasPrivateKey && loaded.NotAfter > DateTime.Now.AddDays(30))
                        return new CertificateAuthority(loaded, certPath, keyPath);
                }
            }
            catch
            {
                // 落到重新生成
            }

            var created = CreateNew();
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(certPath, PemExportCertificate(created), new UTF8Encoding(false));
                File.WriteAllText(keyPath, PemExportPrivateKey(created), new UTF8Encoding(false));
            }
            catch
            {
                // 落盘失败不影响本次运行，只是下次启动会重新生成
            }

            return new CertificateAuthority(created, certPath, keyPath);
        }

        /// <summary>取（或签发）指定主机名的叶证书。</summary>
        public X509Certificate2 GetLeaf(string host)
        {
            var normalized = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            if (normalized.Length == 0) normalized = "localhost";

            if (_leafCache.TryGetValue(normalized, out var cached))
                return cached;

            lock (_signLock)
            {
                if (_leafCache.TryGetValue(normalized, out cached))
                    return cached;

                var leaf = SignLeaf(normalized);
                _leafCache[normalized] = leaf;
                return leaf;
            }
        }

        /// <summary>把根证书装进当前用户的“受信任的根证书颁发机构”。</summary>
        public void TrustForCurrentUser()
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            // 同名同指纹的直接跳过；先把同名不同指纹的旧证书清掉，避免多个有效 CA 混淆
            var stale = store.Certificates
                .Where(c => string.Equals(c.Subject, Root.Subject, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(c.Thumbprint, Root.Thumbprint, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var c in stale)
            {
                try { store.Remove(c); } catch { /* 忽略单个删除失败 */ }
            }

            if (!store.Certificates.Any(c => string.Equals(c.Thumbprint, Root.Thumbprint, StringComparison.OrdinalIgnoreCase)))
                store.Add(new X509Certificate2(Root.Export(X509ContentType.Cert)));

            store.Close();
        }

        /// <summary>根证书是否已在“当前用户 → 受信任的根证书颁发机构”里。</summary>
        public bool IsTrustedForCurrentUser()
        {
            try
            {
                using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Any(c =>
                    string.Equals(c.Thumbprint, Root.Thumbprint, StringComparison.OrdinalIgnoreCase));
                store.Close();
                return found;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>导出 .cer 文件到桌面，供用户手工安装。</summary>
        public string ExportRootToFile()
        {
            var file = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "BetterWebLauncher-CA.cer");
            File.WriteAllBytes(file, Root.Export(X509ContentType.Cert));
            return file;
        }

        // ------------------------------------------------------------------
        // 签发
        // ------------------------------------------------------------------
        private X509Certificate2 SignLeaf(string host)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                new X500DistinguishedName("CN=" + host),
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));   // serverAuth
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(host);
            if (!host.StartsWith("*.", StringComparison.Ordinal) && host.IndexOf('.') > 0)
                san.AddDnsName("*." + host.Substring(host.IndexOf('.') + 1));
            if (System.Net.IPAddress.TryParse(host, out var ip))
                san.AddIpAddress(ip);
            request.CertificateExtensions.Add(san.Build());

            var serial = new byte[16];
            RandomNumberGenerator.Fill(serial);
            serial[0] &= 0x7F;                                  // 保证正数
            if (serial.All(b => b == 0)) serial[0] = 1;

            var notBefore = DateTimeOffset.UtcNow.AddHours(-1);   // 容忍客户端时钟偏差
            var notAfter = DateTimeOffset.UtcNow.AddHours(LeafLifetimeHours);

            using var signed = request.Create(Root, notBefore, notAfter, serial);
            return signed.CopyWithPrivateKey(rsa);
        }

        // ------------------------------------------------------------------
        // 生成 / PEM 读写
        // ------------------------------------------------------------------
        private static X509Certificate2 CreateNew()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                new X500DistinguishedName("CN=" + CaCommonName + ", O=BetterWebLauncher"),
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            var serial = new byte[16];
            RandomNumberGenerator.Fill(serial);
            serial[0] &= 0x7F;
            if (serial.All(b => b == 0)) serial[0] = 1;

            using var generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(CaLifetimeYears));

            // 走一遍 PKCS#12 让私钥真正可用（某些平台上自签名证书的私钥需要这一步）
            var pfx = generated.Export(X509ContentType.Pfx);
            return new X509Certificate2(pfx, (string?)null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }

        private static X509Certificate2? LoadFromPem(string certPath, string keyPath)
        {
            var certPem = File.ReadAllText(certPath);
            var keyPem = File.ReadAllText(keyPath);

            var cert = X509Certificate2.CreateFromPem(certPem, keyPem);
            var pfx = cert.Export(X509ContentType.Pfx);
            cert.Dispose();
            return new X509Certificate2(pfx, (string?)null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }

        private static string PemExportCertificate(X509Certificate2 cert)
        {
            var builder = new StringBuilder();
            builder.AppendLine("-----BEGIN CERTIFICATE-----");
            builder.AppendLine(Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks));
            builder.AppendLine("-----END CERTIFICATE-----");
            return builder.ToString();
        }

        private static string PemExportPrivateKey(X509Certificate2 cert)
        {
            // 根证书固定用 RSA 2048，取出私钥后导出 PKCS#8 DER 再套 PEM 外壳
            using var rsa = cert.GetRSAPrivateKey()
                ?? throw new InvalidOperationException("根证书没有可用的 RSA 私钥");

            var builder = new StringBuilder();
            builder.AppendLine("-----BEGIN PRIVATE KEY-----");
            builder.AppendLine(Convert.ToBase64String(
                rsa.ExportPkcs8PrivateKey(), Base64FormattingOptions.InsertLineBreaks));
            builder.AppendLine("-----END PRIVATE KEY-----");
            return builder.ToString();
        }
    }
}
