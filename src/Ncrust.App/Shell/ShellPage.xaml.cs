using System;
using System.Collections.Generic;
using System.Linq;
using Ncrust.Login;
using Ncrust.Pages;
using Ncrust.Playback;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using muxc = Microsoft.UI.Xaml.Controls;

namespace Ncrust.Shell
{
    /// <summary>
    /// 外壳：NavigationView 汉堡菜单 + 自定义标题栏 + 播放器层 + 登录层 + 全局快捷键。
    /// </summary>
    public sealed partial class ShellPage : Page
    {
        private readonly DispatcherTimer _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        private LoginPage _login;
        private int _suggestVersion;
        private bool _engineHooked;

        public ShellPage()
        {
            InitializeComponent();
            SetUpTitleBar();
            RegisterAccelerators();

            // 快捷键不在页面上弹出按键提示。
            KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

            AppShell.NavigateHandler = (page, parameter) => NavigateTo(page, parameter);
            AppShell.NoticeHandler = ShowNotice;
            _noticeTimer.Tick += (_, __) => HideNotice();

            SearchSuggestion.RemoveRequested += RemoveHistoryAsync;
            LoginLauncher.LaunchRequested += ShowLogin;
            AppServices.SessionChanged += OnSessionChanged;

            // 鼠标侧键「后退」：XButton1 冒泡到根内容，统一处理（WinUI 3 没有 CoreWindow）。
            var root = App.MainWindow?.Content;
            root?.AddHandler(PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), true);
            Loaded += OnLoaded;

            Nav.SelectedItem = HomeItem;
            ContentFrame.Navigate(typeof(HomePage));
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateAccountItem();
            UpdateSessionBar();
            await PlaybackHost.RestoreAsync();

            // 播放权益提示（试听 / 音质受限 / 跳过）走这里，界面文案在 Resources.DisplayFormat。
            if (!_engineHooked)
            {
                _engineHooked = true;
                PlaybackHost.Engine.NoticeRaised += OnPlaybackNotice;

                // 托盘菜单的「播放 / 暂停」文案随播放状态切换。
                PlaybackHost.Engine.IsPlayingChanged += TrayIcon.UpdatePlayPauseLabel;
                TrayIcon.UpdatePlayPauseLabel(PlaybackHost.Engine.IsPlaying);
            }

            // 收藏库先读本地（菜单里的「加入库 / 移除收藏」要靠它判断），云端刷新放到后台。
            try
            {
                await AppServices.Library.PreloadAsync();
                SongActions.NotifyLibraryChanged();
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }

            await RefreshSessionAsync(force: true);
            _ = RefreshLibraryAsync();

            // 自动签到（设置 → 常规 可关）：登录态有效时签到，成功 / 已签以轻提示告知，失败静默。
            _ = TryAutoSignInAsync();
        }

        private static async System.Threading.Tasks.Task TryAutoSignInAsync()
        {
            try
            {
                if (!AppServices.Settings.GetBool("auto_signin", true) || !AppServices.IsLoggedIn())
                {
                    return;
                }

                var result = await new Core.Api.SignInApi(AppServices.Http).DailyAsync();
                if (result.Ok)
                {
                    App.Trace("signin: ok " + result.Message);
                    AppShell.Notice("签到成功：" + result.Message);
                }
                else if (result.AlreadyDone)
                {
                    App.Trace("signin: done " + result.Message);
                    AppShell.Notice(result.Message);
                }
                else
                {
                    App.Trace("signin: failed " + result.Message);
                }
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
        }

        private static async System.Threading.Tasks.Task RefreshLibraryAsync()
        {
            if (!AppServices.IsLoggedIn())
            {
                return;
            }

            try
            {
                await AppServices.Library.RefreshFromCloudAsync();
                SongActions.NotifyLibraryChanged();
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
        }

        // ── 轻提示 ────────────────────────────────────────────────────────────

        private void ShowNotice(string text)
        {
            NoticeText.Text = text;
            NoticeHost.Visibility = Visibility.Visible;
            FadeNotice(1);
            _noticeTimer.Stop();
            _noticeTimer.Start();
        }

        private void HideNotice()
        {
            _noticeTimer.Stop();
            FadeNotice(0);
        }

        /// <summary>Opacity 是独立动画（合成线程执行），不违反「禁止依赖动画」。</summary>
        private void FadeNotice(double to)
        {
            var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(to > 0 ? 150 : 250),
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, NoticeHost);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(animation);
            if (to <= 0)
            {
                storyboard.Completed += (_, __) =>
                {
                    if (!_noticeTimer.IsEnabled)
                    {
                        NoticeHost.Visibility = Visibility.Collapsed;
                    }
                };
            }

            storyboard.Begin();
        }

        // ── 标题栏 ────────────────────────────────────────────────────────────

        /// <summary>系统标题栏高度（WinUI 3 的 AppWindowTitleBar 不暴露高度，取 Windows 默认值）。</summary>
        private const double TitleBarHeight = 32;

        private void SetUpTitleBar()
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var appWindow = window.AppWindow;
            appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            window.ExtendsContentIntoTitleBar = true;
            window.SetTitleBar(AppTitleBar);
            ApplyTitleBarHeight(TitleBarHeight);

            // 播放器的真全屏走系统全屏（presenter 切到 FullScreen）：标题栏隐藏，覆盖层不用再让出标题栏高度。
            appWindow.Changed += (_, args) =>
            {
                if (args.DidPresenterChange)
                {
                    ApplyTitleBarHeight(IsFullScreen() ? 0 : TitleBarHeight);
                }
            };

            // 系统标题栏按钮（最小化 / 最大化 / 关闭）：透明底，前景随明暗主题（切换主题时 AppTheme 会再调一次）。
            AppTheme.ApplyTitleBarColors();

            // WinUI 3 的 AppWindow 没有 SetPreferredMinSize；最小尺寸由 App 的 WndProc 钩子兜底（见 App 初始化）。
            window.Activated += (_, args) =>
            {
                AppTitle.Opacity = args.WindowActivationState == WindowActivationState.Deactivated ? 0.5 : 1.0;

                // 回到前台时复核一次会话（距上次判定 ≥ 30 分钟才真打网络，间隔由 SessionMonitor 判断）。
                if (args.WindowActivationState != WindowActivationState.Deactivated)
                {
                    _ = RefreshSessionAsync(force: false);
                }
            };
        }

        private static bool IsFullScreen()
        {
            var presenter = App.MainWindow?.AppWindow.Presenter;
            return presenter != null &&
                presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
        }

        private void ApplyTitleBarHeight(double height)
        {
            AppTitleBar.Height = height;

            // 系统全屏（播放器真全屏）时标题栏隐藏：自绘的图标与标题一并隐藏，覆盖层不再让出高度。
            var fullScreen = IsFullScreen();
            AppTitleBar.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
            if (fullScreen)
            {
                height = 0;
            }

            // 导航视图整体下移标题栏高度：标题栏条（含左上角图标 / 软件名）不遮住导航区
            // 顶部的返回键与右侧内容（IsTitleBarAutoPaddingEnabled 在 WinUI 3 下不生效，手动让位）。
            Nav.Margin = new Thickness(0, height, 0, 0);

            // 标题栏区域的输入被系统拿去拖动窗口：登录层让出这一条，否则里面的按钮点不到。
            // 播放器层不再让出：展开卡片要全覆盖整窗（含标题栏文字区），内容通过 TitleBarInset 下移。
            Player.TitleBarInset = height;
            OverlayHost.Margin = new Thickness(0, height, 0, 0);
        }

        /// <summary>标题文字给左上角的返回 / 汉堡按钮让位（最小模式两个按钮都在标题栏这一行）。</summary>
        private void NavDisplayModeChanged(muxc.NavigationView sender, muxc.NavigationViewDisplayModeChangedEventArgs args)
        {
            var left = sender.DisplayMode == muxc.NavigationViewDisplayMode.Minimal
                ? sender.CompactPaneLength * 2
                : sender.CompactPaneLength;
            AppTitleBar.Margin = new Thickness(left, 0, 0, 0);
        }

        // ── 导航 ──────────────────────────────────────────────────────────────

        private void NavItemInvoked(muxc.NavigationView sender, muxc.NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                NavigateTo(typeof(SettingsPage));
                return;
            }

            var tag = (args.InvokedItemContainer as muxc.NavigationViewItem)?.Tag as string;
            switch (tag)
            {
                case "home":
                    NavigateTo(typeof(HomePage));
                    break;
                case "liked":
                    NavigateTo(typeof(LibraryPage), "liked");
                    break;
                case "albums":
                    NavigateTo(typeof(LibraryPage), "albums");
                    break;
                case "playlists":
                    NavigateTo(typeof(LibraryPage), "playlists");
                    break;
                case "local":
                    NavigateTo(typeof(LocalMusicPage));
                    break;
                case "account":
                    // 独立账户页：资料 / 登录 / 退出登录（不再进设置页）。
                    NavigateTo(typeof(AccountPage));
                    break;
            }
        }

        private void NavigateTo(Type page, object parameter = null)
        {
            if (ContentFrame.CurrentSourcePageType != page || parameter != null)
            {
                ContentFrame.Navigate(page, parameter);
            }
        }

        private void NavBackRequested(muxc.NavigationView sender, muxc.NavigationViewBackRequestedEventArgs args) => TryGoBack();

        private bool TryGoBack()
        {
            if (!ContentFrame.CanGoBack)
            {
                return false;
            }

            ContentFrame.GoBack();
            return true;
        }

        /// <summary>返回 / 前进后让选中项与当前页一致（Groove 的行为：返回到首页时「首页」重新高亮）。</summary>
        private void ContentFrameNavigated(object sender, NavigationEventArgs e)
        {
            Nav.IsBackEnabled = ContentFrame.CanGoBack;

            if (e.SourcePageType == typeof(HomePage))
            {
                Nav.SelectedItem = HomeItem;
            }
            else if (e.SourcePageType == typeof(LibraryPage))
            {
                // 侧栏「收藏的音乐」分区直达对应页签时，高亮跟到对应菜单项。
                Nav.SelectedItem = (e.Parameter as string) switch
                {
                    "liked" => LikedSongsItem,
                    "albums" => AlbumsItem,
                    "playlists" => PlaylistsItem,
                    _ => LikedSongsItem,
                };
            }
            else if (e.SourcePageType == typeof(LocalMusicPage))
            {
                Nav.SelectedItem = LocalMusicItem;
            }
            else if (e.SourcePageType == typeof(SettingsPage) || e.SourcePageType == typeof(EqualizerPage))
            {
                Nav.SelectedItem = Nav.SettingsItem;
            }
            else
            {
                // 搜索结果、详情页等不对应菜单项：清掉高亮，而不是停在上一个菜单项上。
                Nav.SelectedItem = null;
            }
        }

        // ── 搜索 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 即时建议（Groove）：用户输入停顿 500ms 后取前几首歌作为建议。防抖间隔与 Android
        /// SearchViewModel 相同，不能去掉 —— 否则每敲一个字都打一次接口。
        /// </summary>
        private async void SearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }

            var version = ++_suggestVersion;
            var text = sender.Text.Trim();
            if (text.Length == 0)
            {
                // 清空了输入：回到搜索记录。
                await ShowHistoryAsync(version);
                return;
            }

            await System.Threading.Tasks.Task.Delay(500);
            if (version != _suggestVersion)
            {
                return; // 500ms 内又输入了：这次作废。
            }

            try
            {
                var songs = await AppServices.Search.SearchSongsAsync(text, 8);
                if (version == _suggestVersion)
                {
                    sender.ItemsSource = songs.Select(SearchSuggestion.FromSong).ToList();
                }
            }
            catch
            {
                // 建议失败不打扰用户，回车仍可进入完整搜索。
            }
        }

        private void SearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            _suggestVersion++;
            sender.ItemsSource = null;

            string query;
            if (args.ChosenSuggestion is SearchSuggestion suggestion)
            {
                switch (suggestion.Kind)
                {
                    case SearchSuggestionKind.Song:
                    case SearchSuggestionKind.HistorySong:
                        // 选了歌：直接播放（与搜索结果点歌相同：插到当前曲之后），并记入搜索记录。
                        PlaybackHost.PlaySong(suggestion.Song);
                        _ = AppServices.SearchHistory.AddSongAsync(suggestion.Song);
                        return;
                    case SearchSuggestionKind.HistoryAlbum:
                        AppShell.ToAlbum(suggestion.Id);
                        return;
                    case SearchSuggestionKind.HistoryArtist:
                        AppShell.ToArtist(suggestion.Id);
                        return;
                    case SearchSuggestionKind.ClearHistory:
                        sender.Text = string.Empty;
                        _ = AppServices.SearchHistory.ClearAllAsync();
                        AppShell.Notice("搜索记录已清除");
                        return;
                    default:
                        query = suggestion.Title;
                        sender.Text = query;
                        break;
                }
            }
            else
            {
                query = (args.QueryText ?? string.Empty).Trim();
            }

            if (query.Length == 0)
            {
                return;
            }

            _ = AppServices.SearchHistory.AddQueryAsync(query);
            NavigateTo(typeof(SearchPage), query);

            // 最小 / 紧凑模式下搜索框在浮出的面板里：提交后收起面板，让结果露出来。
            if (Nav.DisplayMode != muxc.NavigationViewDisplayMode.Expanded)
            {
                Nav.IsPaneOpen = false;
            }
        }

        /// <summary>搜索框获得焦点且为空时，下拉显示搜索记录（对应 Android 搜索页空查询时的历史区）。</summary>
        private async void SearchGotFocus(object sender, RoutedEventArgs e)
        {
            if (SearchBox.Text.Trim().Length == 0)
            {
                await ShowHistoryAsync(++_suggestVersion);
            }
        }

        private async void RemoveHistoryAsync(SearchSuggestion suggestion)
        {
            var history = AppServices.SearchHistory;
            try
            {
                switch (suggestion.Kind)
                {
                    case SearchSuggestionKind.Query:
                        await history.RemoveQueryAsync(suggestion.Title);
                        break;
                    case SearchSuggestionKind.HistorySong:
                        await history.RemoveAsync(Core.Search.SearchHistoryType.Song, suggestion.Id);
                        break;
                    case SearchSuggestionKind.HistoryAlbum:
                        await history.RemoveAsync(Core.Search.SearchHistoryType.Album, suggestion.Id);
                        break;
                    case SearchSuggestionKind.HistoryArtist:
                        await history.RemoveAsync(Core.Search.SearchHistoryType.Artist, suggestion.Id);
                        break;
                    default:
                        return;
                }
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }

            await ShowHistoryAsync(++_suggestVersion);
        }

        private async System.Threading.Tasks.Task ShowHistoryAsync(int version)
        {
            IReadOnlyList<SearchSuggestion> items;
            try
            {
                var history = AppServices.SearchHistory;
                items = SearchSuggestion.FromHistory(
                    await history.GetQueriesAsync(),
                    await history.GetSongsAsync(),
                    await history.GetAlbumsAsync(),
                    await history.GetArtistsAsync());
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
                return;
            }

            if (version != _suggestVersion || SearchBox.Text.Trim().Length > 0)
            {
                return; // 期间开始输入了：以即时建议为准。
            }

            SearchBox.ItemsSource = items;
            SearchBox.IsSuggestionListOpen = items.Count > 0;
        }

        private void FocusSearch()
        {
            if (Nav.DisplayMode != muxc.NavigationViewDisplayMode.Expanded)
            {
                Nav.IsPaneOpen = true;
            }

            SearchBox.Focus(FocusState.Keyboard);
        }

        // ── 账户 ──────────────────────────────────────────────────────────────

        private void UpdateAccountItem()
        {
            // 会话失效（cookie 已被清）或未登录时显示「登录」入口；只有会话有效才显示昵称。
            // 还没判定过（Unknown，启动时判定在途）时按「有凭证」处理，免得每次启动都先闪一下「登录」。
            var state = AppServices.AccountSession.State;
            var known = state is Core.Account.SessionState.Valid
                || (state is Core.Account.SessionState.Unknown && AppServices.IsLoggedIn());
            var profile = AppServices.Cache.UserProfile;

            // 账户图标固定用 XAML 里声明的系统联系人剪影（Segoe MDL2 E77B），真实头像只在设置页显示。
            AccountItem.Content = known ? (profile?.Nickname ?? "我的") : "登录";
        }

        /// <summary>会话状态变化：侧栏账户项与内容区顶部的失效横幅都要跟着变。</summary>
        private void OnSessionChanged()
        {
            UpdateAccountItem();
            UpdateSessionBar();

            // 只记状态类型与「请求层还有没有 cookie」：昵称、UID 这些用户资料不进日志。
            // 排查「收藏刷不出来 / 推荐变了」时，这行能直接区分「登录失效」与「网络问题」。
            App.Trace("session: " + Core.Account.SessionState.TypeName(AppServices.AccountSession.State)
                + " cookie=" + (AppServices.IsLoggedIn() ? "yes" : "no"));
        }

        /// <summary>播放权益提示（试听 / 音质封顶 / 跳过）：轻提示，不弹模态框、不打断播放。</summary>
        private void OnPlaybackNotice(Core.Account.PlaybackNotice notice)
        {
            string text;
            switch (notice.Kind)
            {
                case Core.Account.PlaybackNoticeKind.Trial:
                    text = Ncrust.Resources.DisplayFormat.TrialNotice(notice.TrialSeconds, notice.Reason);
                    break;
                case Core.Account.PlaybackNoticeKind.Capped:
                    text = Ncrust.Resources.DisplayFormat.QualityCapped(notice.Level, notice.Reason);
                    break;
                case Core.Account.PlaybackNoticeKind.Skipped:
                    text = Ncrust.Resources.DisplayFormat.SkippedSong(notice.SongName, notice.Reason);
                    break;
                default:
                    text = Ncrust.Resources.DisplayFormat.SkippedSongs(notice.Count);
                    break;
            }

            ShowNotice(text);
        }

        /// <summary>
        /// 会话判定：启动时一次、窗口重新激活且距上次判定 ≥ 30 分钟时一次（间隔检查在
        /// <see cref="Core.Account.SessionMonitor.RefreshAsync"/> 里）。判定结果通过
        /// <c>AppServices.SessionChanged</c> 回到这里刷新横幅与账户项。
        /// </summary>
        private static async System.Threading.Tasks.Task RefreshSessionAsync(bool force)
        {
            try
            {
                await AppServices.AccountSession.RefreshAsync(force);
            }
            catch (Exception ex)
            {
                // 判定本身出错绝不改状态：维持上一个已知状态。
                App.WriteCrashLog(ex);
            }
        }

        /// <summary>会话失效时在内容区顶部挂横幅；重新登录成功后自动消失。</summary>
        private void UpdateSessionBar() =>
            SessionBar.IsOpen = AppServices.AccountSession.State is Core.Account.SessionState.Expired;

        private void SessionBarLoginClick(object sender, RoutedEventArgs e) => LoginLauncher.Request();

        // ── 登录层 ────────────────────────────────────────────────────────────

        private void ShowLogin()
        {
            if (_login != null)
            {
                return;
            }

            _login = new LoginPage();
            _login.LoggedIn += OnLoggedIn;
            _login.CloseRequested += OnLoginClose;
            OverlayHost.Children.Add(_login);
        }

        private async void OnLoggedIn(string cookie)
        {
            CloseLogin();

            // 登录成功后立刻复核（拿到资料与 VIP），不走 30 分钟窗口。
            await AppServices.SetCookieAsync(cookie);
            await RefreshLibraryAsync();
        }

        private void OnLoginClose() => CloseLogin();

        private void CloseLogin()
        {
            if (_login == null)
            {
                return;
            }

            _login.LoggedIn -= OnLoggedIn;
            _login.CloseRequested -= OnLoginClose;
            OverlayHost.Children.Remove(_login);
            _login = null;
        }

        // ── 快捷键 ────────────────────────────────────────────────────────────

        private void RegisterAccelerators()
        {
            AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, (_, args) =>
            {
                if (_login != null)
                {
                    CloseLogin();
                    args.Handled = true;
                    return;
                }

                args.Handled = Player.HandleEscape();
            });
            AddAccelerator(VirtualKey.F11, VirtualKeyModifiers.None, (_, args) =>
            {
                Player.ToggleFullscreen();
                args.Handled = true;
            });
            AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, (_, args) =>
            {
                FocusSearch();
                args.Handled = true;
            });
            AddAccelerator(VirtualKey.L, VirtualKeyModifiers.Control, (_, args) =>
            {
                Player.ToggleExpanded();
                args.Handled = true;
            });
            AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Control, (_, args) =>
            {
                PlaybackHost.Engine.Previous();
                args.Handled = true;
            });
            AddAccelerator(VirtualKey.Right, VirtualKeyModifiers.Control, (_, args) =>
            {
                PlaybackHost.Engine.Next();
                args.Handled = true;
            });
            AddAccelerator(VirtualKey.P, VirtualKeyModifiers.Control, (_, args) =>
            {
                // Groove Music 的播放 / 暂停快捷键。
                PlaybackHost.Engine.PlayPause();
                args.Handled = true;
            });
            AddAccelerator(VirtualKey.Space, VirtualKeyModifiers.None, (_, args) =>
            {
                // 焦点在文本输入或按钮上时，空格留给它们（打字 / 按下按钮）。
                var focused = FocusManager.GetFocusedElement(Content.XamlRoot);
                if (focused is TextBox || focused is AutoSuggestBox || focused is ButtonBase)
                {
                    return;
                }

                PlaybackHost.Engine.PlayPause();
                args.Handled = true;
            });
            AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, (_, args) => args.Handled = TryGoBack());
        }

        private void AddAccelerator(
            VirtualKey key,
            VirtualKeyModifiers modifiers,
            TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += handler;
            KeyboardAccelerators.Add(accelerator);
        }

        /// <summary>鼠标侧键「后退」。</summary>
        private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(null).Properties.IsXButton1Pressed)
            {
                e.Handled = TryGoBack();
            }
        }
    }
}
