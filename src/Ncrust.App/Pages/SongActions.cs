using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ncrust.Core.Api;
using Ncrust.Playback;
using Ncrust.Resources;
using Ncrust.Shell;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Ncrust.Pages
{
    /// <summary>
    /// 歌曲菜单（对应 Android SongMenuSheet + 各页面传入的 SongMenuAction）。桌面上是右键 / 触屏长按 /
    /// Shift+F10 / 菜单键弹出的 <see cref="MenuFlyout"/>。
    ///
    /// 项目与 Android 一致：播放、插播、最后播放、加入库 / 移除收藏，统一追加转到歌手、转到专辑、
    /// 分享（桌面上是复制链接，文本格式同 Android shareSong）。页面可再追加自己的项（音乐库的「重试同步」等）。
    /// </summary>
    internal static class SongActions
    {
        /// <summary>收藏（红心）变化后触发：音乐库页与播放器的收藏按钮据此刷新。</summary>
        public static event Action LibraryChanged;

        /// <summary>
        /// 给歌曲列表挂上下文菜单。列表项的 DataContext 必须是 <see cref="SongItem"/>。
        /// <paramref name="extra"/> 返回页面自己的附加项（插在「分享」一组之前），可为 null。
        /// </summary>
        public static void AttachContextMenu(ListViewBase list, Func<SongItem, IEnumerable<MenuFlyoutItemBase>> extra = null) =>
            AttachMenu<SongItem>(list, song => BuildMenu(song, extra?.Invoke(song)));

        /// <summary>
        /// 给专辑 / 歌单磁贴挂上下文菜单：播放（替换队列）/ 插播 / 最后播放（对应 Android 磁贴上的
        /// 全部播放按钮与搜索页的 onAlbumBatch）。<paramref name="loadSongs"/> 取这张专辑 / 歌单的曲目。
        /// </summary>
        public static void AttachCollectionMenu<T>(
            ListViewBase grid,
            Func<T, Task<IReadOnlyList<SongItem>>> loadSongs,
            Func<T, IEnumerable<MenuFlyoutItemBase>> extra = null)
            where T : class
        {
            AttachMenu<T>(grid, item =>
            {
                var menu = new MenuFlyout();
                menu.Items.Add(Item("播放", Glyphs.Play, () => _ = WithSongsAsync(loadSongs(item), songs => PlaybackHost.PlayAll(songs))));
                menu.Items.Add(Item("插播", Glyphs.PlayNext, () => _ = WithSongsAsync(loadSongs(item), songs =>
                {
                    PlaybackHost.InsertAllNext(songs);
                    AppShell.Notice(DisplayFormat.SongCount(songs.Count) + "将在当前曲目之后播放");
                })));
                menu.Items.Add(Item("最后播放", Glyphs.Add, () => _ = WithSongsAsync(loadSongs(item), songs =>
                {
                    PlaybackHost.AppendAll(songs);
                    AppShell.Notice(DisplayFormat.SongCount(songs.Count) + "已加入播放队列末尾");
                })));

                var more = extra?.Invoke(item);
                if (more != null)
                {
                    foreach (var entry in more)
                    {
                        menu.Items.Add(entry);
                    }
                }

                return menu;
            });
        }

        /// <summary>专辑曲目：先读 ContentCache，没有再请求并写回。</summary>
        public static async Task<IReadOnlyList<SongItem>> AlbumSongsAsync(long albumId)
        {
            var cached = AppServices.Cache.GetAlbum(albumId);
            if (cached != null)
            {
                return cached.Songs;
            }

            var fresh = await AppServices.Albums.GetAlbumDetailAsync(albumId);
            AppServices.Cache.PutAlbum(albumId, fresh);
            return fresh.Songs;
        }

        /// <summary>歌单曲目：先读 ContentCache，没有再请求并写回。</summary>
        public static async Task<IReadOnlyList<SongItem>> PlaylistSongsAsync(long playlistId)
        {
            var cached = AppServices.Cache.GetPlaylistSongs(playlistId);
            if (cached != null)
            {
                return cached;
            }

            var fresh = await AppServices.Playlists.GetPlaylistDetailAsync(playlistId);
            AppServices.Cache.PutPlaylistSongs(playlistId, fresh);
            return fresh;
        }

        private static async Task WithSongsAsync(Task<IReadOnlyList<SongItem>> load, Action<IReadOnlyList<SongItem>> use)
        {
            try
            {
                var songs = await load;
                if (songs.Count > 0)
                {
                    use(songs);
                }
                else
                {
                    AppShell.Notice("没有可播放的歌曲");
                }
            }
            catch (Exception ex)
            {
                AppShell.Notice("加载失败：" + ex.Message);
            }
        }

        private static void AttachMenu<T>(ListViewBase list, Func<T, MenuFlyout> build)
            where T : class
        {
            list.ContextRequested += (sender, args) =>
            {
                var item = FindItem<T>(args.OriginalSource as DependencyObject);
                if (item == null)
                {
                    return;
                }

                args.Handled = true;
                var menu = build(item);
                var target = (FrameworkElement)sender;
                if (args.TryGetPosition(target, out var point))
                {
                    menu.ShowAt(target, point);
                }
                else
                {
                    // 键盘（Shift+F10 / 菜单键）没有指针位置：贴着获得焦点的那一行弹出。
                    menu.ShowAt(args.OriginalSource as FrameworkElement ?? target);
                }
            };
        }

        public static MenuFlyout BuildMenu(SongItem song, IEnumerable<MenuFlyoutItemBase> extra = null)
        {
            var menu = new MenuFlyout();
            menu.Items.Add(Item("播放", Glyphs.Play, () => PlaybackHost.PlaySong(song)));
            menu.Items.Add(Item("插播", Glyphs.PlayNext, () =>
            {
                PlaybackHost.InsertNext(song);
                AppShell.Notice("将在当前曲目之后播放");
            }));
            menu.Items.Add(Item("最后播放", Glyphs.Add, () =>
            {
                PlaybackHost.Append(song);
                AppShell.Notice("已加入播放队列末尾");
            }));

            var saved = AppServices.Library.IsSongSaved(song.Id);
            menu.Items.Add(saved
                ? Item("移除收藏", Glyphs.HeartFill, () => _ = SetSavedAsync(song, false))
                : Item("加入库", Glyphs.Heart, () => _ = SetSavedAsync(song, true)));

            // 本地歌曲没有取链地址，下载只对网易曲库的歌有效。
            if (song.Id > 0)
            {
                menu.Items.Add(Item("下载", Glyphs.Download, () => _ = DownloadAsync(song)));
            }

            if (extra != null)
            {
                foreach (var item in extra)
                {
                    menu.Items.Add(item);
                }
            }

            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("转到歌手", Glyphs.Artist, () => _ = GoToArtistAsync(song)));
            menu.Items.Add(Item("转到专辑", Glyphs.Album, () => _ = GoToAlbumAsync(song)));
            menu.Items.Add(Item("复制链接", Glyphs.Link, () => CopyLink(song)));
            return menu;
        }

        public static MenuFlyoutItem Item(string text, string glyph, Action onClick)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon { FontFamily = new FontFamily("Segoe MDL2 Assets"), Glyph = glyph },
            };
            item.Click += (_, __) => onClick();
            return item;
        }

        /// <summary>加入库（红心）/ 移除收藏。本地立即生效，云端异步同步；失败留在待同步表（音乐库页可重试）。</summary>
        public static async Task SetSavedAsync(SongItem song, bool saved)
        {
            try
            {
                var task = saved
                    ? AppServices.Library.SaveSongAsync(song)
                    : AppServices.Library.RemoveSongAsync(song.Id);

                // 本地列表在第一个 await 之前就改好了：先刷新界面，不等云端往返。
                LibraryChanged?.Invoke();
                AppShell.Notice(saved ? "已加入库" : "已从库中移除");
                await task;
            }
            catch (Exception ex)
            {
                // 云端同步失败不影响本地收藏，待同步表里会记着。
                App.WriteCrashLog(ex);
            }

            LibraryChanged?.Invoke();
        }

        public static void NotifyLibraryChanged() => LibraryChanged?.Invoke();

        /// <summary>
        /// 下载歌曲（对应网易客户端的下载规则）：
        /// 1) 需要网易云会员（SVIP / 黑胶 VIP / 音乐包任一有效），否则提示不弹窗；
        /// 2) 弹窗选择音质 —— 只提供 mp3 三档（压缩 / 较好 / 更好，对应 standard / higher / exhigh），
        ///    无损及以上是 flac / 环绕声格式，不提供下载；
        /// 3) 走 DownloadService（https 直连），保存为 .mp3。
        /// </summary>
        private static async Task DownloadAsync(SongItem song)
        {
            // 会员门槛：会话有效且 VIP 在有效期内。
            var vip = AppServices.AccountSession.State is Core.Account.SessionState.Valid valid ? valid.Vip : null;
            if (!(vip is Core.Account.VipStatus.Active))
            {
                AppShell.Notice("下载需要网易云会员（SVIP / 黑胶 VIP / 音乐包）");
                return;
            }

            try
            {
            // 只提供 mp3 三档：标准 → 压缩、higher → 较好、exhigh → 更好。
            var labels = Ncrust.Resources.DisplayFormat.QualityLabels;
            var levels = new[] { "standard", "higher", "exhigh" };
            var box = new ComboBox
            {
                MinWidth = 240,
                SelectedIndex = 1, // 默认「较好」（320k mp3）
            };
            box.Items.Add(labels[0] + "（mp3）");
            box.Items.Add(labels[1] + "（mp3）");
            box.Items.Add(labels[2] + "（mp3）");

            var dialog = new ContentDialog
            {
                Title = "下载歌曲",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = song.Name + " · " + song.ArtistText,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock { Text = "选择音质（下载格式为 mp3）" },
                        box,
                    },
                },
                PrimaryButtonText = "下载",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                // 右键菜单里弹出的对话框不在可视树中，必须显式挂 XamlRoot，否则抛
                // 「This element does not have a XamlRoot」、窗口永远不出现。
                XamlRoot = App.MainWindow.Content.XamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            AppShell.Notice("开始下载：" + song.Name);
            string path;
            try
            {
                path = await Ncrust.Platform.DownloadService.DownloadAsync(song, levels[Math.Max(0, Math.Min(levels.Length - 1, box.SelectedIndex))]);
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
                path = null;
            }

            AppShell.Notice(path != null
                ? "已下载到 " + path
                : "下载失败：" + song.Name + "（该音质可能无资源）");
            }
            catch (Exception ex)
            {
                // 弹窗 / 取链 / 写盘任一环节异常都给用户一个交代，不再无声失败。
                App.WriteCrashLog(ex);
                AppShell.Notice("下载失败：" + song.Name);
            }
        }

        /// <summary>
        /// 转到歌手 / 专辑（对应 Android resolveAndNavigate）：列表接口大多不带歌手 / 专辑 id，
        /// 缺失时先用 song/detail 现拉；拉不到就提示，不给死链接。
        /// </summary>
        public static async Task GoToArtistAsync(SongItem song)
        {
            var id = song.Artists.FirstOrDefault()?.Id;
            if (id == null)
            {
                id = (await ResolveAsync(song))?.Artists.FirstOrDefault()?.Id;
            }

            if (id.HasValue)
            {
                AppShell.ToArtist(id.Value);
            }
            else
            {
                AppShell.Notice("没有找到这首歌的歌手信息");
            }
        }

        public static async Task GoToAlbumAsync(SongItem song)
        {
            var id = song.Album?.Id;
            if (id == null)
            {
                id = (await ResolveAsync(song))?.Album?.Id;
            }

            if (id.HasValue)
            {
                AppShell.ToAlbum(id.Value);
            }
            else
            {
                AppShell.Notice("没有找到这首歌的专辑信息");
            }
        }

        public static void CopyLink(SongItem song)
        {
            var text = song.Name;
            if (!string.IsNullOrEmpty(song.ArtistText))
            {
                text += " - " + song.ArtistText;
            }

            text += "\n" + SongUrl(song.Id);

            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            AppShell.Notice("链接已复制");
        }

        public static string SongUrl(long id) => "https://music.163.com/song?id=" + id;

        private static async Task<SongItem> ResolveAsync(SongItem song)
        {
            try
            {
                var detail = await AppServices.Songs.GetSongDetailAsync(new[] { song.Id });
                return detail.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static T FindItem<T>(DependencyObject source)
            where T : class
        {
            // 从命中的元素往上找，直到遇到数据上下文是该类型的那一层（项模板根或 ListViewItem）。
            for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is FrameworkElement element && element.DataContext is T item)
                {
                    return item;
                }

                if (node is ListViewBase)
                {
                    break;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Segoe MDL2 Assets 码点。用 (char) 构造而不写 \u 转义：编辑工具会把 \u 转义直接写成
    /// 看不见的私用区字符（见 windows/AGENTS.md「已知的坑」）。
    /// </summary>
    internal static class Glyphs
    {
        public static readonly string Play = G(0xE768);
        public static readonly string Pause = G(0xE769);
        public static readonly string PlayNext = G(0xE893);
        public static readonly string Add = G(0xE710);
        public static readonly string Heart = G(0xEB51);
        public static readonly string HeartFill = G(0xEB52);
        public static readonly string Artist = G(0xE77B);
        public static readonly string Album = G(0xE93C);
        public static readonly string Link = G(0xE71B);
        public static readonly string Delete = G(0xE74D);
        public static readonly string Sync = G(0xE895);
        public static readonly string Radio = G(0xE8D6);
        public static readonly string List = G(0xE8FD);
        public static readonly string Lyrics = G(0xE8D2);
        public static readonly string Clear = G(0xE894);
        public static readonly string History = G(0xE81C);
        public static readonly string Download = G(0xE896);

        private static string G(int codePoint) => ((char)codePoint).ToString();
    }
}
