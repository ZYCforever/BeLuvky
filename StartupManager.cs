using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace BetterWebLauncher
{
    /// <summary>通过 HKCU\...\Run 实现开机自启（仅当前用户，无需管理员权限）。</summary>
    public static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "BetterWebLauncher";

        public static void SetStartup(bool enable)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                key.SetValue(ValueName, "\"" + ResolveExecutablePath() + "\"");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }

        public static bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        private static string ResolveExecutablePath()
        {
            var exe = Environment.ProcessPath ?? string.Empty;

            if (string.IsNullOrEmpty(exe) ||
                Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var loc = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(loc))
                    exe = Path.ChangeExtension(loc, ".exe");
            }

            return exe;
        }
    }
}