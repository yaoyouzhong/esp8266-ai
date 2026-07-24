<p align="center">
  <img src="docs/images/logo.svg" width="72" alt="logo">
</p>

<h1 align="center">AI Mac Mini Display</h1>

<p align="center">A tiny AI status computer for your desk — ESP8266 · Open Source Hardware · Desktop Companion</p>

<p align="center">
  <a href="README.md">中文</a> ·
  English
</p>

<p align="center">
  <a href="https://mac.qust.me">Website</a> ·
  <a href="https://mac.qust.me/#flash">Web Flasher</a> ·
  <a href="https://github.com/pengchujin/esp8266-ai/releases/latest">Download</a>
</p>

<p align="center">
  <img src="docs/images/hero.jpg" width="640" alt="AI Mac Mini Display">
</p>

A retro mini-TV with a 240×240 screen that shows **what Claude Code / Codex CLI are doing right now and how much quota you have left**. No API key is required. The bridge reads existing CLI credentials and local session logs, then serves the device over direct USB on Windows or over the LAN fallback on Windows/macOS.

## Features

| | |
|---|---|
| <img src="docs/images/feature1.jpg" width="360" alt="AI status"> | **AI status & quota**<br>Pet is walking = the AI is working. Claude/Codex show each provider-reported 5H/WK usage window with its reset countdown. The domestic-model submenu selects exactly one vendor. Alibaba Bailian Token Plan shows total usage and its fixed reset time; a Coding Plan shows 5H/WK only when those windows are actually returned. Moonshot shows the Kimi membership tier plus Coding Plan Weekly/5H usage and reset times. Authenticated values refresh every 2 minutes while the last successful result remains cached; unknown values are never estimated. |
| <img src="docs/images/feature2.jpg" width="360" alt="System monitor"> | **Live system monitor**<br>Task-manager-style upload/download curves, a 56-second rolling window, an auto-scaling axis, plus Windows CPU and memory usage. |
| <img src="docs/images/music.jpg" width="360" alt="Now playing"> | **Now playing**<br>Album art, title, artist and progress bar in real time; switches in automatically when music starts, back when it stops. |
| | **Weather clock & stocks**<br>A Chinese weather clock with seconds, air quality, temperature, humidity and selectable pixel animation. Windows can use QWeather live conditions with a manually configured district or Windows geolocation, then fall back to Open-Meteo. The stock page supports up to 20 A-share, Hong Kong or US symbols, keeps four readable rows per page, and advances every five seconds using red-for-up/green-for-down. Both keep the last successful data during network failures. |
| | **Automatic screen saver & Codex alerts**<br>Windows can enter a moving-clock screen saver after 1/5/10/30/60 minutes of real keyboard and mouse inactivity. Immediate preview stays visible even while a pet is working. A Codex approval request globally switches to a red-border alert; one or more task completions play the Windows system sound while the display shows five smooth green pulses with a celebrating pet, then restores the previous page. |
| <img src="docs/images/feature3.jpg" width="360" alt="Swappable pets"> | **Swappable pets**<br>Built-in [petdex.dev](https://petdex.dev) gallery with 3300+ open-source pets, or upload any GIF — decoded on the board itself, no reflashing needed. |

## Getting started

What you need: an "SD2 mini-TV" dev board ([open-source hardware](https://oshwhub.com/q21182889/sd2), or [buy one assembled](https://mobile.yangkeduo.com/goods.html?ps=OuBjGMWE82)) and a USB **data** cable.

### Step 1 · Flash the firmware (~30 s)

Open **[mac.qust.me/#flash](https://mac.qust.me/#flash)** in Chrome / Edge, plug the device in over USB, click "Connect & Flash", pick the serial port and wait. No tools to install.

> Serial port not showing up? On Windows install the [CH340 driver](https://www.wch.cn/downloads/CH341SER_EXE.html); macOS has it built in. Try another USB cable (many are charge-only). More troubleshooting in the [website FAQ](https://mac.qust.me/#flash-faq).
>
> Command-line folks can also flash `esp8266-ai-firmware-*.bin` from [Releases](https://github.com/pengchujin/esp8266-ai/releases/latest) to address `0x0` with esptool.

### Step 2 · Connect WiFi (optional for Windows USB)

WiFi is used for wireless fallback and the device management page. A Windows PC with a USB data connection can skip this step. The device opens **`AI-Clock-Setup`** only after 15 seconds with neither a USB bridge nor a WiFi connection; join it and use the captive portal (or `192.168.4.1`) to choose a network.

### Step 3 · Install the bridge app

Download from [Releases](https://github.com/pengchujin/esp8266-ai/releases/latest) and open:

- **macOS**: `AIClockBridge-*-macOS.dmg`, drag into Applications (ad-hoc signed; on first launch allow it in "System Settings → Privacy & Security" and grant local-network access)
- **Windows**: `AIClockBridge-*-Windows-x64.exe`, just double-click

The bridge lives in your menu bar / tray. Windows uses direct CH340 serial transport when the USB data cable is attached and falls back to LAN automatically. macOS uses LAN discovery and pairing.

<p align="center">
  <img src="docs/images/working.jpg" width="640" alt="In action">
</p>

Daily use is all on the tray icon: **left-click** opens a live mirror (with a brightness slider); **right-click** opens grouped menus for quota, device connection, display modes, page cycling, content, pets and bridge service. Page cycling is enabled on first launch with a 15-second Codex, Claude, weather and stock sequence; users can select pages, reorder them and choose a 10/15/30/60-second interval. A manual page switch stops cycling.
The **Claude + Codex Quota** display mode puts both providers' limits and reset countdowns on one page.
**Display mode → Domestic models** lists the major Chinese vendors and selects exactly one. Alibaba Bailian and Moonshot Kimi are currently wired for verified quota capture; other entries are marked pending.
Screen saver controls are under **Display mode → Screen saver**, including timeout selection and immediate preview.

## FAQ

- **Screen border flashing red**: the device cannot reach the bridge. On Windows, check the USB data cable and the bridge app first; for macOS or wireless fallback, check LAN reachability.
- **Windows devices on the same WiFi still cannot communicate**: keep the USB data cable attached. Status, media, weather, stocks, display control and pet transfer all use direct COM transport and do not require LAN peer access.
- **Quota shows `-` / `--`**: the provider did not return a verifiable value. Qwen Token Plan and Kimi Coding Plan require one sign-in under “Model quota → Domestic model quota authorization”; the authorization page opens the currently selected vendor. Its isolated WebView2 session is retained for 30 days and renewed after successful reads. Domestic `today` tokens currently come only from local Claude Code logs and do not represent every app using the vendor account.
- **Want a different pet**: right-click the tray icon → "Change pet animation…", pick one and upload.

## Development

```
firmware/     ESP8266 firmware (PlatformIO + Arduino, with on-board GIF decoding)
mac-app/      macOS menu bar bridge (Swift/SPM, zero third-party dependencies)
windows-app/  Windows tray bridge (C# / .NET 8 WinForms)
tools/        GIF → RGB565 built-in sprite conversion script
docs/         Developer docs (pinout, HTTP API, architecture details)
```

```bash
cd firmware && pio run -t upload   # firmware: build + flash over USB
cd mac-app && swift run            # Mac bridge: run locally
```

Hardware pinout, display-driver gotchas, the device HTTP API and the on-board GIF decoding architecture are documented in **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)** (Chinese).

Hardware, firmware and software are all open source — modify it, build it, even sell it.
