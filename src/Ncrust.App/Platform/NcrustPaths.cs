using System;
using System.IO;

namespace Ncrust.Platform
{
    /// <summary>
    /// 应用私有数据目录（非打包应用没有 ApplicationData，统一放 %LOCALAPPDATA%\Ncrust）。
    /// 设置 / 文件 / 登录凭据 / 日志都从这里派生。
    /// </summary>
    internal static class NcrustPaths
    {
        private static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ncrust");

        /// <summary>大块数据文件（队列、音乐库、歌词缓存、搜索历史等）。</summary>
        public static string DataDirectory => Ensure(Root);

        /// <summary>崩溃日志与启动日志。</summary>
        public static string LogDirectory => Ensure(Path.Combine(Root, "logs"));

        private static string Ensure(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // 目录创建失败时上层调用方各自兜底（日志写入本身有 try/catch）。
            }

            return dir;
        }
    }
}
