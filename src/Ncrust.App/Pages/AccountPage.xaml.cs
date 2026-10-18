using System;
using System.Globalization;
using Ncrust.Core.Api;
using Ncrust.Login;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace Ncrust.Pages
{
    /// <summary>独立账户页：头像 / 昵称 / UID / 会员状态，登录与退出登录。</summary>
    public sealed partial class AccountPage : Page
    {
        public AccountPage()
        {
            InitializeComponent();
            AppServices.SessionChanged += UpdateAccount;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            UpdateAccount();
            AutoSigninSwitch.IsOn = AppServices.Settings.GetBool("auto_signin", true);
        }

        // ── 签到 ──────────────────────────────────────────────────────────────

        private void AutoSigninToggled(object sender, RoutedEventArgs e) =>
            AppServices.Settings.SetBool("auto_signin", AutoSigninSwitch.IsOn);

        private async void SigninNowClick(object sender, RoutedEventArgs e)
        {
            SigninNowButton.IsEnabled = false;
            SigninStatus.Text = "正在签到…";
            try
            {
                var result = await new Core.Api.SignInApi(AppServices.Http).DailyAsync();
                if (result.Ok)
                {
                    SigninStatus.Text = "签到成功：" + result.Message;
                }
                else if (result.AlreadyDone)
                {
                    SigninStatus.Text = result.Message;
                }
                else
                {
                    SigninStatus.Text = result.Message;
                }
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
                SigninStatus.Text = "签到失败";
            }
            finally
            {
                SigninNowButton.IsEnabled = true;
            }
        }

        private void UpdateAccount()
        {
            var loggedIn = AppServices.IsLoggedIn();
            AccountLoggedIn.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
            AccountLoggedOut.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;

            var profile = AppServices.Cache.UserProfile;
            NicknameText.Text = profile?.Nickname ?? "我的";
            UidText.Text = profile != null && profile.UserId > 0
                ? "UID: " + profile.UserId.ToString(CultureInfo.InvariantCulture)
                : string.Empty;

            // 会员状态只在会话有效时有意义；接口失败显示「未知」，绝不显示成「未开通」。
            var vip = AppServices.AccountSession.State is Core.Account.SessionState.Valid valid ? valid.Vip : null;
            VipStatusText.Text = vip == null ? string.Empty : Ncrust.Resources.DisplayFormat.VipLabel(vip);

            var avatar = CoverUrls.WithSize(profile?.AvatarUrl, 128);
            AvatarImage.Source = Ncrust.Resources.DisplayFormat.CoverRaw(avatar, 128);
        }

        private void LoginClick(object sender, RoutedEventArgs e) => LoginLauncher.Request();

        private async void LogoutClick(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "退出登录",
                Content = "退出后本机的每日推荐与云端同步将不可用，播放队列与已缓存的音乐库保留。",
                PrimaryButtonText = "退出登录",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = App.MainWindow.Content.XamlRoot,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                AppServices.SignOut();
            }
        }
    }
}
