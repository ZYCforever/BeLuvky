using System;
using System.Windows.Forms;

namespace BetterWebLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var settings = AppSettings.Load();

                // 已保存密码 + 自动拨号 → 跳过登录窗口，直接进入主界面
                if (settings.RememberPassword &&
                    settings.AutoDialOnStart &&
                    !string.IsNullOrWhiteSpace(settings.Username) &&
                    !string.IsNullOrWhiteSpace(settings.Password))
                {
                    Application.Run(new MainForm(settings));
                    return;
                }

                using var login = new LoginForm(settings);
                if (login.ShowDialog() != DialogResult.OK) return;

                Application.Run(new MainForm(settings));
            }
            catch (Exception ex)
            {
                MessageBox.Show("程序启动失败：" + ex.Message, "BetterWebLauncher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}