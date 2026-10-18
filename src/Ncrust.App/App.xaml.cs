using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Ncrust.Shell;

namespace Ncrust
{
    /// <summary>应用入口（WinUI 3，非打包运行）。单实例：重复启动会激活已有窗口后退出。</summary>
    sealed partial class App : Application
    {
        private Window _window;
        private Frame _rootFrame;
        private static System.Threading.Mutex _singleInstanceMutex;

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public App()
        {
            Trace("App ctor: enter");

            // 先把处理器挂上：App.xaml 资源解析失败会在 InitializeComponent 里抛出，
            // 那时若还没挂钩就会变成「无日志闪退」。
            UnhandledException += (_, e) => WriteCrashLog(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => WriteCrashLog(e.Exception);

            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                WriteCrashLog(ex);
                throw;
            }

            Trace("App ctor: after InitializeComponent");
        }

        /// <summary>主窗口（WinUI 3 没有 Window.Current，从这里拿）。</summary>
        public static Window MainWindow => ((App)Current)._window;

        /// <summary>
        /// 主 UI 线程的 DispatcherQueue。Core 的回调都在后台线程，界面订阅方用它封送回 UI 线程
        /// （对应 UWP 时代的 CoreDispatcher）。
        /// </summary>
        public static DispatcherQueue UiQueue { get; private set; }

        /// <summary>
        /// 单实例：用命名 Mutex 抢锁。抢到返回 true；已有实例在运行则激活其主窗口并返回 false
        /// （调用方直接结束本次启动）。
        /// </summary>
        private static bool TryAcquireSingleInstance()
        {
            _singleInstanceMutex = new System.Threading.Mutex(true, "Echo.SingleInstance", out var createdNew);
            if (createdNew)
            {
                return true;
            }

            Trace("single instance: another Echo is running");
            try
            {
                var existing = System.Diagnostics.Process.GetProcessesByName("Echo")
                    .FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
                if (existing != null)
                {
                    ShowWindowAsync(existing.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(existing.MainWindowHandle);
                }
            }
            catch
            {
                // 找不到窗口就算了：本次启动直接退出。
            }

            Environment.Exit(0);
            return false;
        }

        internal static void Trace(string message) =>
            WriteLog("boot.log", $"{DateTimeOffset.Now:HH:mm:ss.fff} {message}\r\n");

        /// <summary>数据目录在极早期可能不可用，Environment 与 GetTempPath 兜底。</summary>
        internal static void WriteLog(string fileName, string text)
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ncrust", "logs");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, fileName), text);
            }
            catch
            {
            }

            try
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "ncrust-" + fileName), text);
            }
            catch
            {
            }

            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ncrust-" + fileName), text);
            }
            catch
            {
            }
        }

        internal static void WriteCrashLog(Exception ex)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"--- {DateTimeOffset.Now:O} ---");
                sb.AppendLine($"{ex?.GetType().FullName}: {ex?.Message}");

                var inner = ex?.InnerException;
                for (var i = 0; inner != null && i < 6; i++)
                {
                    sb.AppendLine($"  [inner {i}] {inner.GetType().Name}: {inner.Message}");
                    inner = inner.InnerException;
                }

                sb.AppendLine(ex?.StackTrace);
                sb.AppendLine();

                WriteLog("crash.log", sb.ToString());
            }
            catch
            {
                // 写崩溃日志本身不能再抛，否则可诊断的失败会变成不可诊断的失败。
            }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Trace("OnLaunched: enter");

            // 单实例：已有一个 Echo 在运行时，激活它的主窗口并退出本次启动。
            if (!TryAcquireSingleInstance())
            {
                return;
            }

            UiQueue = DispatcherQueue.GetForCurrentThread();

            _window = new Window { Title = "Echo" };

            var rootFrame = new Frame();
            rootFrame.NavigationFailed += OnNavigationFailed;
            _rootFrame = rootFrame;
            _window.Content = rootFrame;

            // 明暗主题设在根 Frame 上，所有页面继承（设置页可切换：跟随系统 / 深色 / 浅色）。
            AppTheme.ApplySaved(rootFrame);

            // 根 Frame 显式画上当前主题背景：主题切换时顶部标题栏区不再露出系统白底。
            rootFrame.Background = Ncrust.Shell.AppearanceService.CurrentBackgroundBrush();
            AppTheme.Changed += () =>
            {
                rootFrame.Background = Ncrust.Shell.AppearanceService.CurrentBackgroundBrush();
            };

            // 外观：背景材质（纯色 / 亚克力 / 云母）、深色黑度、窗口圆角（Win11）。
            Ncrust.Shell.AppearanceService.Apply(
                _window,
                AppServices.Settings.GetString(Ncrust.Shell.AppearanceService.BackdropKey) ?? "none",
                AppServices.Settings.GetString(Ncrust.Shell.AppearanceService.DarkStyleKey) ?? "black",
                squareCorners: (AppServices.Settings.GetString(Ncrust.Shell.AppearanceService.CornerKey) ?? "round") == "square");

            // 强调色跟随 Windows（读不到时用内置云杉绿），在第一个页面构建前同步好。
            Kanesumi.Xaml.KanesumiAccent.FollowSystem(Resources);

            Trace("OnLaunched: before Navigate");
            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(ShellPage));
            }

            Trace("OnLaunched: after Navigate");
            _window.Activate();
            Trace("OnLaunched: after Activate");

            // 系统托盘：右键「显示主界面 / 播放 / 暂停 / 退出」，双击托盘图标显示主界面。
            Ncrust.Shell.TrayIcon.ShowMainWindow = () =>
            {
                if (_window == null)
                {
                    return;
                }

                _window.AppWindow.Show();
                _window.Activate();
            };
            Ncrust.Shell.TrayIcon.Show();
            Trace("OnLaunched: tray shown");

            // 关闭主窗口默认进托盘（设置 → 托盘 可切回「关闭即退出」）；真正的退出走托盘菜单。
            var appWindow = _window.AppWindow;
            if (appWindow != null)
            {
                appWindow.Closing += (sender, args) =>
                {
                    if (AppServices.Settings.GetBool("close_to_tray", true))
                    {
                        args.Cancel = true;
                        appWindow.Hide();
                        App.Trace("window: closed to tray");
                    }
                };
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            var ex = new Exception($"无法导航到页面 {e.SourcePageType?.FullName}", e.Exception);
            WriteCrashLog(ex);
            throw ex;
        }
    }
}
