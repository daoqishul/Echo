using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ncrust.Core.Api;
using TagLib;

namespace Ncrust.Platform
{
    /// <summary>
    /// 本地音乐：递归扫描文件夹里的音频文件，用 TagLib 读标签（标题 / 歌手 / 专辑 / 时长），
    /// 转成 <see cref="SongItem"/> 供列表与播放复用。本地歌曲用**负 id**（由路径散列而来），
    /// 引擎据此走本地文件而不是取链；id → 路径 的映射持久化到数据目录，
    /// 播放队列恢复后仍能找到文件（文件被移动则跳过）。
    /// </summary>
    internal static class LocalMusicService
    {
        public const string FolderKey = "local_music_folder";

        private static readonly string[] Extensions =
            { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".wma" };

        private static readonly Dictionary<long, string> Paths = new Dictionary<long, string>();
        private static bool _loaded;

        public static string ScanFolder =>
            AppServices.Settings.GetString(FolderKey)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        /// <summary>本地歌曲 id → 文件路径（引擎取本地源用）。</summary>
        public static string? PathFor(long id)
        {
            EnsureLoaded();
            return Paths.TryGetValue(id, out var path) ? path : null;
        }

        /// <summary>扫描当前文件夹，返回歌曲列表（后台线程调用）。</summary>
        public static IReadOnlyList<SongItem> Scan()
        {
            EnsureLoaded();
            var folder = ScanFolder;
            var songs = new List<SongItem>();
            if (!Directory.Exists(folder))
            {
                return songs;
            }

            foreach (var file in EnumerateAudio(folder))
            {
                SongItem song;
                try
                {
                    song = ReadTags(file);
                }
                catch
                {
                    continue; // 读不了标签的文件跳过（损坏 / 不支持）。
                }

                Paths[song.Id] = file;
                songs.Add(song);
            }

            SaveRegistry();
            return songs;
        }

        private static IEnumerable<string> EnumerateAudio(string folder)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
            }
            catch (UnauthorizedAccessException)
            {
                files = Directory.EnumerateFiles(folder);
            }

            return files.Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }

        private static SongItem ReadTags(string path)
        {
            using (var tagFile = TagLib.File.Create(path))
            {
                var tag = tagFile.Tag;
                var title = string.IsNullOrWhiteSpace(tag.Title) ? Path.GetFileNameWithoutExtension(path) : tag.Title;
                var artist = string.Join("/", tag.Performers.Where(p => !string.IsNullOrWhiteSpace(p)));
                if (artist.Length == 0)
                {
                    artist = string.Join("/", tag.AlbumArtists.Where(p => !string.IsNullOrWhiteSpace(p)));
                }

                if (artist.Length == 0)
                {
                    artist = "未知歌手";
                }

                var album = string.IsNullOrWhiteSpace(tag.Album) ? "本地音乐" : tag.Album;

                var song = new SongItem
                {
                    Id = StableId(path),
                    Name = title,
                    Artists = new[] { new ArtistRef { Name = artist } },
                    Album = new AlbumRef { Name = album, PicUrl = string.Empty },
                    Duration = (long)tagFile.Properties.Duration.TotalMilliseconds,
                };
                return song;
            }
        }

        /// <summary>负 id：hash(path) 取负，标记「本地歌曲」且与网易的正 id 空间不冲突。</summary>
        private static long StableId(string path)
        {
            unchecked
            {
                var hash = 1125899906842597L;
                foreach (var c in path)
                {
                    hash = hash * 31 + c;
                }

                return -(Math.Abs(hash) % 9_000_000_000L) - 1;
            }
        }

        private static string RegistryPath => Path.Combine(NcrustPaths.DataDirectory, "local_music.txt");

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            try
            {
                if (!System.IO.File.Exists(RegistryPath))
                {
                    return;
                }

                foreach (var line in System.IO.File.ReadAllLines(RegistryPath))
                {
                    var separator = line.IndexOf('\t');
                    if (separator <= 0 || !long.TryParse(line.Substring(0, separator), out var id))
                    {
                        continue;
                    }

                    Paths[id] = line.Substring(separator + 1);
                }
            }
            catch
            {
                // 注册表损坏：忽略，重新扫描会重建。
            }
        }

        private static void SaveRegistry()
        {
            try
            {
                var lines = Paths.Select(pair => pair.Key + "\t" + pair.Value);
                System.IO.File.WriteAllLines(RegistryPath, lines);
            }
            catch
            {
                // 落盘失败不致命：本次会话内仍可用。
            }
        }
    }
}
