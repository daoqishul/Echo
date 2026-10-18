using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Ncrust.Shell
{
    /// <summary>
    /// 系统托盘图标（Echo），纯 Win32 实现（Shell_NotifyIcon + 消息窗口 + 原生右键菜单）。
    /// 右键菜单：「显示主界面」「播放 / 暂停」「退出」；双击托盘图标显示主界面。
    /// 主窗口关闭默认进托盘（设置可切回直接退出）；托盘「退出」会先落盘队列再退出：
    /// 下次启动自动同步上次的歌曲队列、**不保留进度**（永远从 0 开始）。
    /// </summary>
    internal static class TrayIcon
    {
        private const int WM_USER = 0x0400;
        private const int WM_TRAYMSG = WM_USER + 0x100;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_COMMAND = 0x0111;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIM_SETVERSION = 0x00000004;
        // 版本 3（不是 4）：版本 4 会让 shell 改发 NIN_SELECT/NIN_KEYSELECT 事件，
        // 不再发送 WM_RBUTTONUP / WM_LBUTTONDBLCLK —— 右键菜单与双击都会失灵。
        // 幽灵多图标是旧版截断结构体造成的，完整结构体（976 字节）+ 版本 3 即可两全。
        private const uint NOTIFYICON_VERSION = 3;
        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;

        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_NONOTIFY = 0x0080;
        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;

        private const uint CmdShowMain = 3;
        private const uint CmdPlayPause = 1;
        private const uint CmdExit = 2;

        private static IntPtr _hwnd;
        private static IntPtr _icon;
        private static bool _created;
        private static bool _playing;
        private static WndProcDelegate _wndProc;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion; // 联合体 uTimeout / uVersion
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll")]
        private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        public static void Show()
        {
            if (_created)
            {
                return;
            }

            try
            {
                var className = "EchoTrayWindow";
                _wndProc = WndProc;

                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = GetModuleHandle(null),
                    lpszClassName = className,
                };
                var cls = RegisterClassEx(ref wc);
                App.Trace($"tray: RegisterClassEx={cls} err={Marshal.GetLastWin32Error()}");

                _hwnd = CreateWindowEx(0, className, "EchoTray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                App.Trace($"tray: CreateWindowEx={_hwnd} err={Marshal.GetLastWin32Error()}");
                if (_hwnd == IntPtr.Zero)
                {
                    return;
                }

                _icon = LoadIconHandle();
                var data = new NOTIFYICONDATAW
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONDATAW)),
                    hWnd = _hwnd,
                    uID = 1,
                    uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                    uCallbackMessage = WM_TRAYMSG,
                    hIcon = _icon,
                    szTip = "Echo",
                };
                var added = Shell_NotifyIcon(NIM_ADD, ref data);
                App.Trace($"tray: Shell_NotifyIcon(ADD)={added} icon={_icon} size={data.cbSize}");
                // 完整结构体（不再有幽灵多图标）+ 版本 3（保留右键 / 双击鼠标消息）。
                data.uVersion = NOTIFYICON_VERSION;
                var version = Shell_NotifyIcon(NIM_SETVERSION, ref data);
                App.Trace($"tray: Shell_NotifyIcon(SETVERSION={NOTIFYICON_VERSION})={version}");
                _created = true;
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
        }

        /// <summary>按当前播放状态更新菜单文案（播放中显示「暂停」，否则「播放」）。</summary>
        public static void UpdatePlayPauseLabel(bool playing) => _playing = playing;

        /// <summary>显示主窗口（托盘菜单 / 双击托盘图标；关闭进托盘后从这里回来）。</summary>
        public static Action ShowMainWindow { get; set; }

        public static void DisposeIcon()
        {
            if (!_created)
            {
                return;
            }

            try
            {
                var data = new NOTIFYICONDATAW
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONDATAW)),
                    hWnd = _hwnd,
                    uID = 1,
                };
                Shell_NotifyIcon(NIM_DELETE, ref data);
                DestroyWindow(_hwnd);
            }
            catch
            {
            }
            finally
            {
                _created = false;
                _hwnd = IntPtr.Zero;
            }
        }

        private static void Exit()
        {
            try
            {
                // 退出前把队列落盘（进度不落盘）：下次进入自动同步歌曲、从头播放。
                AppServices.Session.SaveAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }

            DisposeIcon();
            Application.Current.Exit();
        }

        private static void ShowMain()
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            try
            {
                var appWindow = window.AppWindow;
                appWindow.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
        }

        private static void HandleCommand(uint command)
        {
            switch (command)
            {
                case CmdShowMain:
                    App.UiQueue?.TryEnqueue(ShowMain);
                    break;
                case CmdPlayPause:
                    App.UiQueue?.TryEnqueue(() => Ncrust.Playback.PlaybackHost.Engine.PlayPause());
                    break;
                case CmdExit:
                    App.UiQueue?.TryEnqueue(Exit);
                    break;
            }
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TRAYMSG && lParam == (IntPtr)WM_RBUTTONUP)
            {
                ShowMenu();
                return IntPtr.Zero;
            }

            if (msg == WM_TRAYMSG && lParam == (IntPtr)WM_LBUTTONDBLCLK)
            {
                HandleCommand(CmdShowMain);
                return IntPtr.Zero;
            }

            if (msg == WM_COMMAND)
            {
                HandleCommand((uint)wParam.ToInt64());
                return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private static void ShowMenu()
        {
            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return;
            }

            AppendMenu(menu, MF_STRING, (UIntPtr)CmdShowMain, "显示主界面");
            AppendMenu(menu, MF_STRING, (UIntPtr)CmdPlayPause, _playing ? "暂停" : "播放");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)CmdExit, "退出");

            GetCursorPos(out var cursor);
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, cursor.X, cursor.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            DestroyMenu(menu);

            if (command != 0)
            {
                HandleCommand(command);
            }
        }

        private static IntPtr LoadIconHandle()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
                if (File.Exists(path))
                {
                    var handle = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                    if (handle != IntPtr.Zero)
                    {
                        return handle;
                    }
                }
            }
            catch
            {
                // 图标加载失败：继续用空图标（托盘仍工作）。
            }

            return IntPtr.Zero;
        }
    }
}
