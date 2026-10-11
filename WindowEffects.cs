using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace BetterWebLauncher
{
    /// <summary>
    /// Windows 窗口视觉效果：亚克力（毛玻璃）模糊背景 + 圆角 + 深色标题栏。
    ///
    /// 两种实现方式：
    ///   ① Win11 原生 DWM 背景（DWMWA_SYSTEMBACKDROP_TYPE）——性能最好；
    ///   ② SetWindowCompositionAttribute 的 ACCENT_ENABLE_ACRYLICBLURBEHIND——
    ///      Win10 1803+ 可用，且整窗半透明，毛玻璃观感更明显。
    ///
    /// 注意：亚克力只会出现在「程序没有绘制像素」的地方，
    /// 因此窗体需要重写 OnPaintBackground 并且不填充背景色。
    /// </summary>
    public static class WindowEffects
    {
        // ---------------- DWM ----------------
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE      = 38;

        private const int DWMWCP_DONOTROUND  = 1;
        private const int DWMWCP_ROUND       = 2;   // 标准圆角（约 8px）
        private const int DWMWCP_ROUNDSMALL  = 3;   // 小圆角（约 4px）

        private const int DWMSBT_MAINWINDOW      = 2; // 云母
        private const int DWMSBT_TRANSIENTWINDOW = 3; // 亚克力
        private const int DWMSBT_TABBEDWINDOW    = 4;

        // ---------------- SetWindowCompositionAttribute ----------------
        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(
            IntPtr hwnd, ref WindowCompositionAttributeData data);

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        private const int WCA_ACCENT_POLICY               = 19;
        private const int ACCENT_ENABLE_BLURBEHIND        = 3;
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

        // ---------------- uxtheme（让日志框滚动条也变深色）----------------
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

        /// <summary>把控件的滚动条切换为深色主题（Win10 1809+）。</summary>
        public static void ApplyDarkScrollBars(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try { SetWindowTheme(hwnd, "DarkMode_Explorer", null); } catch { }
        }

        /// <summary>true 时只使用 Win11 原生背景（性能更好，但只有未绘制区域透出模糊）。</summary>
        public static bool UseNativeBackdropOnly { get; set; }

        /// <summary>
        /// 亚克力染色层的不透明度：数值越大颜色越实、文字越清晰，越小越通透。
        /// 推荐 140 ~ 235，默认 170（深色毛玻璃，能明显看到背景）。
        /// </summary>
        public static byte AcrylicOpacity { get; set; } = 170;

        public static bool IsWindows11OrLater =>
            Environment.OSVersion.Version.Build >= 22000;

        /// <summary>应用 Win11 / macOS 风格圆角。</summary>
        public static void ApplyRoundedCorners(IntPtr hwnd, bool small = false)
        {
            if (!IsWindows11OrLater || hwnd == IntPtr.Zero) return;
            int pref = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }

        /// <summary>应用亚克力毛玻璃背景（半透明）。</summary>
        public static void ApplyAcrylic(IntPtr hwnd, Color tint, byte opacity = 200)
        {
            if (hwnd == IntPtr.Zero) return;

            bool nativeApplied = false;

            // ① Win11 原生亚克力背景
            if (IsWindows11OrLater)
            {
                int backdrop = DWMSBT_TRANSIENTWINDOW;
                nativeApplied = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
            }

            if (UseNativeBackdropOnly && nativeApplied) return;

            // ② SetWindowCompositionAttribute：整窗半透明 + 模糊
            //    GradientColor 格式：AABBGGRR，A 越大越不透明（文字越清晰）
            int gradientColor = (opacity << 24) | (tint.B << 16) | (tint.G << 8) | tint.R;

            var accent = new AccentPolicy
            {
                AccentState   = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags   = 2,
                GradientColor = gradientColor,
                AnimationId   = 0
            };

            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute  = WCA_ACCENT_POLICY,
                    Data       = ptr,
                    SizeOfData = size
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            catch
            {
                // 系统过旧不支持时忽略，保持默认外观
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>设置深色标题栏。</summary>
        public static void ApplyDarkTitleBar(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            int v = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
        }

        /// <summary>一次性应用整套效果（圆角 + 亚克力 + 深色标题栏）。</summary>
        public static void ApplyAll(IntPtr hwnd, Color tint, byte opacity = 200,
                                    bool darkTitleBar = true, bool smallCorners = false)
        {
            ApplyRoundedCorners(hwnd, smallCorners);
            ApplyAcrylic(hwnd, tint, opacity);
            ApplyDarkTitleBar(hwnd, darkTitleBar);
        }
    }
}
