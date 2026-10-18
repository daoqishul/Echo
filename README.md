# Echo —— 网易云音乐第三方客户端（WinUI 3 版）

基于 [GuitaristRin/Ncrust](https://github.com/GuitaristRin/Ncrust) 参考仓库（`windows/` 目录，UWP + WinUI 2）
移植到 **WinUI 3（Windows App SDK 1.8）** 的第三方网易云音乐客户端。协议层（weapi / eapi / xeapi 加密、
二维码登录、音质阶梯、播放队列、歌词、云收藏同步、播放上报）原样复用参考仓库的 `Ncrust.Core`
（netstandard2.0，397 个单元测试全部通过）。

## 功能

- **侧边栏**：搜索框、首页、「**收藏的音乐**」分区（我喜欢的音乐 / 收藏的专辑 / 收藏的歌单）、
  本地音乐、底部账户入口，随窗口宽度自适应（展开 / 紧凑 / 最小三态）。
- **系统托盘**：右键「显示主界面 / 播放 / 暂停 / 退出」，双击显示主界面；关闭主窗口默认进托盘
  （设置可切回直接退出）；托盘退出落盘队列 —— 下次启动自动同步歌曲、**不保留进度**。
- **账号**：WebView2 网页登录 + 扫码登录（eapi PC 身份），登录态 DPAPI 加密落盘；独立账户页
  （侧边栏账户入口）；启动自动签到网易云（weapi/eapi 多通道，可关）。
- **播放**：7 档音质阶梯（压缩 → 杜比全景声），取不到自动逐级降档；Wi-Fi / 计费网络两档偏好；
  5 种播放模式（列表循环 / 单曲 / 随机 / 顺序 / 相似无限）；私人 FM；双语歌词；播放队列管理；
  SMTC 系统媒体键；播放上报（听歌打卡，os=osx eapi 通道）。
- **动画**：Kanesumi.Xaml 移植版 —— 播放器层由单一 `Progress` / `Fullscreen` 标量驱动的
  Composition 表达式动画（迷你栏 → 卡片 → 真全屏的封面形变），展开 400ms standard /
  收起 260ms fastOutSlowIn，无弹簧、无回弹；页面加载用缓存优先 + 交叉淡化。
- **网络**：**默认直连、不走系统代理**（`NcmHttp.UseSystemProxy=false`），避免代理链路
  干扰音乐流与加密接口；「设置 → 网络」可开启系统代理（重启生效）。
- **首页 / 搜索 / 详情 / 设置**：每日推荐、推荐歌单、新歌速递；三类搜索 + 搜索记录；
  专辑 / 歌手 / 歌单详情页；设置页（常规 / 托盘 / 音质 / 播放 / 网络 / 音效 / 外观 / 缓存 / 关于）。
- **主题与外观**：深色 / 浅色 / 跟随系统即时切换；深色黑度（纯黑 / 浅黑）；背景材质
  （纯色 / 亚克力 / 云母，侧边栏与主界面同步）；Win11 窗口圆角 / 直角（Win10 隐藏）；强调色跟随 Windows。
- **唱片机**：网易云同款黑胶旋转 + 双语歌词界面（Composition 驱动，播放转 / 暂停停）。
- **下载与本地音乐**：右键歌曲 → 下载（音质选择 + 路径选择，https 直连）；本地音乐页自动
  扫描文件夹音频（TagLib 标签识别），负 id 直连本地文件播放。

## 构建（无需 Visual Studio）

需要：Windows 10 1809+ x64、`.NET 8 SDK`。所有依赖（Windows App SDK、WebView2、QRCoder）均来自 NuGet，
构建自带 Windows App SDK 运行时（自包含），目标机无需安装任何运行时。

```powershell
# 安装 .NET 8 SDK（用户级，无需管理员）后：
dotnet restore Ncrust.WinUI3.sln
dotnet build  Ncrust.WinUI3.sln -c Release

# 运行：
.\src\Ncrust.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Echo.exe

# Core 单元测试（397 个）：
dotnet test tests\Ncrust.Core.Tests -c Release
```

产物为**非打包（unpackaged）** 单目录应用，整个输出目录拷走即可分发。
数据与设置存 `%LOCALAPPDATA%\Ncrust\`（`settings.json`、`session.dat`（DPAPI 加密登录态）、
`playback_state.json`、音乐库、歌词缓存）；崩溃日志在 `%LOCALAPPDATA%\Ncrust\logs\crash.log`。

## 从 UWP / WinUI 2 移植的主要改动

| 参考仓库（UWP） | 本仓库（WinUI 3） |
|---|---|
| `Windows.UI.Xaml.*` | `Microsoft.UI.Xaml.*`（脚本化重命名 + 手工适配） |
| `CoreDispatcher` | `DispatcherQueue`（`App.UiQueue`） |
| `ApplicationData.LocalSettings / LocalFolder` | `%LOCALAPPDATA%\Ncrust\settings.json` + 文件存储 |
| `PasswordVault` | DPAPI（`ProtectedData`，当前用户）加密文件 |
| `CoreApplicationViewTitleBar` | `AppWindow.TitleBar` + `Window.SetTitleBar` |
| `ApplicationView.TryEnterFullScreenMode` | `AppWindow.SetPresenter(FullScreen)` |
| `Package.Current.Id.Version` | 程序集版本 |
| `MediaBinder + SetUri` 延迟取链 | `MediaSource.CreateFromUri`（惰性打开语义相同） |
| `MediaPlaybackList` 滑动窗口 | 单条目播放（MediaEnded 手动推进，见下） |
| `Ncrust.Audio`（UWP 运行时组件） | CsWinRT 组件 + SxS `activatableClass` 免注册激活 |
| `ControlsResourcesVersion=Version1` | WASDK 1.8 已移除该开关，由 Kanesumi 资源覆盖压回直角 |

## 已知事项（本机实测）

- **播放 URL 统一改写 `http://` → `https://`**：本机 Media Foundation 的网络源打不开
  网易 CDN 的明文 http 流（`SourceNotSupportedError` / 卡 Opening），https 稳定播放完整歌曲
  （网易 CDN 的 `cdntag=os_web` 本就是 web 身份，签名 URL 两侧通用）。
- **MediaPlaybackList 的网络源在本机不稳定**：故引擎改为单条目播放
  （`MediaPlayer.Source` 直挂 + `MediaEnded` 手动推进），切歌有短暂间隙；
  「无缝播放」设置项保留但当前不生效（Android 端默认也是关闭的实验特性）。
- **均衡器（CsWinRT 音效组件）默认禁用**：`IBasicAudioEffect` 在个别系统的 Media Foundation
  上处理音频帧时会原生崩溃（无托管日志）。设置页会提示「当前系统不支持挂载音效」，
  音乐照常播放。恢复启用见 `PlaybackHost.Create()` 中的 `DisableEqualizer` 开关。
- 登录层打开时会清除 WebView2 的残留 cookie 再导航，避免「一打开就自动登录」。
- 版本检查 / 更新走 GitHub Releases（`win-vX.Y.Z` 资产命名），当前版本取程序集版本。

## 目录结构

```
ncrust-winui3/
├── Ncrust.WinUI3.sln
├── spec/                     # 跨平台协议规格与测试夹具（复制自参考仓库，测试直接引用）
├── src/
│   ├── Ncrust.Core/          # netstandard2.0：协议 / 加密 / 队列 / 歌词 / 云收藏（原样复用）
│   ├── Kanesumi.Xaml/        # token / 缓动 / 控件 / NavigationView 覆盖（WinUI 3 移植）
│   ├── Ncrust.Audio/         # CsWinRT 均衡器音效组件（可选，默认禁用）
│   └── Ncrust.App/           # WinUI 3 应用：外壳 / 页面 / 播放器 / 登录 / 平台实现
├── tests/Ncrust.Core.Tests/  # 397 个 Core 单元测试（net8.0）
└── tools/                    # 开发工具：UIA 验证脚本、接口探针、截图脚本
```

## 致谢

界面、协议与交互规格来自 [GuitaristRin/Ncrust](https://github.com/GuitaristRin/Ncrust)（含 `spec/` 与
`windows/` 目录）。本仓库是其 Windows 端的 WinUI 3 移植，仅供学习交流使用，请支持正版音乐。
