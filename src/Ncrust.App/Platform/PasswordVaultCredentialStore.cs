using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Ncrust.Core.Platform;

namespace Ncrust.Platform
{
    /// <summary>
    /// <see cref="ICredentialStore"/> 的 WinUI 3 实现：DPAPI（当前用户）加密后写文件。
    /// 非打包应用没有 PasswordVault，用 ProtectedData 达到同级安全：密文只有本机本用户能解开。
    /// 文件：%LOCALAPPDATA%\Ncrust\session.dat（Base64）。
    /// </summary>
    public sealed class PasswordVaultCredentialStore : ICredentialStore
    {
        private static readonly string PathValue =
            Path.Combine(NcrustPaths.DataDirectory, "session.dat");

        public string GetCookie()
        {
            try
            {
                var base64 = File.ReadAllText(PathValue);
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(base64), null,
                    DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }

        public void SetCookie(string cookie)
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(cookie), null,
                DataProtectionScope.CurrentUser);
            File.WriteAllText(PathValue, Convert.ToBase64String(bytes));
        }

        public void ClearCookie()
        {
            try
            {
                File.Delete(PathValue);
            }
            catch (FileNotFoundException)
            {
                // 本来就没有。
            }
        }
    }
}
