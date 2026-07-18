# AIClockBridge for Windows

`mac-app/` 菜单栏桥接的 Windows 移植与扩展版：复用同一套基础设备协议，并增加 USB
直连、天气、股票、国产模型额度和 CPU/内存监控，以系统托盘图标形式常驻。

Windows 版功能：

- **左键托盘图标** → ESP8266 屏幕实时镜像（账号套餐徽标 + 额度环 + 桌宠动画 + 国产模型
  聚合页 + 系统监控 + 音乐页，与设备渲染同一份数据），底部附
  自动/Claude/Codex/双额度/国产/系统监控/天气/股票 快速切换
- **右键托盘图标** → 控制菜单按模型额度、设备连接、显示模式、循环展示、内容设置、桌宠与外观、桥接服务分类；
  循环展示可勾选 Claude + Codex 额度、Claude、Codex、天气、股票、国产模型、音乐、系统监控等页面，并选择 10/15/30/60 秒间隔。
  启用时如未勾选页面，默认轮播 Codex、Claude、天气、股票；手动切换页面会自动停止循环。
- **自动屏保**：在「显示模式 → 屏保设置」选择关闭或 1/5/10/30/60 分钟，也可立即预览。
  计时依据 Windows 真实键鼠空闲时间；键鼠恢复会退出并恢复原页面，AI 工作或审批提醒会
  临时覆盖屏保显示对应模型，事件结束后若用户仍未返回则继续屏保。AUTO 模式播放音乐会
  恢复正常 AUTO 页面。循环展示在屏保期间暂停，退出后继续；立即预览使用强制预览模式，
  不会被当前正在工作的桌宠或键鼠输入打断，固定展示 5 秒后自动恢复原页面。屏保以七段液晶数字显示时间，每
  5 秒移动刷新一次，下方显示日期和中文星期。
- **Codex 动作提醒**：`PermissionRequest` 会从任意固定页面切到 Codex，并以整圈红色边框闪烁；
  桥接同时识别 Codex Desktop 会话 JSONL 的 `task_complete` 和 Hook 的 `Stop`，触发四边
  播放一次 Windows 系统提示音，同时显示完整绿框的 5 次平滑脉冲和桌宠动画；多个完成事件按最新完成序号重新触发，随后恢复真实额度进度环和原固定页面，不要求修改 Codex Desktop 的
  `notify` 配置。
- 本地 HTTP 服务 `0.0.0.0:8765`：`/status`、`/net`、`/music`、`/stock`、`/weather` 及其
  RGB565 中文位图端点、`POST /event`（Claude Code / Codex hooks 秒级状态推送）

套餐徽标只显示供应商凭据或额度接口明确返回的等级；无法识别时隐藏，不根据额度猜测。
额度窗口按返回的实际时长识别，因此供应商临时关闭 5h 限制时会显示 `5h -`，周额度仍正常显示并驱动进度环。
额度请求暂时失败时继续显示最近一次成功结果；该结果会缓存在 `%APPDATA%\AIClockBridge\usage-cache.json`，
因此断网重启 Windows 后也不会立刻变成空白。下次请求成功时自动覆盖缓存并更新屏幕。
- **国产模型显示页**：在「显示模式 → 国产模型」子菜单中单选厂商；识别 Claude Code JSONL 中实际使用的千问（`qwen*`）和小米 MiMo
  （`mimo*`）模型，显示最近模型名和本机日志中的今日 token。这个数字只覆盖写入 Claude
  Code 会话日志的调用，不等于同一个 Token Plan 被所有应用消耗的总量。Token Plan、5h、
  Weekly 只有供应商返回可验证的真实值时才显示，不拿会话时长伪造百分比。Claude 页只统计
  `claude-*` 模型，不把通过 Claude Code 客户端调用的千问算成 Claude 用量。
- **国产模型额度授权**：左侧厂商导航列出阿里云百炼、月之暗面、小米 MiMo、智谱、火山方舟、
  月之暗面、MiniMax、DeepSeek、百度千帆、腾讯混元、华为盘古、讯飞星火、阶跃星辰、
  百川和零一万物。阿里云百炼与月之暗面已接通准确额度捕获：前者显示 Token Plan，并把团队版等订阅版本显示为金色徽标；后者显示
  Kimi Coding Plan 会员权益及 Weekly/5h，并与 Codex 一样在启动时立即读取、随后每 2 分钟后台刷新；自动或手动请求至少间隔 60 秒，供应商限流时退避 5 分钟。小米已预留响应规则但尚未用真实订阅账号验证。其余厂商提供
  登录入口并明确标为待接。打开授权页时会直接进入当前单选厂商。阿里和 Kimi 登录状态保留 30 天，
  每次成功读取自动续期；供应商主动撤销会话后需要重新登录。
- **USB 优先桥接**：小时钟经 CH340 数据线连接时，App 自动识别 COM 口并通过
  460800 串口下发状态、网速、完整音乐画面、天气、股票，以及显示模式/亮度控制；GIF 桌宠上传和镜像
  动画读取同样走 USB。大数据采用 COBS 二进制分块、逐块 CRC/ACK 和整包 CRC；审批/完成提醒
  使用带 ACK/重试的紧凑控制帧，避免被大数据传输阻塞；USB 连续
  8 秒无心跳后，固件自动恢复现有 WiFi HTTP 轮询
- 数据来源同 Mac 版：`%USERPROFILE%\.claude\projects` / `%USERPROFILE%\.codex\sessions`
  的 JSONL 日志 + 各自官方用量接口（凭据读
  `%USERPROFILE%\.claude\.credentials.json` 和 `%USERPROFILE%\.codex\auth.json`，
  token 只发给各自官方 API）
- 音乐页读系统级 Now Playing（WinRT `GlobalSystemMediaTransportControlsSessionManager`，
  Spotify / 浏览器 / 本地播放器都能识别）；网速取物理网卡（以太网/WiFi）字节计数，
  4Hz 采样，排除 VPN/虚拟网卡
- 天气页每 15 分钟刷新。配置和风天气 API Host 与 API KEY 后，实时天气和当日高低温优先
  使用和风天气，空气质量接口不可用时单独回退 Open-Meteo；和风主请求失败时整页回退
  Open-Meteo。地区支持手动输入到区县，或经用户授权读取一次 Windows 定位坐标；全部网络
  请求失败时继续使用 `%APPDATA%\AIClockBridge\weather-cache.json` 的最近成功值。API KEY
  存在 Windows 凭据管理器目标 `AIClockBridge/QWeatherApiKey`，不进入设置文件、缓存或日志。
  股票页支持 A股/港股/美股，
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

首次启动 Windows 会弹防火墙授权。USB 直连不依赖这项权限；需要使用无线回退时，HTTP
服务监听 `0.0.0.0:8765`，应选“允许”让设备从局域网访问。

如果小时钟通过 USB 数据线直连电脑，基础状态和控制不再要求两个设备能在局域网中互访。
App 会优先按 CH340 的 VID/PID 筛选 COM 口，再用协议握手确认设备；烧录固件前应先退出
桥接 App，避免 COM 口被占用。

**开机自启**：右键托盘图标，勾选「随 Windows 启动」。App 使用当前用户启动项，
不需要管理员权限；再次点击即可关闭。

**Hooks 实时状态**（可选，同主 README §7）：Claude Code / Codex 的 hooks 往
`http://127.0.0.1:8765/event` POST 事件即可，Windows 下 curl 自带。

## 验证

```powershell
dotnet build windows-app\AIClockBridge\AIClockBridge.csproj -c Release
curl.exe -s http://localhost:8765/status | python -m json.tool
# 设备已连接时，覆盖状态、天气、股票、音乐、GIF 和页面缓存：
windows-app\AIClockBridge\bin\Release\net8.0-windows10.0.19041.0\AIClockBridge.exe --test-usb
```

主要运行数据：

| 路径 | 内容 |
|---|---|
| `%APPDATA%\AIClockBridge\settings.json` | 设备地址、串口和显示/循环/屏保/天气非敏感设置；不含 API KEY |
| `%APPDATA%\AIClockBridge\usage-cache.json` | Claude/Codex 最近一次成功额度，不含凭据 |
| `%APPDATA%\AIClockBridge\domestic-quota-cache.json` | 国产模型最近一次准确额度，不含 Cookie |
| `%APPDATA%\AIClockBridge\weather-cache.json` | 最近一次成功天气 |
| `%APPDATA%\AIClockBridge\quota-auth-profile` | 国产模型授权专用 WebView2 profile |
| `%LOCALAPPDATA%\AIClockBridge\petdex-v1.json` | petdex manifest 缓存 |

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
| `DomesticQuotaService.cs` | — | 国产厂商目录、控制台额度捕获和登录保持 |
| `SerialBridge.cs` | — | CH340 自动识别、JSON 控制帧和 COBS 二进制传输 |
| `StartupManager.cs` | — | 当前用户 Windows 自启动 |
| `NetSpeedMonitor.cs` | `NetSpeedMonitor.swift` | 4Hz 网速采样环 |
| `SystemStatsMonitor.cs` | — | Windows CPU 与物理内存占用 |
| `NowPlayingMonitor.cs` | `NowPlayingMonitor.swift` | 系统 Now Playing + 封面/文字条 RGB565 |
| `StockMonitor.cs` | — | 自选股行情 + 中文名称 RGB565 |
| `WeatherMonitor.cs` | — | 城市天气/空气质量 + 断网缓存 + 中文标题 RGB565 |
| `WeatherSettingsForm.cs` | — | 和风数据源、手动区县、Windows 自动定位和连接测试 |
| `CredentialStore.cs` | — | Windows 凭据管理器读写，不把供应商密钥落入 JSON |
| `DeviceClient.cs` | `DeviceClient.swift` | 设备 HTTP API + 自动配对/子网扫描 |
| `MiniHttpServer.cs` | `HTTPServer.swift` | 0.0.0.0:8765 极简 HTTP 服务 |
| `Rgb565.cs` | （MirrorPopover 内联） | RGB565 大端编解码 |
