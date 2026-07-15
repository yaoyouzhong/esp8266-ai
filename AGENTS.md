# Project rules

本文件补充全局 `~/.codex/AGENTS.md`，只记录本仓库的工程约定。默认使用中文沟通，代码、协议字段和命令保持英文。

## Scope and sources of truth

- `firmware/`：ESP8266/ESP-12S 固件，PlatformIO + Arduino，设备端渲染、HTTP 管理页、USB 协议和 Wi-Fi 回退。
- `windows-app/AIClockBridge/`：Windows 10/11 托盘桥接，当前功能最完整；USB、天气、股票、国产模型额度和设备镜像以这里为准。
- `mac-app/`：macOS 菜单栏桥接，保留原有 LAN 路径；不要假设它自动拥有 Windows 新增能力。
- `README.md` / `README.en.md`：普通用户入口，两种语言的功能与安装说明必须同步。
- `docs/DEVELOPMENT.md`：硬件、协议、设备 HTTP API 和架构说明。
- `windows-app/README.md`：Windows 构建、运行、数据源、缓存和排障说明。

## Transport invariants

- Windows 与设备优先走 CH340 串口，当前稳定速率是 `460800`。
- 小控制帧使用 `@AICLOCK ` + 单行 JSON，协议 `version=1`。
- 图片、GIF 和精灵图使用 `NUL + COBS + NUL` 分块传输，包含传输 ID、序号、长度、逐块 ACK/CRC32 和整包 CRC32。
- USB 心跳失效约 8 秒后，设备必须恢复 Wi-Fi HTTP 轮询；修改 USB 路径时不得破坏无线回退。
- 串口协议、显示模式或二进制类型变更必须同时修改 Windows、固件和相应文档。

## Runtime state and secrets

- 用户设置和缓存位于 `%APPDATA%\AIClockBridge`，petdex 缓存位于 `%LOCALAPPDATA%\AIClockBridge`；这些运行数据不得提交。
- 阿里云授权使用独立 WebView2 profile，Cookie 不进入源码、日志或额度 JSON 缓存。
- `usage-cache.json`、`domestic-quota-cache.json` 和 `weather-cache.json` 只能保存可显示状态，不得保存 OAuth token、API key、密码或 Cookie 值。
- 实机 COM 号会变化；先自动识别 CH340，再以握手结果为准，不要把 `COM7` 当成协议常量。

## Required validation

Windows 代码变更：

```powershell
dotnet build windows-app\AIClockBridge\AIClockBridge.csproj -c Release
curl.exe -s http://127.0.0.1:8765/status
```

如果 Release EXE 正在运行并锁住输出文件，先正常退出桥接 App，再编译并重新启动。纯协议、媒体或显示改动在设备已连接时还要运行：

```powershell
windows-app\AIClockBridge\bin\Release\net8.0-windows10.0.19041.0\AIClockBridge.exe --test-usb
```

固件变更：

```powershell
python -m platformio run -d firmware
python -m platformio run -d firmware -t upload --upload-port COMx
```

上传前确认桥接 App 已释放串口；刷写后检查 USB 握手、`/status`、当前显示模式和 Wi-Fi 回退。不要把“编译通过”冒充“实机通过”。

## Change discipline

- 先读调用方和协议对端，再修改共享 JSON、串口帧、位图尺寸或缓存格式。
- 天气/股票中文位图和设备端 LittleFS 页面缓存是配套设计；调整尺寸、坐标或压缩格式时必须两端同步。
- 国产模型 `tokens_today` 当前来自本机 Claude Code JSONL，只覆盖写入这些日志的调用；不得描述成阿里账号全量消耗。Token Plan 百分比来自厂商页面授权，是另一条数据源。
- 网络或供应商接口失败时保留最近一次成功状态，不能把可用界面清空成错误页。
- 只修改当前任务需要的文件；提交前运行 `git diff --check` 并检查敏感信息和构建产物。
