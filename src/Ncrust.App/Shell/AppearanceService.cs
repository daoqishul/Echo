using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ncrust.Shell
{
    /// <summary>
    /// 外观设置服务：背景材质（纯色 / 亚克力 / 云母）、深色黑度（纯黑 / 浅黑）、窗口圆角（Win11）。
    /// 实现方式：运行时直接改 Kanesumi 明暗 ThemeDictionaries 里的画刷实例（所有
    /// {ThemeResource} 引用处即时更新，不用重载资源）；窗口级材质用 Window.SystemBackdrop。
    /// </summary>
    internal static class AppearanceService
    {
        public const string BackdropKey = "backdrop";     // none | acrylic | mica
        public const string DarkStyleKey = "dark_style"; // black | soft
        public const string CornerKey = "corner";        // round | square

        private static readonly Color DarkBg = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        private static readonly Color DarkSurface = Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
        private static readonly Color DarkSurfaceVariant = Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
        private static readonly Color LightBg = Color.FromArgb(0xFF, 0xF6, 0xF2, 0xE9);
        private static readonly Color LightSurface = Color.FromArgb(0xFF, 0xFF, 0xFD, 0xF8);
        private static readonly Color LightSurfaceVariant = Color.FromArgb(0xFF, 0xED, 0xE6, 0xD8);

        private static readonly Color SoftDarkBg = Color.FromArgb(0xFF, 0x14, 0x14, 0x14);
        private static readonly Color SoftDarkSurface = Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F);
        private static readonly Color SoftDarkVariant = Color.FromArgb(0xFF, 0x26, 0x26, 0x26);

        private static ResourceDictionary _colorsDark;
        private static ResourceDictionary _colorsLight;
        private static ResourceDictionary _navDark;
        private static ResourceDictionary _navLight;

        /// <summary>外观变化（材质 / 黑度 / 主题）时通知：卡片、歌词页等需要不透明背景的层据此刷新。</summary>
        public static event Action Changed;

        public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

        /// <summary>当前主题（明暗）下的页面背景画刷实例：给根 Frame 用，避免主题切换后顶部露出系统白底。</summary>
        public static Brush CurrentBackgroundBrush()
        {
            Init();
            var dict = AppTheme.IsDark ? _colorsDark : _colorsLight;
            return dict != null && dict["KBackgroundBrush"] is Brush brush ? brush : null;
        }

        /// <summary>
        /// 永远不透明的背景画刷（颜色跟随当前主题与黑度设置）：播放器卡片与歌词页用。
        /// 背景材质（云母 / 亚克力）会把 KBackgroundBrush 调成半透明 —— 卡片与歌词页必须遮住主界面，
        /// 所以取当前背景色、强制 alpha=255。
        /// </summary>
        public static Brush OpaqueBackgroundBrush()
        {
            Init();
            var dict = AppTheme.IsDark ? _colorsDark : _colorsLight;
            if (dict != null && dict["KBackgroundBrush"] is SolidColorBrush brush)
            {
                var color = brush.Color;
                color.A = 255;
                return new SolidColorBrush(color);
            }

            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        }

        public static void Apply(Window window, string backdrop, string darkStyle, bool squareCorners)
        {
            Init();
            App.Trace($"appearance: backdrop={backdrop} darkStyle={darkStyle} corners={(squareCorners ? "square" : "round")} win11={IsWindows11}");

            // 1) 窗口级背景材质：云母 / 亚克力 / 纯色。
            window.SystemBackdrop = backdrop switch
            {
                "mica" => new MicaBackdrop(),
                "acrylic" => new DesktopAcrylicBackdrop(),
                _ => null,
            };

            // 2) 半透明画刷：让材质透出来（侧边栏与主界面同源 KBackgroundBrush / 面板画刷）。
            var translucent = backdrop != "none";
            var soft = darkStyle == "soft";
            if (_colorsDark != null)
            {
                Set(_colorsDark, translucent, soft, isDark: true);
                Set(_colorsLight, translucent, soft: false, isDark: false);
            }

            if (_navDark != null)
            {
                SetNav(_navDark, translucent, isDark: true);
                SetNav(_navLight, translucent, isDark: false);
            }

            // 3) 窗口圆角（Win10 上调用会失败，无害；设置项只在 Win11 显示）。
            ApplyCornerPreference(window, squareCorners);

            Changed?.Invoke();
        }

        private static void Init()
        {
            if (_colorsDark != null)
            {
                return;
            }

            // Kanesumi 的颜色 / 导航面板画刷在其合并字典的 ThemeDictionaries 里（可能嵌在更深一层），递归查找。
            FindDictionaries(Application.Current.Resources.MergedDictionaries);
        }

        private static void FindDictionaries(IList<ResourceDictionary> collections)
        {
            foreach (var merged in collections)
            {
                var themeDictionaries = merged.ThemeDictionaries;
                if (themeDictionaries != null)
                {
                    var dark = themeDictionaries.ContainsKey("Dark") ? themeDictionaries["Dark"] as ResourceDictionary : null;
                    var light = themeDictionaries.ContainsKey("Light") ? themeDictionaries["Light"] as ResourceDictionary : null;
                    if (dark != null && light != null)
                    {
                        if (_colorsDark == null && dark.ContainsKey("KBackgroundBrush"))
                        {
                            _colorsDark = dark;
                            _colorsLight = light;
                        }
                        else if (_navDark == null && dark.ContainsKey("NavigationViewExpandedPaneBackground"))
                        {
                            _navDark = dark;
                            _navLight = light;
                        }
                    }
                }

                if (merged.MergedDictionaries != null)
                {
                    FindDictionaries(merged.MergedDictionaries);
                }
            }
        }

        private static void Set(ResourceDictionary dict, bool translucent, bool soft, bool isDark)
        {
            var bg = isDark ? (soft ? SoftDarkBg : DarkBg) : LightBg;
            var surface = isDark ? (soft ? SoftDarkSurface : DarkSurface) : LightSurface;
            var variant = isDark ? (soft ? SoftDarkVariant : DarkSurfaceVariant) : LightSurfaceVariant;

            if (translucent)
            {
                bg.A = isDark ? (byte)0xC8 : (byte)0xE0;
                surface.A = isDark ? (byte)0xD0 : (byte)0xE8;
                variant.A = isDark ? (byte)0xD0 : (byte)0xE8;
            }

            SetBrush(dict, "KBackgroundBrush", bg);
            SetBrush(dict, "KSurfaceBrush", surface);
            SetBrush(dict, "KSurfaceVariantBrush", variant);
        }

        private static void SetNav(ResourceDictionary dict, bool translucent, bool isDark)
        {
            var expanded = isDark
                ? Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
                : Color.FromArgb(0xFF, 0xF6, 0xF2, 0xE9);
            var floating = isDark
                ? Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)
                : Color.FromArgb(0xFF, 0xFF, 0xFD, 0xF8);

            if (translucent)
            {
                expanded.A = isDark ? (byte)0xB4 : (byte)0xDC;
                floating.A = isDark ? (byte)0xBE : (byte)0xE4;
            }

            SetBrush(dict, "NavigationViewExpandedPaneBackground", expanded);
            SetBrush(dict, "NavigationViewDefaultPaneBackground", floating);
            SetBrush(dict, "NavigationViewTopPaneBackground", expanded);
        }

        private static void SetBrush(ResourceDictionary dict, string key, Color color)
        {
            if (dict[key] is SolidColorBrush brush)
            {
                brush.Color = color;
            }
            else
            {
                dict[key] = new SolidColorBrush(color);
            }
        }

        // DWMWA_WINDOW_CORNER_PREFERENCE = 33；DWMWCP_DONOTROUND = 1、DWMWCP_ROUND = 2。
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

        private static void ApplyCornerPreference(Window window, bool square)
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                var preference = square ? 1 : 2;
                DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
            }
            catch
            {
                // Win10 无此属性：忽略。
            }
        }
    }
}
