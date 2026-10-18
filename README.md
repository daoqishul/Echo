# Echo —— 网易云音乐第三方客户端（WinUI 3 版）
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

仅供学习交流使用，请支持正版音乐。
<img width="1436" height="848" alt="image" src="https://github.com/user-attachments/assets/259e2c5b-2b17-41ed-95d5-3998979b1617" />
<img width="1436" height="848" alt="image" src="https://github.com/user-attachments/assets/d282e5da-12a5-4b92-a0c6-380628203ba1" />
<img width="1436" height="848" alt="image" src="https://github.com/user-attachments/assets/6f22ca12-c498-438f-8274-5b29b2ad6af7" />

