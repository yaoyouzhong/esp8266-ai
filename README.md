<p align="center">
  <img src="docs/images/logo.svg" width="72" alt="logo">
</p>

<h1 align="center">AI Mac 小屏幕</h1>

<p align="center">桌上的一台 AI 状态小电脑 —— ESP8266 · 开源硬件 · 桌面伴侣</p>

<p align="center">
  中文 ·
  <a href="README.en.md">English</a>
</p>

<p align="center">
  <a href="https://mac.qust.me">官网</a> ·
  <a href="https://mac.qust.me/#flash">网页刷机</a> ·
  <a href="https://github.com/pengchujin/esp8266-ai/releases/latest">下载</a>
</p>

<p align="center">
  <img src="docs/images/hero.jpg" width="640" alt="AI Mac 小屏幕">
</p>

一块 240×240 的复古小电视，放在桌上实时显示 **Claude Code / Codex CLI 在干什么、额度还剩多少**。不需要任何 API key：桥接程序读取本机已有的 CLI 登录凭据和会话日志，Windows 优先通过 USB 直连设备，macOS 及 Windows 无线回退通过局域网连接。

## 功能

| | |
|---|---|
| <img src="docs/images/feature1.jpg" width="360" alt="AI 工作状态"> | **AI 工作状态与额度**<br>桌宠动起来 = AI 正在干活。Claude/Codex 显示供应商返回的真实额度；国产模型页聚合千问和小米 MiMo，显示本机会话里识别到的模型名和今日 token。阿里云百炼授权后可读取并缓存准确的千问 Token Plan 百分比；未知额度显示 `--`，不做估算。 |
| <img src="docs/images/feature2.jpg" width="360" alt="系统监控"> | **系统实时监控**<br>任务管理器风格的上下行曲线，56 秒滚动窗口，量程自动调整，并同步显示 Windows CPU 与内存占用。 |
| <img src="docs/images/music.jpg" width="360" alt="音乐播放"> | **音乐播放显示**<br>专辑封面、歌名、歌手、进度条实时同步；音乐响起自动切入，停止自动切回。 |
| | **天气时钟与股票行情**<br>天气页采用大号时分秒、城市/天气/空气质量、温度和湿度布局；股票页最多显示 4 只 A股/港股/美股，按国内习惯涨红跌绿。两页均由 Windows 桥接获取数据、失败时保留最近成功值，并支持 USB 直推。 |
| <img src="docs/images/feature3.jpg" width="360" alt="桌宠可换"> | **可换桌宠**<br>内置 [petdex.dev](https://petdex.dev) 画廊 3300+ 开源桌宠，也可上传任意 GIF，设备板上直接解码，无需重烧固件。 |

## 快速上手

需要的东西：一台「SD2 小电视」开发板（[开源硬件](https://oshwhub.com/q21182889/sd2)，也可[直接购买成品](https://mobile.yangkeduo.com/goods.html?ps=OuBjGMWE82)）、一根 USB **数据**线。

### 第 1 步 · 刷固件（约 30 秒）

用 Chrome / Edge 打开 **[mac.qust.me/#flash](https://mac.qust.me/#flash)**，USB 连接设备，点「连接设备并烧录」，选择串口等待完成即可，无需安装任何工具。

> 弹窗里看不到串口？Windows 需要装 [CH340 驱动](https://www.wch.cn/downloads/CH341SER_EXE.html)，Mac 系统自带无需安装；换根 USB 线（很多线只能充电）；更多排查见[官网 FAQ](https://mac.qust.me/#flash-faq)。
>
> 命令行党也可以用 esptool 把 [Releases](https://github.com/pengchujin/esp8266-ai/releases/latest) 里的 `esp8266-ai-firmware-*.bin` 刷到 `0x0`。

### 第 2 步 · 配 WiFi

WiFi 用于无线回退和网页管理；Windows 通过 USB 使用时可跳过。设备连续 15 秒既没有
USB 桥接也没有连上 WiFi，才会开启热点 **`AI-Clock-Setup`**：手机连上后自动弹出配网页
（没弹就用浏览器打开 `192.168.4.1`），选择家里 WiFi、输入密码，完成。

### 第 3 步 · 装桥接程序

从 [Releases](https://github.com/pengchujin/esp8266-ai/releases/latest) 下载并打开：

- **macOS**：`AIClockBridge-*-macOS.dmg`，拖入 Applications（ad-hoc 签名，首次启动需在「系统设置 → 隐私与安全性」允许，并同意本地网络权限）
- **Windows**：`AIClockBridge-*-Windows-x64.exe`，双击即用

桥接程序常驻菜单栏 / 托盘。Windows 连接 USB 数据线后直接通过 COM 握手，不要求电脑和设备在局域网互通；macOS 和 Windows 的无线回退会自动发现并配对同一局域网内的设备。

<p align="center">
  <img src="docs/images/working.jpg" width="640" alt="工作演示">
</p>

日常使用都在托盘图标上：**左键**打开设备画面的实时镜像（底部有屏幕亮度滑条），**右键**按「模型额度、设备连接、显示模式、循环展示、内容设置、桌宠与外观、桥接服务」分类。循环展示可勾选要轮播的页面和 10/15/30/60 秒间隔；手动切换页面会自动停止循环。
显示模式中的「额度总览」会把 Claude 5h/Weekly 与 Codex 当前有效额度放在同一页，并显示重置倒计时。

## 常见问题

- **屏幕边框红色闪烁**：设备连不上桥接程序——Windows 优先确认 USB 数据线和桥接程序，
  macOS 或无线回退再确认电脑与设备位于可互通的局域网。
- **Windows 下同一 WiFi 仍无法互通**：保持 USB 数据线连接并运行最新版 Windows 桥接程序，
  程序会自动通过 COM 串口直连；状态、网速、音乐（含封面和中文）、天气、股票、显示控制、桌宠上传和
  镜像动画都不再依赖局域网互访。USB 断开后自动回退原有 WiFi HTTP 通道。
- **额度一直显示 `-` / `--`**：代表供应商没有返回可验证的额度数据；不是连接故障。
  千问 Token Plan 需要在 Windows 托盘的「模型额度 → 国产模型额度授权」登录一次；登录状态持久化 30 天，成功读取时自动续期。国产模型的模型名和今日 token 来自本机 Claude Code 会话日志，只覆盖写入这些日志的调用，并不等于阿里账号下所有应用的总消耗。Claude 页只统计真正的 `claude-*` 模型，不统计通过 Claude Code 客户端调用的千问或 MiMo。
- **想换桌宠**：右键托盘图标 → 「更换桌宠动画…」，挑一个点上传就行。

## 开发

```
firmware/     ESP8266 固件（PlatformIO + Arduino，含板上 GIF 解码）
mac-app/      macOS 菜单栏桥接（Swift/SPM，零第三方依赖）
windows-app/  Windows 托盘桥接（C# / .NET 8 WinForms）
tools/        GIF → RGB565 内置精灵图转换脚本
docs/         开发文档（硬件引脚、HTTP API、架构细节）
```

```bash
cd firmware && pio run -t upload   # 固件：编译 + USB 烧录
cd mac-app && swift run            # Mac 桥接：本地跑起来
```

硬件引脚表、屏幕驱动的坑、设备 HTTP API、GIF 板上解码架构等细节见 **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)**。

硬件、固件、软件全部开源，拿去改、拿去做、拿去卖都行。
