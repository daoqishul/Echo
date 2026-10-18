using System.IO;
using System.Threading.Tasks;
using Ncrust.Core.Platform;

namespace Ncrust.Platform
{
    /// <summary>
    /// <see cref="IFileStore"/> 的 WinUI 3 实现：%LOCALAPPDATA%\Ncrust 下的文本文件。
    /// 队列、音乐库、歌词缓存、搜索历史这些较大的 JSON 都走这里。
    /// </summary>
    public sealed class LocalFileStore : IFileStore
    {
        private static string PathFor(string name) =>
            Path.Combine(NcrustPaths.DataDirectory, name);

        public Task<string> ReadTextAsync(string name)
        {
            try
            {
                return Task.FromResult(File.ReadAllText(PathFor(name)));
            }
            catch (FileNotFoundException)
            {
                return Task.FromResult<string>(null);
            }
            catch (DirectoryNotFoundException)
            {
                return Task.FromResult<string>(null);
            }
        }

        public Task WriteTextAsync(string name, string content)
        {
            var path = PathFor(name);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, true);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name)
        {
            try
            {
                File.Delete(PathFor(name));
            }
            catch (FileNotFoundException)
            {
                // 文件不存在时按成功处理。
            }
            catch (DirectoryNotFoundException)
            {
                // 目录不存在时按成功处理。
            }

            return Task.CompletedTask;
        }
    }
}
