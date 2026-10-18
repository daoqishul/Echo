using System;
using System.Collections.Generic;
using System.Linq;
using Ncrust.Core.Account;
using Ncrust.Core.Api;
using Ncrust.Core.Audio;
using Ncrust.Core.Net;
using Ncrust.Core.Platform;
using Ncrust.Core.Playback;
using Windows.Foundation.Collections;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Ncrust.Playback
{
    /// <summary>
    /// 把 <see cref="PlaybackSessionState"/> 的决定落到系统播放器上（对应 Android 的 PlaybackService）。
    ///
    /// - 线程：MediaPlayer / MediaPlaybackList 的事件在后台线程触发，这里统一封送到 UI 线程再处理，
    ///   所以本类的**所有公开事件都在 UI 线程上触发**，订阅方可以直接改界面；队列状态也只在 UI 线程上改。
    ///   唯一在后台线程运行的是 MediaBinder 的 Binding —— 它只读闭包里捕获的不可变参数，不碰共享状态。
    /// - 滑动窗口：<see cref="MediaPlaybackList"/> 只放「当前 + 下一首」，不用它自带的 Shuffle/AutoRepeat；
    ///   当前项变化时队列前进一步（<see cref="PlaybackQueue.MoveNext"/>）、补上新的下一首、裁掉已播项。
    /// - 延迟取链：每个条目用 <see cref="MediaBinder"/>，URL 在 Binding 事件里拿 deferral 异步解析；
    ///   元数据在创建时挂到 <see cref="MediaPlaybackItem"/> 上，换歌不会串元数据。
    /// - 质量降级：ItemFailed → <see cref="QualityLadder.NextRetryLevel"/> 在原位置重建条目
    ///   （同一 songId@level 3 秒内只重试一次）；失败的是当前曲则切回重建的条目继续播。
    /// - 进度：500ms ticker 读位置/时长（约 2Hz）；播放上报（听歌打卡）按 <see cref="PlayReportPolicy"/>
    ///   去重后发两条 eapi 上报：起播时 startplay、听完（或 ≥80%）时 play。
    /// - SMTC：由 MediaPlayer + DisplayProperties 自动接管（媒体键、系统浮窗）。
    /// </summary>
    internal sealed class PlaybackEngine
    {
        private static readonly TimeSpan RetryDedupeWindow = TimeSpan.FromSeconds(3);

        private readonly MediaPlayer _player = new MediaPlayer();
        private readonly DispatcherTimer _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        private readonly SongUrlResolver _resolver;
        private readonly PlaybackSessionState _session;
        private readonly PlaybackPreferences _preferences;
        private readonly INetworkInfo _network;
        private readonly NcmHttp _http;
        private readonly Func<bool> _sessionValid;
        private readonly PlayReportPolicy _reportPolicy = new PlayReportPolicy();

        /// <summary>试听 / 封顶 / 跳过的提示去重与合并（规则见 spec/api/entitlement.md §界面要求）。</summary>
        private readonly PlaybackNoticeTracker _notices = new PlaybackNoticeTracker();

        // 只在 UI 线程上读写。
        private readonly Dictionary<string, DateTime> _lastRetryAt = new Dictionary<string, DateTime>();

        // 均衡器参数：与音效组件按引用共享，改值即生效（见 EqualizerEffectKeys）。
        private readonly PropertySet _equalizer = new PropertySet();

        private readonly InfinityFeeder _infinity;

        private DispatcherQueue _dispatcher;
        private long _currentSongId = -1;
        private int _consecutiveFailures;

        // 当前在播（或即将起播）的条目信息：无列表窗口后一次只有一首。
        private ItemInfo _currentInfo;

        // 异步取链（CreateItemAsync）的防串：PlayCurrent 每次调用都推进版本，
        // 取链返回时版本对不上说明期间又换了歌，丢弃结果。
        private int _playVersion;

        // INFINITY 续播：同一时刻只取一批；取回时若已经停在队尾（播完 / 用户按了下一首），接着播。
        private bool _infinityInFlight;

        // 上次会话恢复完（或已经开始播放）之前不落盘：启动时恢复播放模式会调 SetMode，
        // 那时队列还是空的，存下去就把上次保存的队列覆盖成空（实测：重启后队列丢失）。
        private bool _stateReady;
        private bool _advanceWhenFed;

        public PlaybackEngine(
            SongUrlResolver resolver,
            PlaybackSessionState session,
            PlaybackPreferences preferences,
            INetworkInfo network,
            NcmHttp http,
            InfinityFeeder infinity,
            Func<bool> sessionValid)
        {
            _infinity = infinity;
            _resolver = resolver;
            _session = session;
            _preferences = preferences;
            _network = network;
            _http = http;
            _sessionValid = sessionValid ?? (() => false);
        }

        public event Action<SongItem> CurrentSongChanged;

        public event Action<bool> IsPlayingChanged;

        public event Action<bool> BufferingChanged;

        public event Action PlaybackEnded;

        public event Action<long> PlaybackError;

        /// <summary>(positionMs, durationMs)，约 2Hz。</summary>
        public event Action<long, long> ProgressChanged;

        /// <summary>播放模式变化（按钮切换后）。</summary>
        public event Action<PlaybackMode> ModeChanged;

        /// <summary>队列内容或当前位置变化（队列视图据此刷新）。</summary>
        public event Action QueueChanged;

        /// <summary>
        /// 当前曲实际在播的音质档位变化（对应 Android 播放器的音质标签 currentQualityIndex）：
        /// 起播时是请求档位，取到 URL 后换成取链实际落到的档位，解码失败降档后再更新。参数为 null 表示没有在播。
        /// </summary>
        public event Action<string> QualityChanged;

        /// <summary>
        /// 需要提示一次的播放权益事件（试听 / 音质封顶 / 跳过）。UI 线程上触发，文案由界面渲染。
        /// </summary>
        public event Action<PlaybackNotice> NoticeRaised;

        /// <summary>当前曲实际在播的档位（API 名，如 lossless）；没有在播为 null。</summary>
        public string CurrentLevel => _currentInfo?.DisplayLevel;

        /// <summary>当前曲是否只能在播试听片段（界面据此把音质徽标换成「试听」）。</summary>
        public bool CurrentIsTrial => _currentSongId > 0 && _notices.IsTrial(_currentSongId);

        /// <summary>
        /// 私人 FM 入口进来的 INFINITY：队尾续播拉 FM 流而不是相似歌曲（对应 Android fmMode）。
        /// 手动切换播放模式或整队替换时清掉。
        /// </summary>
        public bool FmMode { get; set; }

        public long CurrentSongId => _currentSongId;

        public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

        public PlaybackMode Mode => _session.Queue.Mode;

        public long PositionMs => (long)_player.PlaybackSession.Position.TotalMilliseconds;

        public long DurationMs => (long)_player.PlaybackSession.NaturalDuration.TotalMilliseconds;

        /// <summary>0..1。</summary>
        public double Volume
        {
            get => _player.Volume;
            set => _player.Volume = Math.Max(0, Math.Min(1, value));
        }

        public bool IsMuted
        {
            get => _player.IsMuted;
            set => _player.IsMuted = value;
        }

        /// <summary>音效组件是否成功挂到播放器上（失败时设置页提示均衡器不可用）。</summary>
        public bool EqualizerAvailable { get; private set; }

        /// <summary>临时开关：CsWinRT 音效组件在个别系统上会引发原生崩溃，此时整体禁用。</summary>
        internal static bool DisableEqualizer { get; set; }

        /// <summary>
        /// 把 10 段均衡器挂到播放器上（启动时调用一次）。作为「可选」音效添加：万一管线插不进去，
        /// 照常播放、只是没有均衡器，而不是整首歌放不出来。
        /// </summary>
        public void AttachEqualizer(EqualizerState state)
        {
            ApplyEqualizer(state);
            if (DisableEqualizer)
            {
                return; // 排查用开关：音效组件在本机引发原生崩溃时整体禁用。
            }

            try
            {
                _player.AddAudioEffect(EqualizerEffectKeys.ActivatableClassId, true, _equalizer);
                EqualizerAvailable = true;
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
        }

        /// <summary>更新均衡器参数，下一帧生效。</summary>
        public void ApplyEqualizer(EqualizerState state)
        {
            _equalizer[EqualizerEffectKeys.GainsDb] = state.GainsDb.ToArray();
            _equalizer[EqualizerEffectKeys.PreampDb] = state.PreampDb;
            _equalizer[EqualizerEffectKeys.Enabled] = state.Enabled;
        }

        /// <summary>必须在 UI 线程上调用（DispatcherTimer 与 DispatcherQueue 都取自主视图）。</summary>
        public void Initialize()
        {
            _dispatcher = App.UiQueue;

            _player.AudioCategory = MediaPlayerAudioCategory.Media;
            _player.CommandManager.IsEnabled = true;
            _player.MediaEnded += (sender, args) => OnUi(OnMediaEnded);
            _player.MediaFailed += (sender, args) =>
            {
                App.Trace("mediafailed: " + args.ErrorMessage);
                OnUi(OnItemFailed);
            };
            _player.PlaybackSession.PlaybackStateChanged += (sender, args) =>
            {
                App.Trace("state: " + _player.PlaybackSession.PlaybackState + " natural=" + _player.PlaybackSession.NaturalDuration);
                OnUi(OnPlaybackStateChanged);
            };
            _player.PlaybackSession.BufferingStarted += (sender, args) => OnUi(() => BufferingChanged?.Invoke(true));
            _player.PlaybackSession.BufferingEnded += (sender, args) => OnUi(() => BufferingChanged?.Invoke(false));
            _ticker.Tick += OnTick;
            _ticker.Start();
        }

        /// <summary>按当前队列与模式从当前曲开始播放（替换队列、跳转、后退后调用）。</summary>
        public void PlayCurrent()
        {
            _stateReady = true;
            ResetWindow();
            var current = _session.Queue.Current;
            if (current == null)
            {
                return;
            }

            // 先同步更新界面（标题 / 队列高亮），条目与 URL 异步创建好后再起播。
            SetCurrentSong(current);
            QueueChanged?.Invoke();
            QualityChanged?.Invoke(CurrentLevel);

            var version = ++_playVersion;
            _ = FeedCurrentAsync(current, RequestedLevel(), version);
        }

        /// <summary>取链 → 建条目 → 起播。UI 线程上的异步完成路径。</summary>
        private async System.Threading.Tasks.Task FeedCurrentAsync(SongItem song, string level, int version)
        {
            var item = await CreateItemAsync(song, level);
            if (version != _playVersion || _session.Queue.Current?.Id != song.Id)
            {
                return; // 期间又点了别的歌 / 队列被改：这次作废。
            }

            _player.Source = item;
            _player.Play();
            TryStartReport();
            QueueChanged?.Invoke();
            QualityChanged?.Invoke(CurrentLevel);
        }

        /// <summary>清空队列后调用：停止播放、清空源。</summary>
        public void Stop()
        {
            _player.Pause();
            ResetWindow();
            _advanceWhenFed = false;
            _currentSongId = -1;
            CurrentSongChanged?.Invoke(null);
            QualityChanged?.Invoke(null);
            QueueChanged?.Invoke();
        }

        public void PlayPause()
        {
            if (IsPlaying)
            {
                _player.Pause();
            }
            else if (_currentInfo == null)
            {
                // 刚从上次会话恢复、还没起播过：从队列的当前曲开始播。
                PlayCurrent();
            }
            else
            {
                _player.Play();
            }
        }

        /// <summary>启动时恢复了上次的队列：只把当前曲显示出来，不自动播放。</summary>
        public void ShowRestored()
        {
            _stateReady = true;
            var current = _session.Queue.Current;
            if (current != null)
            {
                SetCurrentSong(current);
            }
        }

        public void Pause() => _player.Pause();

        /// <summary>下一首（对应 Android playNext）。无窗口列表：直接由队列决定后重建源。</summary>
        public void Next()
        {
            if (_session.Queue.MoveNext() != null)
            {
                PlayCurrent();
                return;
            }

            // INFINITY 停在队尾：等续播取回后接着播。
            if (_session.Queue.Mode == PlaybackMode.Infinity)
            {
                _advanceWhenFed = true;
                LaunchInfinity();
            }
        }

        /// <summary>上一首（对应 Android playPrevious）：SINGLE 与不可回退时回到开头。</summary>
        public void Previous()
        {
            if (_session.Queue.Mode == PlaybackMode.Single || _session.Queue.MovePrevious() == null)
            {
                Seek(0);
                return;
            }

            PlayCurrent();
        }

        public void Seek(long positionMs) =>
            _player.PlaybackSession.Position = TimeSpan.FromMilliseconds(Math.Max(0, positionMs));

        /// <summary>切换播放模式（窗口列表已移除，只需通知模式变化）。</summary>
        public void SetMode(PlaybackMode mode)
        {
            FmMode = false;
            _session.Queue.SetMode(mode);
            ModeChanged?.Invoke(mode);
            SaveState();
        }

        /// <summary>队列被编辑后调用：无窗口列表，只通知队列视图刷新。</summary>
        public void RefreshNext() => QueueChanged?.Invoke();

        /// <summary>队列在外部被改动（排序 / 删除 / 插入）后由调用方触发保存。</summary>
        public void SaveState()
        {
            if (_stateReady)
            {
                _ = SaveStateAsync();
            }
        }

        private string RequestedLevel() => _preferences.QualityForNetwork(_network.IsMetered);

        private void OnUi(Action action)
        {
            if (_dispatcher == null || _dispatcher.HasThreadAccess)
            {
                Guard(action);
                return;
            }

            _dispatcher.TryEnqueue(() => Guard(action));
        }

        private static void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // 播放回调里的异常不能带崩整个进程；记日志，播放器保持现状。
                App.WriteCrashLog(ex);
            }
        }

        private void ResetWindow()
        {
            _currentInfo = null;
            try
            {
                _player.Source = null;
            }
            catch
            {
                // 置空源失败不影响后续播放（下一次 PlayCurrent 会换新源）。
            }
        }

        /// <summary>
        /// 创建播放条目：先同步取链（拿 deferral 的语义变成「创建条目前取链」），再以
        /// <see cref="MediaSource.CreateFromUri"/> 建源。网易 CDN 的 URL 有效期远大于取链周期，
        /// 惰性打开 + https 改写后本机 MF 可稳定播放（MediaBinder + SetUri 实测 SourceNotSupported）。
        /// 必须只在 UI 线程调用。
        /// </summary>
        private async System.Threading.Tasks.Task<MediaPlaybackItem> CreateItemAsync(SongItem song, string level)
        {
            var info = new ItemInfo(song, level);

            // 本地歌曲（负 id）：跳过取链，直接以文件建源（本地音乐页扫描进来的文件）。
            if (song.Id < 0)
            {
                var localPath = Ncrust.Platform.LocalMusicService.PathFor(song.Id);
                if (!string.IsNullOrEmpty(localPath) && System.IO.File.Exists(localPath))
                {
                    info.ActualLevel = "local";
                    info.Entitlement = new PlaybackEntitlement.Full(false, null, null);
                    var localItem = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(System.IO.Path.GetFullPath(localPath))));
                    ApplyDisplayProperties(localItem, song);
                    _currentInfo = info;
                    return localItem;
                }

                // 文件没了：空 binder，起播后走 MediaFailed → 跳歌。
                info.Entitlement = new PlaybackEntitlement.Unavailable(EntitlementReason.Unknown);
                var missingItem = new MediaPlaybackItem(MediaSource.CreateFromMediaBinder(new MediaBinder { Token = song.Id + "@local" }));
                ApplyDisplayProperties(missingItem, song);
                _currentInfo = info;
                return missingItem;
            }

            // 会话是否有效也在这一刻读（判定结果只影响原因文案）：设置项「非会员遇到 VIP 歌曲」
            // 需要它决定是播试听还是跳过。
            var sessionValid = _sessionValid();
            var playTrial = _preferences.VipTrialPlayback;

            ResolvedSongUrl result;
            try
            {
                result = await _resolver.ResolveAsync(song.Id, level, sessionValid, song.Privilege);
            }
            catch (Exception ex)
            {
                // 取链失败：按「没拿到 URL」处理，条目会走 ItemFailed → 降级。
                App.Trace($"resolve {song.Id} failed: {ex.Message}");
                result = null;
            }

            info.Entitlement = result?.Entitlement;
            MediaPlaybackItem item;

            if (result != null && result.HasUrl && !(result.Entitlement is PlaybackEntitlement.Trial && !playTrial))
            {
                // Media Foundation 的网络源在部分 Windows 上打不开 http:// 流（卡 Opening 或
                // SourceNotSupportedError），而网易 CDN 对同一 URL 的 https:// 完全可用
                // （cdntag=os_web 本就是 web 身份）。统一改写 https，避免明文流被网络设备拦截。
                var playUrl = result.Url!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    ? "https://" + result.Url.Substring("http://".Length)
                    : result.Url;
                info.ActualLevel = result.ActualLevel;
                item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(playUrl)));

                // 试听 / 封顶先记账再刷新标签：徽标要显示「试听」而不是拿到的那一档。
                // 提示只在真正开播时发；去重规则在 PlaybackNoticeTracker 里（每首试听一次、封顶每进程一次）。
                _notices.NotePlayableStarted();
                if (result.Entitlement is PlaybackEntitlement.Trial trial)
                {
                    _notices.RecordTrial(song.Id, trial.TrialSeconds, trial.Reason);
                }
                else if (result.Entitlement is PlaybackEntitlement.Full { Capped: true } full)
                {
                    _notices.RecordCapped(
                        song.Id,
                        full.CappedTo ?? result.ActualLevel,
                        full.CappedReason ?? EntitlementReason.Unknown);
                }

                if (ReferenceEquals(_currentInfo, info))
                {
                    QualityChanged?.Invoke(CurrentLevel);
                    // 起播时取链若还没回来，startplay 推迟到这里：要先知道是不是试听片段。
                    TryStartReport();
                }

                Raise(_notices.TrialNoticeOnStart(song.Id));
                Raise(_notices.CappedNoticeOnStart(song.Id));
            }
            else
            {
                // 不可播，或设置要求跳过试听：空 binder（不 SetUri），起播后由 MediaFailed → 统一跳歌并说明原因。
                var binder = new MediaBinder { Token = song.Id + "@" + level };
                item = new MediaPlaybackItem(MediaSource.CreateFromMediaBinder(binder));
            }

            ApplyDisplayProperties(item, song);
            _currentInfo = info;
            return item;
        }

        /// <summary>挂 SMTC 元数据（标题 / 歌手 / 专辑 / 封面缩略图）。</summary>
        private static void ApplyDisplayProperties(MediaPlaybackItem item, SongItem song)
        {
            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = song.Name;
            props.MusicProperties.Artist = song.ArtistText;
            props.MusicProperties.AlbumTitle = song.Album?.Name ?? string.Empty;
            if (!string.IsNullOrEmpty(song.Album?.PicUrl))
            {
                try
                {
                    props.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(song.Album.PicUrl));
                }
                catch
                {
                    // 封面 URL 非法时忽略缩略图。
                }
            }

            item.ApplyDisplayProperties(props);
        }

        /// <summary>队尾续播：取一批 FM / 相似歌曲追加到队尾（无窗口列表，直接入队列）。</summary>
        private async void LaunchInfinity()
        {
            var seed = _session.Queue.Current;
            if (_infinityInFlight || seed == null)
            {
                return;
            }

            _infinityInFlight = true;
            try
            {
                var existing = _session.Queue.Songs.Select(song => song.Id).ToList();
                var continuation = await _infinity.FetchAsync(FmMode, seed.Id, existing);

                // 取数期间用户可能切了模式或换了队列：只在仍是 INFINITY 时追加。
                if (continuation.Count == 0 || _session.Queue.Mode != PlaybackMode.Infinity)
                {
                    _advanceWhenFed = false;
                    return;
                }

                _session.Queue.AppendAll(continuation);
                SaveState();

                if (_advanceWhenFed)
                {
                    _advanceWhenFed = false;
                    if (_session.Queue.MoveNext() != null)
                    {
                        PlayCurrent();
                        return;
                    }
                }

                QueueChanged?.Invoke();
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
            finally
            {
                _infinityInFlight = false;
            }
        }

        private void OnPlaybackStateChanged()
        {
            var playing = IsPlaying;
            if (playing)
            {
                _consecutiveFailures = 0;
            }

            IsPlayingChanged?.Invoke(playing);
        }

        /// <summary>
        /// 一首歌所有音质都失败（对应 Android 的 onUnplayable → playNext）：是当前曲就跳到下一首。
        /// 连续失败达到队列长度就停下，避免整队都放不了时无限空转。
        /// </summary>
        /// <param name="reason">不可播的原因（来自取链判定），用于「已跳过《歌名》：<原因>」。</param>
        /// <param name="songName">歌名；拿不到时传空串（提示会退化成合并计数）。</param>
        private void SkipUnplayable(long songId, string songName, EntitlementReason reason)
        {
            PlaybackError?.Invoke(songId);

            // 只在实际跳过当前曲时提示：连续多首会合并成一条计数（见 PlaybackNoticeTracker）。
            Raise(_notices.SkipNotice(songId, songName, reason));

            if (++_consecutiveFailures >= Math.Max(1, _session.Queue.Songs.Count))
            {
                _player.Pause();
                return;
            }

            Next();
        }

        private void SetCurrentSong(SongItem song)
        {
            _currentSongId = song.Id;
            CurrentSongChanged?.Invoke(song);

            // 试听 / 音质封顶的提示在**真正开播**这一刻发。
            // 慢网络下取链晚于起播时，取链回调里还会再试一次（tracker 按歌去重，不会重复提示）。
            Raise(_notices.TrialNoticeOnStart(song.Id));
            Raise(_notices.CappedNoticeOnStart(song.Id));
        }

        /// <summary>MediaFailed（当前条目失败）：沿音质阶梯降档重建，全失败则跳歌。</summary>
        private void OnItemFailed()
        {
            var info = _currentInfo;
            if (info == null || info.Song.Id != _currentSongId)
            {
                // 拿不到条目信息（理论上不会发生）：按未知原因跳歌。
                SkipUnplayable(_currentSongId, string.Empty, EntitlementReason.Unknown);
                return;
            }

            // 取链判定已经给过结论（无版权 / 需要会员，或设置要求跳过试听）：不必再沿阶梯重试，
            // 直接跳歌并说明原因 —— 否则每一档都要再打一次网络，最后还是跳。
            if (info.Entitlement is PlaybackEntitlement.Unavailable unavailable)
            {
                SkipUnplayable(info.Song.Id, info.Song.Name, unavailable.Reason);
                return;
            }

            if (info.Entitlement is PlaybackEntitlement.Trial trial && !_preferences.VipTrialPlayback)
            {
                SkipUnplayable(info.Song.Id, info.Song.Name, trial.Reason);
                return;
            }

            var nextLevel = QualityLadder.NextRetryLevel(info.Level);
            var key = info.Song.Id + "@" + info.Level;
            var now = DateTime.UtcNow;
            if (nextLevel == null ||
                (_lastRetryAt.TryGetValue(key, out var last) && now - last < RetryDedupeWindow))
            {
                SkipUnplayable(info.Song.Id, info.Song.Name, EntitlementReason.Unknown);
                return;
            }

            _lastRetryAt[key] = now;
            _ = RebuildAsync(info.Song, nextLevel);
        }

        /// <summary>降级重建条目：取链成功后换源继续播同一首。</summary>
        private async System.Threading.Tasks.Task RebuildAsync(SongItem song, string level)
        {
            MediaPlaybackItem replacement;
            try
            {
                replacement = await CreateItemAsync(song, level);
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
                return;
            }

            if (_session.Queue.Current?.Id != song.Id)
            {
                return; // 期间队列变了：丢弃。
            }

            _player.Source = replacement;
            _player.Play();
            TryStartReport();
            QualityChanged?.Invoke(CurrentLevel);
        }

        private void OnMediaEnded()
        {
            ReportCurrent(ended: true);

            if (_session.Queue.Mode == PlaybackMode.Infinity && _session.Queue.PeekNext() == null)
            {
                // 队列到了尾：INFINITY 等续播取回后接着播。
                _advanceWhenFed = true;
                LaunchInfinity();
            }
            else if (_session.Queue.MoveNext() != null)
            {
                // 由队列状态机决定下一首（CYCLE 回绕、LINE 停在队尾不动），再起播。
                PlayCurrent();
            }
            else
            {
                _player.Pause();
            }

            PlaybackEnded?.Invoke();
        }

        private void OnTick(object sender, object e)
        {
            var position = PositionMs;
            var duration = DurationMs;
            _session.UpdateProgress(position, duration);
            ProgressChanged?.Invoke(position, duration);

            FirePlayReport(ended: false, position, duration, CurrentIsTrial);
        }

        private void ReportCurrent(bool ended)
        {
            var duration = DurationMs;
            FirePlayReport(ended, duration, duration, CurrentIsTrial);
        }

        /// <summary>
        /// 当前曲取链已完成时发 startplay；还没完成就等取链回调再来（试听片段不发，判定要等取链结果）。
        /// 起播与取链回调两处都会调，<see cref="PlayReportPolicy.TryAcquireStart"/> 按歌去重。
        /// </summary>
        private void TryStartReport()
        {
            if (_currentInfo?.Entitlement == null)
            {
                return;
            }

            FireStartReport(_currentSongId, CurrentIsTrial);
        }

        /// <summary>把一次权益提示交给界面（UI 线程上调用）；tracker 用 null 表示「这次不提示」。</summary>
        private void Raise(PlaybackNotice notice)
        {
            if (notice != null)
            {
                NoticeRaised?.Invoke(notice);
            }
        }

        /// <summary>
        /// 开始播一首就把 startplay 发出去（听歌打卡第一步，进「最近播放」）。挂点与
        /// <see cref="SetCurrentSong"/> 同源：<see cref="PlayCurrent"/>（起播 / 手动切歌）与
        /// <see cref="OnCurrentItemChanged"/>（无缝衔接）都算「真的开始播了」，重复调用由
        /// <see cref="PlayReportPolicy.TryAcquireStart"/> 去重；恢复上次会话不播，因此不发。
        ///
        /// <paramref name="isTrial"/>：试听片段（权益判定 trial）不发，调用处传 <see cref="CurrentIsTrial"/>。
        /// </summary>
        private void FireStartReport(long songId, bool isTrial = false)
        {
            if (!_reportPolicy.TryAcquireStart(songId, isTrial))
            {
                return;
            }

            SendReport(PlayReport.BuildStartplayLogs(songId), songId);
        }

        /// <summary>
        /// 「听完」上报的唯一出口：自然结束（<paramref name="ended"/> 为 true）或进度 ≥ 80% 时发
        /// play（涨「听歌排行」）。
        ///
        /// 试听片段（<paramref name="isTrial"/>）在**记去重之前**就挡掉，与 Android 端
        /// <c>currentIsTrial</c> 的位置一致：被抑制不算「已上报」，同一首之后按完整播放走时仍会发。
        /// </summary>
        private void FirePlayReport(bool ended, long positionMs, long durationMs, bool isTrial = false)
        {
            if (!_reportPolicy.TryAcquire(_currentSongId, positionMs, durationMs, ended, isTrial))
            {
                return;
            }

            SendReport(
                PlayReport.BuildPlayLogs(_currentSongId, positionMs, isWifi: !_network.IsMetered),
                _currentSongId);
        }

        /// <summary>
        /// 发给 clientlog 的 eapi 通道（os=osx 身份）。发出后不管结果、不阻塞播放；未登录不发。
        /// </summary>
        private void SendReport(string logsJson, long songId)
        {
            if (songId <= 0 || string.IsNullOrEmpty(_http.Cookie))
            {
                return;
            }

            _ = _http.EapiPostOsxAsync(PlayReport.WeblogPath, PlayReport.WeblogFields(logsJson));
        }

        private async System.Threading.Tasks.Task SaveStateAsync()
        {
            try
            {
                await _session.SaveAsync();
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
        }

        private sealed class ItemInfo
        {
            public ItemInfo(SongItem song, string level)
            {
                Song = song;
                Level = level;
            }

            public SongItem Song { get; }

            /// <summary>请求的档位（降档重试会用更低的档位新建条目）。</summary>
            public string Level { get; }

            /// <summary>取链实际落到的档位。</summary>
            public string ActualLevel { get; set; }

            /// <summary>取链的权益判定（跳歌提示的原因取自它）；还没取链时为 null。</summary>
            public PlaybackEntitlement Entitlement { get; set; }

            public string DisplayLevel => ActualLevel ?? Level;
        }
    }
}
