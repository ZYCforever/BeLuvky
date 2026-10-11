using System;
using System.Diagnostics;

namespace BetterWebLauncher
{
    /// <summary>通过 rasdial 命令拨号 / 断开，并翻译常见错误码。</summary>
    public static class Dialer
    {
        public static (bool Success, string Message) Connect(
            string connectionName, string username, string password)
        {
            return RunRasdial(new[] { connectionName, username, password });
        }

        public static (bool Success, string Message) Disconnect(string connectionName)
        {
            return RunRasdial(new[] { connectionName, "/disconnect" });
        }

        private static (bool Success, string Message) RunRasdial(string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo("rasdial")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                foreach (var a in args) psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p == null) return (false, "无法启动 rasdial 进程。");

                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();

                if (!p.WaitForExit(30_000))
                {
                    try { p.Kill(); } catch { }
                    return (false, "拨号超时（30 秒）。请检查网线、光猫或宽带线路。");
                }

                int code = p.ExitCode;

                // 813：该连接已存在；视为成功
                if (code == 0 || code == 813)
                    return (true, "连接成功。");

                return (false, DescribeError(code));
            }
            catch (Exception ex)
            {
                return (false, "拨号异常：" + ex.Message);
            }
        }

        private static string DescribeError(int code) => code switch
        {
            691 => "身份验证失败：用户名或密码错误（691）。",
            678 => "服务器无响应：请检查网线、光猫或宽带线路（678）。",
            651 => "调制解调器报告错误，设备无响应（651）。",
            623 => "找不到电话簿条目：连接名称可能填写不正确（623）。",
            629 => "连接被远程计算机断开（629）。",
            720 => "PPP 协商失败，可能是协议或驱动问题（720）。",
            868 => "找不到域名（868）。",
            619 => "端口已断开（619）。",
            692 => "硬件故障（692）。",
            _ => $"拨号失败，错误代码 {code}。"
        };
    }
}