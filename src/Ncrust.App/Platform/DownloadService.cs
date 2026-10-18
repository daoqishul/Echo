using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Ncrust.Core.Api;
using Ncrust.Core.Playback;

namespace Ncrust.Platform
{
    /// <summary>
    /// 歌曲下载：按所选音质走 <see cref="SongUrlResolver"/> 取链（https 改写、直连不走代理），
    /// 保存到设置里的下载目录（默认「音乐\Echo」）。文件命名：歌手 - 歌名.mp3/flac。
    /// </summary>
    internal static class DownloadService
    {
        public const string FolderKey = "download_folder";

        public static string DownloadFolder =>
            AppServices.Settings.GetString(FolderKey)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Echo");

        /// <summary>下载一首歌。成功返回文件路径；失败返回 null。</summary>
        public static async Task<string?> DownloadAsync(SongItem song, string level)
        {
            try
            {
                var result = await AppServices.UrlResolver
                    .ResolveAsync(song.Id, level, AppServices.IsSessionValid, song.Privilege)
                    .ConfigureAwait(false);
                if (!result.HasUrl)
                {
                    return null;
                }

                var url = result.Url!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    ? "https://" + result.Url.Substring("http://".Length)
                    : result.Url;

                Directory.CreateDirectory(DownloadFolder);

                // 下载只提供 mp3 三档（见 SongActions.DownloadAsync），统一保存为 .mp3。
                var path = Path.Combine(DownloadFolder, SafeName(song.ArtistText + " - " + song.Name) + ".mp3");

                using (var handler = new HttpClientHandler
                {
                    UseCookies = false,
                    UseProxy = false,
                    Proxy = null,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                })
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) })
                {
                    var bytes = await client.GetByteArrayAsync(url).ConfigureAwait(false);
                    File.WriteAllBytes(path, bytes);
                }

                return path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把文件名里的非法字符替换掉。</summary>
        private static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }
    }
}
