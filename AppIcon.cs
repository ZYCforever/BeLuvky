using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace BetterWebLauncher
{
    /// <summary>
    /// 应用图标：优先使用内嵌的 app.ico（多尺寸），
    /// 单文件发布时回退到读取 exe 自身的图标资源。
    /// </summary>
    internal static class AppIcon
    {
        private static Icon? _large;
        private static Icon? _small;

        /// <summary>窗体 / 任务栏图标。</summary>
        public static Icon Large => _large ??= Load(0);

        /// <summary>托盘小图标。</summary>
        public static Icon Small => _small ??= Load(SystemInformation.SmallIconSize.Width);

        private static Icon Load(int size)
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("BetterWebLauncher.Assets.app.ico");
                if (stream != null)
                {
                    return size > 0 ? new Icon(stream, new Size(size, size)) : new Icon(stream);
                }
            }
            catch { /* 回退到下面的方案 */ }

            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path))
                {
                    var extracted = Icon.ExtractAssociatedIcon(path);
                    if (extracted != null) return extracted;
                }
            }
            catch { }

            return SystemIcons.Application;
        }
    }
}
