using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterWebLauncher
{
    /// <summary>
    /// hosts 文件改写。
    ///
    /// 加速的原理：把目标域名解析到 127.0.0.1，
    /// 让流量进入本地代理，由代理按域名选一条能通的线路出去。
    /// 我们只在 hosts 里维护自己标记出的一段，绝不动用户原有的条目：
    ///
    ///   # BetterWebLauncher BEGIN &lt;token&gt;
    ///   127.0.0.1 github.com #BWL
    ///   ...
    ///   # BetterWebLauncher END &lt;token&gt;
    /// </summary>
    public static class HostsFile
    {
        private const string MarkerPrefix = "# BetterWebLauncher ";
        private const string EntrySuffix = " #BWL";

        /// <summary>本次运行的标记（重启程序后旧标记会被自动清理）。</summary>
        public static readonly string Token = Guid.NewGuid().ToString("N").Substring(0, 16);

        private static string BeginMarker => MarkerPrefix + "BEGIN " + Token;
        private static string EndMarker => MarkerPrefix + "END " + Token;

        /// <summary>Windows 的 hosts 路径；开发机（macOS）上落到 /etc/hosts 便于调试。</summary>
        public static string FilePath
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    if (!string.IsNullOrEmpty(system32))
                        return System.IO.Path.Combine(system32, "drivers", "etc", "hosts");
                }

                return "/etc/hosts";
            }
        }

        /// <summary>默认代理地址。hosts 只能指定主机名，端口固定 443。</summary>
        public const string LoopbackAddress = "127.0.0.1";

        /// <summary>把域名列表写进 hosts。返回真正写入的条目数。</summary>
        public static int Apply(IEnumerable<string> domains)
        {
            var lines = new List<string> { BeginMarker };
            int count = 0;

            foreach (var raw in domains)
            {
                var domain = Normalize(raw);
                if (domain.Length == 0) continue;
                lines.Add(LoopbackAddress + " " + domain + EntrySuffix);
                count++;
            }

            lines.Add(EndMarker);

            var body = ReadAllLines();
            RemoveOurBlock(body);
            body.AddRange(lines);
            WriteAllLines(body);

            FlushDns();
            return count;
        }

        /// <summary>移除本程序写入的所有条目（含历史运行残留），返回是否有改动。</summary>
        public static bool Remove()
        {
            var body = ReadAllLines();
            if (!RemoveOurBlock(body)) return false;

            WriteAllLines(body);
            FlushDns();
            return true;
        }

        /// <summary>当前 hosts 里是否已经有本程序的条目。</summary>
        public static bool IsApplied()
        {
            foreach (var line in ReadAllLines())
                if (line.StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        /// <summary>当前 hosts 里被本程序劫持的域名数量。</summary>
        public static int CountApplied()
        {
            int count = 0;
            foreach (var line in ReadAllLines())
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
                if (trimmed.EndsWith(EntrySuffix, StringComparison.OrdinalIgnoreCase)) count++;
            }

            return count;
        }

        // ------------------------------------------------------------------
        // 内部实现
        // ------------------------------------------------------------------
        private static string Normalize(string? domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return "";
            var d = domain.Trim().ToLowerInvariant();
            if (d.StartsWith("*.", StringComparison.Ordinal)) return "";   // hosts 不支持通配
            if (d.IndexOf(' ') >= 0 || d.IndexOf('\t') >= 0) return "";
            if (d.IndexOf('#') >= 0) return "";
            return d;
        }

        /// <summary>删掉 BEGIN/END 之间的内容，以及任何一条带我们后缀的单行。</summary>
        private static bool RemoveOurBlock(List<string> lines)
        {
            bool changed = false;

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var trimmed = lines[i].TrimStart();

                if (trimmed.StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // 无论是 BEGIN 还是 END（哪怕来自上一次运行），一并清掉
                    lines.RemoveAt(i);
                    changed = true;
                    continue;
                }

                if (trimmed.EndsWith(EntrySuffix, StringComparison.OrdinalIgnoreCase))
                {
                    lines.RemoveAt(i);
                    changed = true;
                }
            }

            return changed;
        }

        private static List<string> ReadAllLines()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<string>();
                return new List<string>(File.ReadAllLines(FilePath));
            }
            catch
            {
                return new List<string>();
            }
        }

        private static void WriteAllLines(List<string> lines)
        {
            // hosts 要求 ASCII（非 ASCII 域名不会被解析）；原文件末尾统一补一个换行
            var text = new StringBuilder();
            foreach (var line in lines) text.Append(line).Append("\r\n");

            var bytes = Encoding.ASCII.GetBytes(text.ToString());
            File.WriteAllBytes(FilePath, bytes);
        }

        /// <summary>让解析缓存立即失效，否则改完 hosts 仍会走旧地址。</summary>
        public static void FlushDns()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    RunHidden("ipconfig", "/flushdns");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    RunHidden("dscacheutil", "-flushcache");
                }
                else
                {
                    RunHidden("resolvectl", "flush-caches");
                }
            }
            catch
            {
                // 刷新失败不影响 hosts 已生效的事实
            }
        }

        private static void RunHidden(string file, string args)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            process?.WaitForExit(5000);
        }

        /// <summary>当前进程是否有权限改 hosts（Windows 上必须管理员）。</summary>
        public static bool CanWrite()
        {
            try
            {
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                return stream.CanWrite;
            }
            catch
            {
                return false;
            }
        }
    }
}
