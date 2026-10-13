using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterWebLauncher
{
    /// <summary>程序配置。密码使用 AES 加密后存储，避免明文落盘。</summary>
    public sealed class AppSettings
    {
        private static readonly byte[] Salt =
            Encoding.UTF8.GetBytes("BetterWebLauncher::v1::salt");

        /// <summary>断线重连间隔默认值（秒）。</summary>
        public const int DefaultReconnectIntervalSeconds = 5;
        public const int MinReconnectIntervalSeconds = 1;
        public const int MaxReconnectIntervalSeconds = 3600;

        public string ConnectionName { get; set; } = "宽带连接";
        public string Username { get; set; } = "";
        public string PasswordCipher { get; set; } = "";

        public bool RememberPassword { get; set; } = true;
        public bool AutoDialOnStart { get; set; } = true;
        public bool AutoReconnect { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;

        /// <summary>断线自动重连的尝试间隔（秒）：失败后每隔这么久重试一次。</summary>
        public int ReconnectIntervalSeconds { get; set; } = DefaultReconnectIntervalSeconds;

        // ---------- 网站访问加速 ----------

        /// <summary>是否启用网站访问加速（本地代理 + hosts 劫持）。</summary>
        public bool AccelerationEnabled { get; set; } = false;

        /// <summary>是否同时加速 GitHub 系列域名。</summary>
        public bool AccelGitHub { get; set; } = true;

        /// <summary>旧版本的 Discord 开关，保留字段只为读得懂老配置文件；当前不启用。</summary>
        public bool AccelDiscord { get; set; } = false;

        /// <summary>是否同时加速 Steam 系列域名。</summary>
        public bool AccelSteam { get; set; } = true;

        /// <summary>是否同时加速 Twitch 系列域名。</summary>
        public bool AccelTwitch { get; set; } = true;

        /// <summary>本地监听端口。hosts 只能劫持到 443，一般不需要改。</summary>
        public int AccelPort { get; set; } = 443;

        /// <summary>已把本地根证书装进“受信任的根证书颁发机构”。</summary>
        public bool AccelCaTrusted { get; set; } = false;

        /// <summary>把间隔限制在合法范围内（配置文件被手工改坏时也能兜住）。</summary>
        public static int ClampReconnectInterval(int seconds)
        {
            if (seconds < MinReconnectIntervalSeconds) return MinReconnectIntervalSeconds;
            if (seconds > MaxReconnectIntervalSeconds) return MaxReconnectIntervalSeconds;
            return seconds;
        }

        [JsonIgnore]
        public string Password
        {
            get => Decrypt(PasswordCipher);
            set => PasswordCipher = Encrypt(value ?? string.Empty);
        }

        private static string SettingsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterWebLauncher");

        private static string SettingsFile => Path.Combine(SettingsDir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile, Encoding.UTF8);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) return s;
                }
            }
            catch { /* 配置损坏则使用默认值 */ }

            return new AppSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(SettingsFile, json, Encoding.UTF8);
        }

        // ---------- 加解密 ----------
        private static byte[] DeriveKey()
        {
            using var kdf = new Rfc2898DeriveBytes(
                Environment.MachineName + "|" + Environment.UserName,
                Salt, 50_000, HashAlgorithmName.SHA256);
            return kdf.GetBytes(32);
        }

        private static string Encrypt(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return string.Empty;
            try
            {
                using var aes = Aes.Create();
                aes.Key = DeriveKey();
                aes.GenerateIV();

                var data = Encoding.UTF8.GetBytes(plain);
                using var enc = aes.CreateEncryptor();
                var cipher = enc.TransformFinalBlock(data, 0, data.Length);

                var buffer = new byte[aes.IV.Length + cipher.Length];
                Buffer.BlockCopy(aes.IV, 0, buffer, 0, aes.IV.Length);
                Buffer.BlockCopy(cipher, 0, buffer, aes.IV.Length, cipher.Length);
                return Convert.ToBase64String(buffer);
            }
            catch { return string.Empty; }
        }

        private static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;
            try
            {
                var buffer = Convert.FromBase64String(cipherText);
                using var aes = Aes.Create();
                aes.Key = DeriveKey();

                var iv = new byte[16];
                Buffer.BlockCopy(buffer, 0, iv, 0, 16);
                aes.IV = iv;

                using var dec = aes.CreateDecryptor();
                var plain = dec.TransformFinalBlock(buffer, 16, buffer.Length - 16);
                return Encoding.UTF8.GetString(plain);
            }
            catch { return string.Empty; }
        }
    }
}