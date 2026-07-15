# AIClockBridge for Windows

`mac-app/` 菜单栏桥接的 Windows 移植版：同一套功能、同一套设备协议（固件感知不到
桥接跑在哪个系统上），以系统托盘图标形式常驻。

功能与 Mac 版一致：

- **左键托盘图标** → ESP8266 屏幕实时镜像（账号套餐徽标 + 额度环 + 桌宠动画 + 国产模型
  聚合页 + 系统监控 + 音乐页，与设备渲染同一份数据），底部附
  自动/Claude/Codex/额度总览/国产/系统监控/天气/股票 快速切换
- **右键托盘图标** → 控制菜单按模型额度、设备连接、显示模式、循环展示、内容设置、桌宠与外观、桥接服务分类；
  循环展示可勾选额度总览、Claude、Codex、天气、股票、国产模型、音乐、系统监控等页面，并选择 10/15/30/60 秒间隔。
  启用时如未勾选页面，默认轮播 Codex、Claude、天气、股票；手动切换页面会自动停止循环。
- 本地 HTTP 服务 `0.0.0.0:8765`：`/status`、`/net`、`/music`、`/stock`、`/weather` 及其
  RGB565 中文位图端点、`POST /event`（Claude Code / Codex hooks 秒级状态推送）

套餐徽标只显示供应商凭据或额度接口明确返回的等级；无法识别时隐藏，不根据额度猜测。
额度窗口按返回的实际时长识别，因此供应商临时关闭 5h 限制时会显示 `5h -`，周额度仍正常显示并驱动进度环。
额度请求暂时失败时继续显示最近一次成功结果；该结果会缓存在 `%APPDATA%\AIClockBridge\usage-cache.json`，
因此断网重启 Windows 后也不会立刻变成空白。下次请求成功时自动覆盖缓存并更新屏幕。
- **国产模型聚合页**：当前先识别 Claude Code JSONL 中实际使用的千问（`qwen*`）和
  小米 MiMo（`mimo*`）模型，分别显示当前模型名和今日 token。Token Plan、5h、Weekly
  只有供应商返回可验证的真实值时才显示；当前阿里云百炼和小米 Token Plan 没有面向模型
  API key 的公开额度查询接口，所以显示 `--`，不会拿会话时长伪造百分比。Claude 页只统计
  `claude-*` 模型，不再把通过 Claude Code 客户端调用的千问算成 Claude 用量。
- **国产模型额度授权**：左侧厂商导航列出阿里云百炼、小米 MiMo、智谱、火山方舟、
  月之暗面、MiniMax、DeepSeek、百度千帆、腾讯混元、华为盘古、讯飞星火、阶跃星辰、
  百川和零一万物。千问、小米已接入准确额度捕获；其余厂商提供登录入口并明确标为待接。
- **USB 优先桥接**：小时钟经 CH340 数据线连接时，App 自动识别 COM 口并通过
  460800 串口下发状态、网速、完整音乐画面、天气、股票，以及显示模式/亮度控制；GIF 桌宠上传和镜像
  动画读取同样走 USB。大数据采用 COBS 二进制分块、逐块 CRC/ACK 和整包 CRC；USB 连续
  8 秒无心跳后，固件自动恢复现有 WiFi HTTP 轮询
- 数据来源同 Mac 版：`%USERPROFILE%\.claude\projects` / `%USERPROFILE%\.codex\sessions`
  的 JSONL 日志 + 各自官方用量接口（凭据读
  `%USERPROFILE%\.claude\.credentials.json` 和 `%USERPROFILE%\.codex\auth.json`，
  token 只发给各自官方 API）
- 音乐页读系统级 Now Playing（WinRT `GlobalSystemMediaTransportControlsSessionManager`，
  Spotify / 浏览器 / 本地播放器都能识别）；网速取物理网卡（以太网/WiFi）字节计数，
  4Hz 采样，排除 VPN/虚拟网卡
- 天气页通过 Open-Meteo 获取城市实时天气和空气质量，每 15 分钟刷新；失败时继续使用
  `%APPDATA%\AIClockBridge\weather-cache.json` 的最近成功值。股票页支持 A股/港股/美股，
  优先使用腾讯行情，失败时切换新浪行情；两者都失败则保留最近成功值。默认上证指数
  `sh000001`，最多显示 4 只
- petdex manifest 和 spritesheet 下载会同时尝试系统代理与直连；manifest 成功后缓存到
  `%LOCALAPPDATA%\AIClockBridge\petdex-v1.json`，临时断网时仍可打开上次的桌宠列表

与 Mac 版的差异：

- 无固件刷写入口（刷写请用网页版刷写工具）
- 唯一的第三方依赖是 [ImageSharp](https://github.com/SixLabors/ImageSharp)——
  System.Drawing 解不了 petdex 的 WebP 精灵图、也编不了多帧 GIF

## 构建 / 运行

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows 10
19041+ / Windows 11）：

```powershell
cd windows-app\AIClockBridge
dotnet run                # 前台运行（托盘出现小电脑图标）
# 或发布单文件：
dotnet publish -c Release -r win-x64 --self-contained false
# 产物在 bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\AIClockBridge.exe
```

首次启动 Windows 会弹防火墙授权（HTTP 服务监听 0.0.0.0:8765，设备要从局域网访问，
选"允许"）。

如果小时钟通过 USB 数据线直连电脑，基础状态和控制不再要求两个设备能在局域网中互访。
App 会优先按 CH340 的 VID/PID 筛选 COM 口，再用协议握手确认设备；烧录固件前应先退出
桥接 App，避免 COM 口被占用。

**开机自启**：右键托盘图标，勾选「随 Windows 启动」。App 使用当前用户启动项，
不需要管理员权限；再次点击即可关闭。

**Hooks 实时状态**（可选，同主 README §7）：Claude Code / Codex 的 hooks 往
`http://127.0.0.1:8765/event` POST 事件即可，Windows 下 curl 自带。

## 验证

```powershell
curl.exe -s http://localhost:8765/status | python -m json.tool
```

配置持久化在 `%APPDATA%\AIClockBridge\settings.json`（设备地址等）。

## 代码结构

| 文件 | 对应 Mac 版 | 说明 |
|---|---|---|
| `Program.cs` | `main.swift` | 入口 + 路由表 + 被动发现 |
| `TrayAppContext.cs` | `MenuBarController.swift` | 托盘图标 + 控制菜单 |
| `MirrorForm.cs` | `MirrorPopover.swift` | 240x240 屏幕镜像弹窗 |
| `PetPickerForm.cs` | `PetPickerWindow.swift` | petdex 桌宠选择器 |
| `PetdexService.cs` | `PetdexService.swift` | manifest / 精灵图 / GIF 合成 |
| `StatusService.cs` | `StatusReader.swift` | JSONL 日志扫描 + hook 事件 |
| `UsageFetcher.cs` | `UsageFetcher.swift` | 官方额度接口 |
| `NetSpeedMonitor.cs` | `NetSpeedMonitor.swift` | 4Hz 网速采样环 |
| `NowPlayingMonitor.cs` | `NowPlayingMonitor.swift` | 系统 Now Playing + 封面/文字条 RGB565 |
| `StockMonitor.cs` | — | 自选股行情 + 中文名称 RGB565 |
| `WeatherMonitor.cs` | — | 城市天气/空气质量 + 断网缓存 + 中文标题 RGB565 |
| `DeviceClient.cs` | `DeviceClient.swift` | 设备 HTTP API + 自动配对/子网扫描 |
| `MiniHttpServer.cs` | `HTTPServer.swift` | 0.0.0.0:8765 极简 HTTP 服务 |
| `Rgb565.cs` | （MirrorPopover 内联） | RGB565 大端编解码 |
