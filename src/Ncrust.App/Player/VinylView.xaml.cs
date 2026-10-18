using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Ncrust.Core.Api;
using Ncrust.Playback;

namespace Ncrust.Player
{
    /// <summary>
    /// 歌词页：纯歌词居中（宽度按设置 窄/标准/宽），右下角上一首 / 播放 / 下一首。
    /// 背景完全不透明（遮住主界面）。
    /// </summary>
    public sealed partial class VinylView : UserControl
    {
        public const string WidthKey = "lyrics_width"; // narrow | standard | wide

        private bool _initialized;
        private LyricsController _lyrics;

        public event Action CloseRequested;

        public VinylView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Lyrics.LineFontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Microsoft YaHei UI");
            Lyrics.LineFontWeight = Microsoft.UI.Text.FontWeights.Bold;
            _lyrics = new LyricsController(Lyrics, LyricsStatus);
            PlayerHost.LyricsSettingsChanged += _lyrics.Reload;

            var engine = PlaybackHost.Engine;
            engine.CurrentSongChanged += OnSongChanged;
            engine.ProgressChanged += OnProgress;
            engine.IsPlayingChanged += OnPlayingChanged;

            OnSongChanged(Ncrust.AppServices.Queue.Current);
        }

        /// <summary>歌词面板是否可见：不可见时停掉边界定时器。</summary>
        public bool Active
        {
            get => _lyrics?.Active ?? false;
            set
            {
                if (_lyrics != null)
                {
                    _lyrics.Active = value;
                }
            }
        }

        public void Show()
        {
            // 背景永远不透明（跟随主题与黑度，不受云母 / 亚克力半透明影响），遮住主界面。
            Root.Background = Ncrust.Shell.AppearanceService.OpaqueBackgroundBrush();

            // 歌词栏宽度：每次打开时按当前设置生效。
            LyricsHost.MaxWidth = Ncrust.AppServices.Settings.GetString(WidthKey) switch
            {
                "narrow" => 480,
                "wide" => 840,
                _ => 640,
            };

            Visibility = Visibility.Visible;
            Active = true;
            OnSongChanged(Ncrust.AppServices.Queue.Current);
            OnPlayingChanged(PlaybackHost.Engine.IsPlaying);
        }

        public void Hide()
        {
            Active = false;
            Visibility = Visibility.Collapsed;
        }

        private void OnSongChanged(SongItem song)
        {
            _lyrics?.OnSongChanged(song);

            if (song == null)
            {
                TitleText.Text = "歌词";
                ArtistText.Text = string.Empty;
                return;
            }

            TitleText.Text = song.Name;
            ArtistText.Text = song.ArtistText;
            OnProgress(0, 0);
        }

        private void OnProgress(long positionMs, long durationMs) => _lyrics?.OnProgress(positionMs);

        private void OnPlayingChanged(bool playing)
        {
            _lyrics?.OnPlayingChanged(playing);
            PlayIcon.Glyph = playing ? Ncrust.Pages.Glyphs.Pause : Ncrust.Pages.Glyphs.Play;
        }

        private void PreviousClick(object sender, RoutedEventArgs e) => PlaybackHost.Engine.Previous();

        private void PlayPauseClick(object sender, RoutedEventArgs e) => PlaybackHost.Engine.PlayPause();

        private void NextClick(object sender, RoutedEventArgs e) => PlaybackHost.Engine.Next();

        private void CollapseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    }
}
