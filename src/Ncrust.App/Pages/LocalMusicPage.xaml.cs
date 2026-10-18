using System;
using System.Threading.Tasks;
using Ncrust.Core.Api;
using Ncrust.Playback;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Ncrust.Pages
{
    /// <summary>本地音乐：扫描文件夹里的音频文件（自动识别标签），点歌即播（负 id 走本地文件）。</summary>
    public sealed partial class LocalMusicPage : Page
    {
        private bool _scanning;

        public LocalMusicPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            FolderText.Text = Ncrust.Platform.LocalMusicService.ScanFolder;
            _ = RescanAsync();
        }

        private async Task RescanAsync()
        {
            if (_scanning)
            {
                return;
            }

            _scanning = true;
            EmptyHint.Text = "正在扫描本地音乐…";
            EmptyHint.Visibility = Visibility.Visible;
            try
            {
                var songs = await Task.Run(() => Ncrust.Platform.LocalMusicService.Scan());
                SongList.ItemsSource = songs;
                EmptyHint.Text = songs.Count == 0 ? "没有找到音乐文件，点「选择文件夹」指定目录" : string.Empty;
                EmptyHint.Visibility = songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
                EmptyHint.Text = "扫描失败，请检查文件夹权限";
            }
            finally
            {
                _scanning = false;
            }
        }

        private void SongItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SongItem song)
            {
                PlaybackHost.PlaySong(song);
            }
        }

        private async void PickFolderClick(object sender, RoutedEventArgs e)
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

            AppServices.Settings.SetString(Ncrust.Platform.LocalMusicService.FolderKey, folder.Path);
            FolderText.Text = folder.Path;
            await RescanAsync();
        }

        private async void RefreshClick(object sender, RoutedEventArgs e) => await RescanAsync();
    }
}
