using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ncrust.Core.Audio;
using Ncrust.Playback;
using Ncrust.Shell;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Ncrust.Pages
{
    /// <summary>设置页（对应 Android UserScreen 的设置部分；账户资料已移到独立的账户页）。</summary>
    public sealed partial class SettingsPage : Page
    {
        /// <summary>与 Android strings.qualityOptions 一致，下标即持久化值（0..6）。</summary>
        private static readonly string[] QualityOptions = Ncrust.Resources.DisplayFormat.QualityLabels;

        private bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
            foreach (var option in QualityOptions)
            {
                WifiQualityBox.Items.Add(option);
                MeteredQualityBox.Items.Add(option);
            }

            VersionText.Text = "Echo 1.0";

            // 圆角设置只在 Windows 11 提供；Windows 10 没有系统圆角，直接隐藏（默认直角）。
            if (!AppearanceService.IsWindows11)
            {
                CornerBox.Visibility = Visibility.Collapsed;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // 从均衡器子页回来时刷新摘要；其余控件按当前设置回填（回填期间不触发写入）。
            _loading = true;
            var prefs = AppServices.PlayPrefs;
            WifiQualityBox.SelectedIndex = Clamp(prefs.WifiQualityIndex);
            MeteredQualityBox.SelectedIndex = Clamp(prefs.MobileQualityIndex);
            GaplessSwitch.IsOn = prefs.GaplessEnabled;
            TranslationSwitch.IsOn = prefs.LyricsTranslation;
            TrialPlayRadio.IsChecked = prefs.VipTrialPlayback;
            TrialSkipRadio.IsChecked = !prefs.VipTrialPlayback;
            ThemeSystemRadio.IsChecked = AppTheme.Mode == AppThemeMode.System;
            ThemeDarkRadio.IsChecked = AppTheme.Mode == AppThemeMode.Dark;
            ThemeLightRadio.IsChecked = AppTheme.Mode == AppThemeMode.Light;
            ProxySwitch.IsOn = AppServices.Settings.GetBool("use_system_proxy", false);
            CloseToTraySwitch.IsOn = AppServices.Settings.GetBool("close_to_tray", true);

            SelectByTag(BackdropBox, AppServices.Settings.GetString(AppearanceService.BackdropKey) ?? "none");
            SelectByTag(DarkStyleBox, AppServices.Settings.GetString(AppearanceService.DarkStyleKey) ?? "black");
            SelectByTag(CornerBox, AppServices.Settings.GetString(AppearanceService.CornerKey) ?? "round");
            SelectByTag(LyricsWidthBox, AppServices.Settings.GetString(Player.VinylView.WidthKey) ?? "standard");

            DownloadPathText.Text = Ncrust.Platform.DownloadService.DownloadFolder;
            _loading = false;

            UpdateEqualizerSummary();
            _ = UpdateCacheSizeAsync();
        }

        private static void SelectByTag(ComboBox box, string tag)
        {
            for (var i = 0; i < box.Items.Count; i++)
            {
                if ((box.Items[i] as ComboBoxItem)?.Tag as string == tag)
                {
                    box.SelectedIndex = i;
                    return;
                }
            }
        }

        // ── 托盘 ─────────────────────────────────────────────────────────────

        private void CloseToTrayToggled(object sender, RoutedEventArgs e)
        {
            if (!_loading)
            {
                AppServices.Settings.SetBool("close_to_tray", CloseToTraySwitch.IsOn);
            }
        }

        // ── 歌词页 ────────────────────────────────────────────────────────────

        private void LyricsWidthChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !(LyricsWidthBox.SelectedItem is ComboBoxItem item) || !(item.Tag is string tag))
            {
                return;
            }

            AppServices.Settings.SetString(Player.VinylView.WidthKey, tag);
        }

        // ── 音质 / 播放 ───────────────────────────────────────────────────────

        private void WifiQualityChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading && WifiQualityBox.SelectedIndex >= 0)
            {
                AppServices.PlayPrefs.WifiQualityIndex = WifiQualityBox.SelectedIndex;
            }
        }

        private void MeteredQualityChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading && MeteredQualityBox.SelectedIndex >= 0)
            {
                AppServices.PlayPrefs.MobileQualityIndex = MeteredQualityBox.SelectedIndex;
            }
        }

        private void GaplessToggled(object sender, RoutedEventArgs e)
        {
            if (!_loading)
            {
                AppServices.PlayPrefs.GaplessEnabled = GaplessSwitch.IsOn;
            }
        }

        private void TranslationToggled(object sender, RoutedEventArgs e)
        {
            if (!_loading)
            {
                AppServices.PlayPrefs.LyricsTranslation = TranslationSwitch.IsOn;
                Player.PlayerHost.NotifyLyricsSettingsChanged();
            }
        }

        // ── 网络 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 默认直连（不走系统代理）：代理链路经常干扰音乐流与加密接口，导致播放不正常。
        /// 开启后重启应用生效（请求层在启动时按此开关构建，见 AppServices.CreateHttp）。
        /// </summary>
        private void ProxyToggled(object sender, RoutedEventArgs e)
        {
            if (!_loading)
            {
                AppServices.Settings.SetBool("use_system_proxy", ProxySwitch.IsOn);
                ProxyHint.Visibility = ProxySwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>「非会员遇到 VIP 歌曲」：播放试听片段（默认）/ 直接跳过。引擎每次取链现读，立即生效。</summary>
        private void TrialModeChecked(object sender, RoutedEventArgs e)
        {
            if (_loading || !(sender is RadioButton radio) || !(radio.Tag is string tag))
            {
                return;
            }

            AppServices.PlayPrefs.VipTrialPlayback = tag == "play";
        }

        // ── 音效 ──────────────────────────────────────────────────────────────

        private void UpdateEqualizerSummary()
        {
            if (!PlaybackHost.Engine.EqualizerAvailable)
            {
                EqualizerSummary.Text = "当前系统不支持挂载音效，均衡器不可用";
                return;
            }

            var state = AppServices.Equalizer.LoadState();
            EqualizerSummary.Text = state.Enabled
                ? "已开启 · " + (EqualizerPage.DescribePreset(state) ?? "自定义")
                : "已关闭";
        }

        private void EqualizerClick(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(EqualizerPage));

        // ── 外观 ──────────────────────────────────────────────────────────────

        private void ThemeChecked(object sender, RoutedEventArgs e)
        {
            if (_loading || !(sender is RadioButton radio) || !(radio.Tag is string tag))
            {
                return;
            }

            if (Enum.TryParse<AppThemeMode>(tag, out var mode) && mode != AppTheme.Mode)
            {
                AppTheme.Set(mode);
            }
        }

        private async void OpenColorSettingsClick(object sender, RoutedEventArgs e) =>
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:colors"));

        private void BackdropChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !(BackdropBox.SelectedItem is ComboBoxItem item) || !(item.Tag is string tag))
            {
                return;
            }

            AppServices.Settings.SetString(AppearanceService.BackdropKey, tag);
            ApplyAppearance();
        }

        private void DarkStyleChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !(DarkStyleBox.SelectedItem is ComboBoxItem item) || !(item.Tag is string tag))
            {
                return;
            }

            AppServices.Settings.SetString(AppearanceService.DarkStyleKey, tag);
            ApplyAppearance();
        }

        private void CornerChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !(CornerBox.SelectedItem is ComboBoxItem item) || !(item.Tag is string tag))
            {
                return;
            }

            AppServices.Settings.SetString(AppearanceService.CornerKey, tag);
            ApplyAppearance();
        }

        private static void ApplyAppearance()
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var backdrop = AppServices.Settings.GetString(AppearanceService.BackdropKey) ?? "none";
            var darkStyle = AppServices.Settings.GetString(AppearanceService.DarkStyleKey) ?? "black";
            var corner = AppServices.Settings.GetString(AppearanceService.CornerKey) ?? "round";
            AppearanceService.Apply(window, backdrop, darkStyle, squareCorners: corner == "square");
        }

        private async void PickDownloadPathClick(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary,
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                return;
            }

            AppServices.Settings.SetString(Ncrust.Platform.DownloadService.FolderKey, folder.Path);
            DownloadPathText.Text = folder.Path;
        }

        // ── 存储与缓存 ────────────────────────────────────────────────────────

        private async Task UpdateCacheSizeAsync()
        {
            var bytes = await Task.Run(() => CacheFolders().Sum(FolderSize) + FileSize(LyricsCacheFile()));
            CacheSizeText.Text = "缓存占用 " + FormatBytes(bytes);
        }

        private async void ClearCacheClick(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "清除缓存",
                Content = "确定清除全部缓存？",
                PrimaryButtonText = "清除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            ClearCacheButton.IsEnabled = false;
            AppServices.Cache.ClearAll();
            await AppServices.Lyrics.ClearAsync();
            await Task.Run(() =>
            {
                foreach (var folder in CacheFolders())
                {
                    ClearFolder(folder);
                }
            });

            await UpdateCacheSizeAsync();
            CacheSizeText.Text += " · 缓存已清除";
            ClearCacheButton.IsEnabled = true;
        }

        /// <summary>
        /// 可清理的缓存目录：WebView2 登录页的用户数据（默认位于可执行目录旁的 Ncrust.exe.WebView2）
        /// 与临时下载目录。不含 %LOCALAPPDATA%\Ncrust 的数据目录（登录凭据、音乐库、播放队列都在那里）。
        /// </summary>
        private static string[] CacheFolders()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Ncrust.exe.WebView2"),
                Path.Combine(local, "Ncrust", "WebView2"),
                Path.Combine(Path.GetTempPath(), "Ncrust"),
            };
        }

        private static string LyricsCacheFile() => Path.Combine(Ncrust.Platform.NcrustPaths.DataDirectory, "lyrics_cache.json");

        private static long FolderSize(string path)
        {
            try
            {
                return Directory.Exists(path)
                    ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => SafeLength(f))
                    : 0;
            }
            catch
            {
                return 0; // 没有权限枚举的目录按 0 计。
            }
        }

        private static long SafeLength(FileInfo file)
        {
            try
            {
                return file.Length;
            }
            catch
            {
                return 0;
            }
        }

        private static long FileSize(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static void ClearFolder(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try
                    {
                        file.Delete();
                    }
                    catch
                    {
                        // 正被占用的文件跳过（例如正在显示的封面）。
                    }
                }
            }
            catch
            {
            }
        }

        // ── 格式化 ────────────────────────────────────────────────────────────

        private static int Clamp(int index) => Math.Max(0, Math.Min(QualityOptions.Length - 1, index));

        /// <summary>与 Android formatCacheBytes 同样的分档：B / KB / MB / GB。</summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            }

            double value = bytes;
            string[] units = { "KB", "MB", "GB" };
            var unit = -1;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[unit];
        }
    }
}
