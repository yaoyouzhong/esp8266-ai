// ESP8266 WiFi clock: shows local time plus live Claude Code / Codex CLI
// working status and usage quota, polled from a small bridge service that
// runs on the developer's Mac (see ../bridge/bridge.py).
//
// Display: 240x240 SPI ST7789 (TFT_eSPI). Pin mapping is set via build_flags
// in platformio.ini - edit those if your wiring differs.

#include <Arduino.h>
#include <ESP8266WiFi.h>
#include <ESP8266WebServer.h>
#include <ESP8266HTTPClient.h>
#include <WiFiClient.h>
#include <WiFiManager.h>
#include <LittleFS.h>
#include <ArduinoJson.h>
#include <TFT_eSPI.h>
#include <AnimatedGIF.h>
#include <time.h>

#include "config.h"
#include "img/claude_sprite.h"
#include "img/codex_sprite.h"
#include "img/claude_logo.h"
#include "img/codex_logo.h"

TFT_eSPI tft = TFT_eSPI();
ESP8266WebServer webServer(80);
WiFiManager wifiManager;

bool wifiPortalActive = false;
bool webServerStarted = false;
unsigned long wifiDisconnectedSinceMs = 0;
unsigned long lastWifiRetryMs = 0;
const unsigned long WIFI_PORTAL_DELAY_MS = 15000;
const unsigned long WIFI_RETRY_INTERVAL_MS = 30000;

// ---------- custom sprite storage (LittleFS) ----------
// Custom uploads replace the compiled-in default animation without needing a
// firmware rebuild. You POST a raw .gif straight to /sprite/claude or
// /sprite/codex (the device serves its own upload page at "/"); the ESP8266
// decodes and rescales the GIF *on-device* (AnimatedGIF, line-by-line so it
// never needs a full-canvas buffer) into the wire format below, which the
// display path then reads back frame-by-frame:
//   [1 byte frame count][frame0 bytes][frame1 bytes]...
// Each frame is exactly CLAUDE_SPRITE_W x H (or CODEX_SPRITE_W x H) RGB565
// pixels, byte order matching tools/convert_sprites.py's to_rgb565() so the
// compiled-in defaults and custom uploads share one draw path.
const char *CLAUDE_SPRITE_FILE = "/c.bin";
const char *CODEX_SPRITE_FILE = "/x.bin";
const char *CLAUDE_GIF_FILE = "/c.gif"; // raw upload, decoded then removed
const char *CODEX_GIF_FILE = "/x.gif";
const int MAX_CUSTOM_FRAMES = 8;
const size_t CLAUDE_FRAME_BYTES = (size_t)CLAUDE_SPRITE_W * CLAUDE_SPRITE_H * 2;
const size_t CODEX_FRAME_BYTES = (size_t)CODEX_SPRITE_W * CODEX_SPRITE_H * 2;

// We never hold a whole sprite frame in RAM. Decoding a GIF needs ~24KB of
// heap for AnimatedGIF's own buffers, which wouldn't fit alongside a static
// full-frame buffer (a 120x120 frame is ~28KB) on the ESP8266's ~80KB. So both
// the display path and the decoder work one screen-row at a time through these
// two small scratch rows (SCREEN_W is the widest we ever need).
uint16_t rowBuf[SCREEN_W];     // current row being drawn / decoded
uint16_t prevRowBuf[SCREEN_W]; // decode only: same row from the previous frame

bool claudeCustom = false;
int claudeCustomFrames = 0;
bool codexCustom = false;
int codexCustomFrames = 0;
uint32_t spriteRev = 0; // bumped on upload/reset so the Mac mirror re-fetches

const int SCREEN_CX = 120, SCREEN_CY = 120;
const int RING_MARGIN = 4;      // inset from screen edge
const int RING_THICKNESS = 10;  // ring bar thickness
const uint16_t RING_TRACK_COLOR = 0x2104; // visible dark grey on black
const unsigned long ANIM_INTERVAL_MS = 120;  // sprite frame advance
const unsigned long FLASH_INTERVAL_MS = 400; // "urgent" flash speed
const unsigned long COMPLETION_PULSE_INTERVAL_MS = 140;
const unsigned long SWITCH_BOTH_MS = 2000;   // both apps working: alternate fast
const unsigned long SWITCH_IDLE_MS = 6000;   // neither working: alternate slow

enum ActiveApp { APP_CLAUDE, APP_CODEX };
ActiveApp currentApp = APP_CLAUDE;
unsigned long lastSwitchMs = 0;

// Display override, settable from the Mac app via POST /api/display:
// auto = follow working status, claude/codex = pin that app on screen,
// domestic/net/music/stock/weather = bridge-side pages; screensaver = moving clock.
enum DisplayMode { MODE_AUTO, MODE_CLAUDE, MODE_CODEX, MODE_DUAL, MODE_DOMESTIC, MODE_NET, MODE_MUSIC, MODE_STOCK, MODE_WEATHER, MODE_SCREENSAVER };
DisplayMode displayMode = MODE_AUTO;
DisplayMode effectiveMode();
bool screenSaverPreview = false;

const uint8_t COMPLETION_PULSE_STEPS = 10;
const uint8_t COMPLETION_PULSE_COUNT = 5;
const uint8_t COMPLETION_FLASH_PHASES = COMPLETION_PULSE_STEPS * COMPLETION_PULSE_COUNT;
uint8_t completionFlashPhase = COMPLETION_FLASH_PHASES + 1;
unsigned long completionFlashLastMs = 0;
uint32_t codexCompletionSeq = 0;
bool codexCompletionActive = false;
bool codexCompletionInitialized = false;
uint32_t usbAlertSession = 0;
uint32_t usbAlertSeq = 0;
bool usbAlertInitialized = false;

bool completionAlertActive() {
  // The final phase restores the quota ring after five complete pulses.
  return completionFlashPhase <= COMPLETION_FLASH_PHASES;
}

void startCodexCompletionAlert() {
  codexCompletionActive = true;
  completionFlashPhase = 0;
  completionFlashLastMs = millis() - COMPLETION_PULSE_INTERVAL_MS;
  currentApp = APP_CODEX;
  lastSwitchMs = millis();
}

// When AUTO and the Mac reports audio playing, the screen auto-switches to the
// music page and back when it stops — same spirit as the Claude/Codex auto
// switch. Only AUTO does this; a pinned mode is always honored as-is.
bool statusMusicPlaying = false;
DisplayMode lastEffectiveMode = MODE_AUTO;
uint32_t bridgeEpochUtc = 0;
// China is the safe first-boot default for this device. The bridge overwrites
// and persists the actual Windows offset, so other regions remain supported.
int bridgeUtcOffsetS = 8 * 3600;
unsigned long bridgeClockSyncMs = 0;
bool ntpStarted = false;
bool ntpSynced = false;
const time_t NTP_VALID_AFTER = 1704067200; // 2024-01-01 UTC
int screenSaverOldX = -1, screenSaverOldY = -1, screenSaverOldW = 0, screenSaverOldH = 0;
long screenSaverLastTick = -1;

// 18x18 monochrome glyphs for 周日一二三四五六. The screen saver must remain
// usable without Wi-Fi, so its weekday cannot depend on a bridge bitmap.
const uint32_t WEEKDAY_GLYPHS[8][18] PROGMEM = {
  { 0x00000, 0x07FFC, 0x0630C, 0x0630C, 0x06FEC, 0x0630C, 0x0630C, 0x07FEC, 0x0600C, 0x0EFEC, 0x0EC6C, 0x0CC6C, 0x0CFEC, 0x0CC0C, 0x1C00C, 0x08078, 0x00000, 0x00000 },
  { 0x00000, 0x07FF8, 0x06018, 0x06018, 0x06018, 0x06018, 0x06018, 0x07FF8, 0x06018, 0x06018, 0x06018, 0x06018, 0x06018, 0x06018, 0x07FF8, 0x06018, 0x00000, 0x00000 },
  { 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x1FFFE, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000 },
  { 0x00000, 0x00000, 0x07FF8, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x1FFFE, 0x00000, 0x00000, 0x00000, 0x00000 },
  { 0x00000, 0x00000, 0x0FFFC, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x07FF8, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x1FFFE, 0x00000, 0x00000, 0x00000 },
  { 0x00000, 0x0FFFC, 0x0CCCC, 0x0CCCC, 0x0CCCC, 0x0CCCC, 0x0CCCC, 0x0CCCC, 0x0CCCC, 0x0CCCC, 0x0D8FC, 0x0F80C, 0x0C00C, 0x0FFFC, 0x0C00C, 0x00000, 0x00000, 0x00000 },
  { 0x00000, 0x0FFFC, 0x00E00, 0x00C00, 0x00C00, 0x00C00, 0x0FFF0, 0x01C30, 0x01C30, 0x01830, 0x01830, 0x01830, 0x01830, 0x01830, 0x1FFFE, 0x00000, 0x00000, 0x00000 },
  { 0x00300, 0x00700, 0x00380, 0x00300, 0x1FFFE, 0x00000, 0x00000, 0x01840, 0x01CE0, 0x03870, 0x03870, 0x07038, 0x0601C, 0x0E01C, 0x1C00E, 0x04008, 0x00000, 0x00000 },
};

// ---------- net speed mode state ----------
// Rendering is decoupled from the network: pollNet() fetches every 2s and
// only refills a queue of 250ms samples (the bridge samples at 4Hz and tags
// them with a running seq, so nothing is drawn twice or skipped). The sweep
// itself consumes exactly one queued sample every NET_DRAW_INTERVAL_MS, so
// the trace advances at a constant rate no matter how long HTTP takes.
const unsigned long NET_POLL_INTERVAL_MS = 2000; // queue refill cadence
const unsigned long NET_DRAW_INTERVAL_MS = 250;  // one chart step per bridge sample
const int NET_QUEUE = 32;
long netQRx[NET_QUEUE], netQTx[NET_QUEUE]; // ring buffer of pending samples
int netQHead = 0, netQCount = 0;
long netSeq = -1;                          // last bridge sample seq consumed into the queue
long netCurRx = 0, netCurTx = 0;           // smoothed readout for the header
int netCpuPct = 0, netMemPct = 0;
unsigned long lastNetPollMs = 0;
unsigned long lastNetDrawMs = 0;
bool netChromeDrawn = false;
bool netHeaderDirty = false;

// Chart layout (task-manager style scrolling area chart, newest at the right)
const int NET_CHART_X = 8, NET_CHART_Y = 60, NET_CHART_W = 224, NET_CHART_H = 128;
long netHistRx[NET_CHART_W], netHistTx[NET_CHART_W]; // one 250ms sample per column
long netScale = 10240;    // current "nice" full-scale value (whole chart shares it)
String netLastDl, netLastUl, netLastScaleText; // change detection for partial redraws
String netLastCpuVal, netLastMemVal;
bool netSysLabelsDrawn = false;

// ---------- music mode state ----------
const int MUSIC_COVER_W = 128;
const int MUSIC_COVER_H = 128;
// Title/artist come as a Mac-rendered bitmap strip (232x44) because the
// panel fonts are ASCII-only and CJK titles would render as blanks.
const int MUSIC_TEXT_W = 232;
const int MUSIC_TEXT_H = 44;
const int MUSIC_TEXT_X = 4, MUSIC_TEXT_Y = 150;
const unsigned long MUSIC_POLL_INTERVAL_MS = 2000;
String musicTitle, musicArtist, musicAlbum;
bool musicPlaying = false;
int musicElapsed = 0, musicDuration = 0;
int musicArtworkRev = -1;
int musicTextRev = -1;
bool musicHasArtwork = false;
bool musicChromeDrawn = false;
unsigned long lastMusicPollMs = 0;

// ---------- stock watchlist / weather clock state ----------
const unsigned long STOCK_POLL_INTERVAL_MS = 5000;
const unsigned long STOCK_PAGE_INTERVAL_MS = 5000;
const int MAX_STOCKS = 20;
const int STOCK_ROWS_PER_PAGE = 4;
const int STOCK_NAME_W = 156, STOCK_NAME_H = 20;
struct StockRow { String code, price, pct; int up = 0; };
StockRow stocks[MAX_STOCKS];
int stockCount = 0;
int stockPage = 0;
int stockNamesRev = -1, stockNamesDrawnRev = -1;
bool stockEverLoaded = false, stockDirty = false, stockChromeDrawn = false;
String stockLastCode[STOCK_ROWS_PER_PAGE], stockLastValue[STOCK_ROWS_PER_PAGE];
unsigned long lastStockPollMs = 0, lastStockPageMs = 0;

const unsigned long WEATHER_POLL_INTERVAL_MS = 15000;
const int WEATHER_HEADER_W = 122, WEATHER_HEADER_H = 26;
const int WEATHER_DATE_W = 190, WEATHER_DATE_H = 30;
const int WEATHER_AIR_W = 100, WEATHER_AIR_H = 30;
const int WEATHER_CONTENT_LEFT = 14;
const int WEATHER_HEADER_Y = 1;
const int WEATHER_DATE_X = WEATHER_CONTENT_LEFT, WEATHER_DATE_Y = 117;
// Air quality occupies the first 42 px; the weather condition replaces the
// former icon in the 52 px region at the right.
const int WEATHER_AIR_X = 136, WEATHER_AIR_Y = 12;
const int WEATHER_ANIM_BOTTOM = 224;
struct WeatherStatus {
  float temp = 0, high = 0, low = 0, pm25 = -1;
  int humidity = 0, icon = -1, animation = 0, utcOffsetS = 0, textRev = -1, dateCenterX = WEATHER_DATE_W / 2, headerCenterX = WEATHER_HEADER_W / 2, rangeY = 34;
  uint32_t epochUtc = 0;
  bool stale = false, loaded = false;
};
WeatherStatus weatherStatus;
int weatherContentCenter() {
  return WEATHER_DATE_X + constrain(weatherStatus.dateCenterX, 0, WEATHER_DATE_W - 1);
}
int weatherHeaderX() { return WEATHER_DATE_X; }
int weatherHeaderCenter() {
  return weatherHeaderX() + constrain(weatherStatus.headerCenterX, 0, WEATHER_HEADER_W - 1);
}
int weatherTextDrawnRev = -1;
bool weatherChromeDrawn = false;
unsigned long weatherSyncMs = 0, lastWeatherPollMs = 0, lastWeatherClockMs = 0;
unsigned long lastWeatherAnimMs = 0;
int weatherAnimFrame = 0;
int weatherLastAnimation = -1;
int weatherLastHour = -1, weatherLastMinute = -1, weatherLastSecond = -1, weatherLastStale = -1;
void drawWeatherScreen(bool force);
void drawStockCachedOrLoading();
void drawWeatherCachedOrLoading();

int claudeFrame = 0;
int codexFrame = 0;
unsigned long lastAnimMs = 0;

bool flashOn = true;
unsigned long lastFlashMs = 0;

// Bridge host is not asked for during first-time WiFi setup: the Mac/Windows
// bridge discovers the device and pairs automatically (or set via /api/bridge).
String bridgeHost;

struct ClaudeStatus {
  String plan;
  String status = "unknown";
  long tokensToday = 0;
  int sessionMin = 0;
  int sessionWindowMin = 300;
  float fiveHourPct = -1; // real OAuth quota from the bridge, -1 = unknown
  int fiveHourResetMin = -1; // minutes until the 5h window resets
  float sevenDayPct = -1;
  int sevenDayResetMin = -1; // minutes until the 7-day window resets
  bool needsInput = false; // waiting on a permission/approval prompt
};

struct CodexStatus {
  String plan;
  String status = "unknown";
  long tokensToday = 0;
  float primaryPct = -1;
  int primaryResetMin = -1;
  float weeklyPct = -1;
  int weeklyResetMin = -1;
  int resetCreditsAvailable = -1;
  uint32_t resetCreditExpiresAt = 0;
  bool needsInput = false;
  uint32_t completionAt = 0;
};

struct DomesticProviderStatus {
  String model;
  bool membershipBadge = false;
  long tokensToday = 0;
  float planPct = -1;
  String planPctText;
  String remainingPctText;
  float fiveHourPct = -1;
  int fiveHourResetMin = -1;
  float weeklyPct = -1;
  int weeklyResetMin = -1;
  uint32_t planResetAt = 0;
  int planResetMin = -1;
  float balance = -1;
  float usedCost = -1;
  String currency;
};

struct DomesticStatus {
  String status = "offline";
  String activeProvider;
  bool needsInput = false;
  // The bridge supplies this provider-neutral display payload. qwen/xiaomi
  // remain as compatibility data; future vendors only need to populate active.
  DomesticProviderStatus active;
  DomesticProviderStatus qwen;
  DomesticProviderStatus xiaomi;
  DomesticProviderStatus kimi;
  DomesticProviderStatus minimax;
  DomesticProviderStatus deepseek;
};

ClaudeStatus claudeStatus;
CodexStatus codexStatus;
DomesticStatus domesticStatus;

unsigned long lastPollMs = 0;
unsigned long lastSuccessMs = 0;
bool everPolled = false;

// USB bridge frames share the CH340 serial stream with human-readable debug
// logs. Only lines with this prefix are parsed as protocol messages.
const char *USB_FRAME_PREFIX = "@AICLOCK ";
const size_t USB_TEXT_FRAME_MAX = 8192;
const unsigned long USB_STALE_MS = 8000;
bool hostGoingAway = false;
unsigned long lastUsbStatusMs = 0;
bool everUsbStatus = false;
bool decodeGifToBin(const char *gifPath, const char *binPath, int targetW, int targetH);

enum UsbBlobKind { USB_BLOB_NONE, USB_MUSIC_COVER, USB_MUSIC_TEXT, USB_STOCK_NAMES, USB_STOCK_NAMES_RLE, USB_WEATHER_HEADER, USB_WEATHER_DATE, USB_WEATHER_AIR, USB_WEATHER_LABELS, USB_WEATHER_LABELS_RLE, USB_GIF_CLAUDE, USB_GIF_CODEX };
const char *USB_UI_TEMP_FILE = "/usb-ui.tmp";
const char *USB_UI_BACKUP_FILE = "/usb-ui.bak";
const char *STOCK_UI_CACHE_FILE = "/stock-ui.rle";
const char *WEATHER_UI_CACHE_FILE = "/weather-ui.rle";
struct UsbBlobState {
  UsbBlobKind kind = USB_BLOB_NONE;
  uint16_t transfer = 0;
  uint16_t nextSeq = 0;
  uint32_t expectedSize = 0;
  uint32_t received = 0;
  uint32_t expectedCrc = 0;
  uint32_t crc = 0xffffffff;
  int width = 0, height = 0;
  int rowFill = 0, rowIndex = 0;
  File file;
  bool active = false;
} usbBlob;

bool usbBridgeActive() {
  return everUsbStatus && millis() - lastUsbStatusMs < USB_STALE_MS;
}

// ---------- backlight brightness ----------
// The panel backlight (TFT_BL, active LOW) is PWM-dimmable — the vendor's own
// firmware does the same. 0 = off, 100 = full. Persisted so it survives reboot.

int brightness = BRIGHTNESS_DEFAULT; // 0-100

void applyBrightness() {
  // analogWriteRange(100) is set in setup(), so the duty value is just the
  // inverted percentage (active LOW: 0 duty = always LOW = full on).
  analogWrite(TFT_BL, 100 - brightness);
}

void loadBrightness() {
  if (!LittleFS.exists(BRIGHTNESS_FILE)) return;
  File f = LittleFS.open(BRIGHTNESS_FILE, "r");
  if (!f) return;
  int v = f.readStringUntil('\n').toInt();
  f.close();
  if (v >= 0 && v <= 100) brightness = v;
}

void saveBrightness() {
  File f = LittleFS.open(BRIGHTNESS_FILE, "w");
  if (!f) return;
  f.println(brightness);
  f.close();
}

// ---------- persistence for the bridge host ----------

void loadBridgeHost() {
  if (LittleFS.exists(WIFI_CONFIG_FILE)) {
    File f = LittleFS.open(WIFI_CONFIG_FILE, "r");
    bridgeHost = f.readStringUntil('\n');
    bridgeHost.trim();
    f.close();
  }
}

void saveBridgeHost(const String &host) {
  File f = LittleFS.open(WIFI_CONFIG_FILE, "w");
  f.println(host);
  f.close();
}

// ---------- custom sprite loading ----------

// Checks LittleFS for a previously-uploaded custom sprite and validates its
// size before trusting it (frame count byte + exact expected byte length).
void loadCustomSpriteState() {
  claudeCustom = false;
  if (LittleFS.exists(CLAUDE_SPRITE_FILE)) {
    File f = LittleFS.open(CLAUDE_SPRITE_FILE, "r");
    if (f && f.size() >= 1) {
      uint8_t cnt = f.read();
      size_t expected = 1 + (size_t)cnt * CLAUDE_FRAME_BYTES;
      if (cnt > 0 && cnt <= MAX_CUSTOM_FRAMES && (size_t)f.size() == expected) {
        claudeCustom = true;
        claudeCustomFrames = cnt;
      }
    }
    if (f) f.close();
  }

  codexCustom = false;
  if (LittleFS.exists(CODEX_SPRITE_FILE)) {
    File f = LittleFS.open(CODEX_SPRITE_FILE, "r");
    if (f && f.size() >= 1) {
      uint8_t cnt = f.read();
      size_t expected = 1 + (size_t)cnt * CODEX_FRAME_BYTES;
      if (cnt > 0 && cnt <= MAX_CUSTOM_FRAMES && (size_t)f.size() == expected) {
        codexCustom = true;
        codexCustomFrames = cnt;
      }
    }
    if (f) f.close();
  }

  Serial.printf("[sprite] claude custom=%d frames=%d | codex custom=%d frames=%d\n", claudeCustom,
                claudeCustomFrames, codexCustom, codexCustomFrames);
}

int claudeFrameCount() { return claudeCustom ? claudeCustomFrames : CLAUDE_SPRITE_FRAMES; }
int codexFrameCount() { return codexCustom ? codexCustomFrames : CODEX_SPRITE_FRAMES; }

// Draws one sprite frame centered on screen, one row at a time so we never
// need a full-frame buffer: each row comes either from the custom LittleFS
// file (streamed) or the compiled-in PROGMEM default (copied row-by-row).
void drawSpriteFrame(bool custom, const char *file, const uint16_t *const *progmemFrames, int frameIdx, int w,
                     int h, size_t frameBytes) {
  int x0 = SCREEN_CX - w / 2, y0 = SCREEN_CY - h / 2;
  size_t rowBytes = (size_t)w * 2;
  if (custom) {
    File f = LittleFS.open(file, "r");
    if (!f) return;
    f.seek(1 + (size_t)frameIdx * frameBytes);
    for (int r = 0; r < h; r++) {
      f.read((uint8_t *)rowBuf, rowBytes);
      tft.pushImage(x0, y0 + r, w, 1, rowBuf);
    }
    f.close();
  } else {
    const uint16_t *frame = progmemFrames[frameIdx];
    for (int r = 0; r < h; r++) {
      memcpy_P(rowBuf, frame + (size_t)r * w, rowBytes);
      tft.pushImage(x0, y0 + r, w, 1, rowBuf);
    }
  }
}

// ---------- helpers ----------

String formatTokens(long tokens) {
  if (tokens >= 1000000) {
    char buf[16];
    snprintf(buf, sizeof(buf), "%.1fM", tokens / 1000000.0);
    return String(buf);
  }
  if (tokens >= 1000) {
    char buf[16];
    snprintf(buf, sizeof(buf), "%.1fk", tokens / 1000.0);
    return String(buf);
  }
  return String(tokens);
}

// ---------- drawing ----------

void drawStaticChrome() {
  tft.fillScreen(TFT_BLACK);
}

// Bridge unreachable / data stale -> flashing red overrides everything else,
// matches the "urgent, look now" state from the reference signal-light design.
bool bridgeStale() {
  if (usbBridgeActive()) return false;
  if (!everPolled) return true;
  return (millis() - lastSuccessMs) >= 2UL * BRIDGE_POLL_INTERVAL_MS;
}

// True when the app currently on screen is waiting on a permission/approval
// prompt — drives the red "look now, act" border flash.
bool currentAppNeedsInput() {
  return currentApp == APP_CLAUDE ? claudeStatus.needsInput : codexStatus.needsInput;
}

// Working vs idle is now conveyed by the sprite animation itself (moving vs
// still), not by ring color. The ring just stays steady green, except
// bridge-stale which flashes red ("check it now") and overrides everything.
uint16_t currentStatusColor() {
  if (bridgeStale()) return flashOn ? TFT_RED : TFT_BLACK;
  return TFT_GREEN;
}

// The ring is skipped when nothing changed (see drawSquareRing) so the 5s
// poll doesn't visibly blank-and-repaint it. Anything that paints over the
// ring area must invalidate this cache.
float ringLastPct = -1000;
uint16_t ringLastColor = 1;

// Paints the full square border in one color (all four sides), used for the
// attention flash so the whole edge blinks, not just the filled quota arc.
void drawFullBorder(uint16_t color) {
  ringLastPct = -1000; // ring got painted over; next ring draw must repaint
  int x0 = RING_MARGIN, y0 = RING_MARGIN;
  int side = SCREEN_W - 2 * RING_MARGIN;
  tft.fillRect(x0, y0, side, RING_THICKNESS, color);                              // top
  tft.fillRect(x0, SCREEN_H - RING_MARGIN - RING_THICKNESS, side, RING_THICKNESS, color); // bottom
  tft.fillRect(x0, y0, RING_THICKNESS, side, color);                              // left
  tft.fillRect(SCREEN_W - RING_MARGIN - RING_THICKNESS, y0, RING_THICKNESS, side, color); // right
}

// Square progress ring hugging the screen edge. `pct` of the perimeter
// (clockwise from top-left) is drawn in `color`, the rest in dark grey.
void drawSquareRing(float pct, uint16_t color) {
  if (pct < 0) pct = 0;
  if (pct > 100) pct = 100;
  if (pct == ringLastPct && color == ringLastColor) return; // nothing changed
  ringLastPct = pct;
  ringLastColor = color;

  int x0 = RING_MARGIN, y0 = RING_MARGIN;
  int x1 = SCREEN_W - RING_MARGIN, y1 = SCREEN_H - RING_MARGIN;
  int side = x1 - x0;
  float perimeter = side * 4.0;

  // Keep the whole perimeter visible so a small percentage still reads as a
  // progress ring instead of an isolated short status line.
  tft.fillRect(x0, y0, side, RING_THICKNESS, RING_TRACK_COLOR);                  // top
  tft.fillRect(x1 - RING_THICKNESS, y0, RING_THICKNESS, side, RING_TRACK_COLOR); // right
  tft.fillRect(x0, y1 - RING_THICKNESS, side, RING_THICKNESS, RING_TRACK_COLOR); // bottom
  tft.fillRect(x0, y0, RING_THICKNESS, side, RING_TRACK_COLOR);                  // left

  // filled portion, clockwise: top -> right -> bottom -> left
  float remaining = perimeter * (pct / 100.0);
  if (remaining <= 0) return;

  float seg = min(remaining, (float)side);
  tft.fillRect(x0, y0, (int)seg, RING_THICKNESS, color);
  remaining -= side;
  if (remaining <= 0) return;

  seg = min(remaining, (float)side);
  tft.fillRect(x1 - RING_THICKNESS, y0, RING_THICKNESS, (int)seg, color);
  remaining -= side;
  if (remaining <= 0) return;

  seg = min(remaining, (float)side);
  tft.fillRect(x1 - (int)seg, y1 - RING_THICKNESS, (int)seg, RING_THICKNESS, color);
  remaining -= side;
  if (remaining <= 0) return;

  seg = min(remaining, (float)side);
  tft.fillRect(x0, y1 - (int)seg, RING_THICKNESS, (int)seg, color);
}

void drawClaudeSprite(int frameIdx) {
  drawSpriteFrame(claudeCustom, CLAUDE_SPRITE_FILE, claude_sprite_frames, frameIdx, CLAUDE_SPRITE_W,
                  CLAUDE_SPRITE_H, CLAUDE_FRAME_BYTES);
}

void drawCodexSprite(int frameIdx) {
  drawSpriteFrame(codexCustom, CODEX_SPRITE_FILE, codex_sprite_frames, frameIdx, CODEX_SPRITE_W, CODEX_SPRITE_H,
                  CODEX_FRAME_BYTES);
}

String pctText(float pct) {
  return pct >= 0 ? String((int)pct) + "%" : "-";
}

// One compact row per quota window: label, used percentage and reset time.
// It reuses the old 48px footer, so the pet keeps its original dimensions.
String lastQuota5hPct, lastQuota5hReset, lastQuotaWkPct, lastQuotaWkReset;
bool lastQuotaSingle = false;
String quotaResetText(int minutes);
void epochToLocal(uint32_t utc, int offset, int &year, int &month, int &day,
                  int &hour, int &minute, int &second, int &weekday);
const uint16_t QUOTA_PANEL_COLOR = 0x1082;
const uint16_t QUOTA_PANEL_BORDER = 0x29A5;

// Faux-bold: the packed TFT_eSPI fonts have no bold face, so draw twice with
// a 1px x offset. Transparent draws - the caller must have cleared the region.
void drawBoldString(const String &s, int x, int y, int font, uint16_t color) {
  tft.setTextColor(color);
  tft.drawString(s, x, y, font);
  tft.drawString(s, x + 1, y, font);
}

void drawQuotaRow(const char *label, float pct, int resetMin, int y, bool force,
                  String &lastPct, String &lastReset) {
  String value = pctText(pct);
  String reset = quotaResetText(resetMin);
  if (force) {
    tft.setTextDatum(MC_DATUM);
    drawBoldString(label, 53, y + 11, 2, 0x9492);
  }
  if (force || value != lastPct) {
    lastPct = value;
    tft.fillRect(87, y + 1, 66, 19, QUOTA_PANEL_COLOR);
    tft.setTextDatum(MC_DATUM);
    drawBoldString(value, 120, y + 11, 2, TFT_WHITE);
  }
  if (force || reset != lastReset) {
    lastReset = reset;
    tft.fillRect(154, y + 1, 63, 19, QUOTA_PANEL_COLOR);
    tft.setTextDatum(MC_DATUM);
    drawBoldString(reset, 187, y + 11, 2, TFT_CYAN);
  }
}

void loadUtcOffset() {
  if (!LittleFS.exists(UTC_OFFSET_FILE)) return;
  File f = LittleFS.open(UTC_OFFSET_FILE, "r");
  if (!f) return;
  int value = f.readStringUntil('\n').toInt();
  f.close();
  if (value >= -12 * 3600 && value <= 14 * 3600) bridgeUtcOffsetS = value;
}

void saveUtcOffset(int value) {
  if (value == bridgeUtcOffsetS) return;
  bridgeUtcOffsetS = value;
  File f = LittleFS.open(UTC_OFFSET_FILE, "w");
  if (!f) return;
  f.println(value);
  f.close();
}

void serviceNtp() {
  if (WiFi.status() != WL_CONNECTED) return;
  if (!ntpStarted) {
    // configTime is asynchronous on ESP8266; never block display/USB startup
    // while DNS or an NTP server is unavailable.
    configTime(0, 0, "ntp.aliyun.com", "ntp.tencent.com", "pool.ntp.org");
    ntpStarted = true;
    Serial.println("[ntp] synchronization started");
  }
  time_t now = time(nullptr);
  if (!ntpSynced && now >= NTP_VALID_AFTER) {
    ntpSynced = true;
    Serial.printf("[ntp] synchronized epoch=%lu\n", (unsigned long)now);
  }
}

void drawQuotaText(float hourPct, int hourResetMin, float weekPct, int weekResetMin, bool force) {
  bool single = hourPct < 0 && weekPct >= 0;
  if (single != lastQuotaSingle) force = true;
  if (force) {
    tft.fillRect(18, 177, 204, 47, TFT_BLACK);
    if (single) {
      tft.fillRoundRect(20, 191, 200, 24, 7, QUOTA_PANEL_COLOR);
      tft.drawRoundRect(20, 191, 200, 24, 7, QUOTA_PANEL_BORDER);
    } else {
      tft.fillRoundRect(20, 178, 200, 21, 6, QUOTA_PANEL_COLOR);
      tft.drawRoundRect(20, 178, 200, 21, 6, QUOTA_PANEL_BORDER);
      tft.fillRoundRect(20, 201, 200, 21, 6, QUOTA_PANEL_COLOR);
      tft.drawRoundRect(20, 201, 200, 21, 6, QUOTA_PANEL_BORDER);
    }
    lastQuota5hPct = lastQuota5hReset = lastQuotaWkPct = lastQuotaWkReset = "";
  }
  lastQuotaSingle = single;
  if (single) {
    drawQuotaRow("WK", weekPct, weekResetMin, 192, force, lastQuotaWkPct, lastQuotaWkReset);
  } else {
    drawQuotaRow("5H", hourPct, hourResetMin, 178, force, lastQuota5hPct, lastQuota5hReset);
    drawQuotaRow("WK", weekPct, weekResetMin, 201, force, lastQuotaWkPct, lastQuotaWkReset);
  }
}

// ---------- quota-exhausted countdown ----------
// When the current app's 5h or weekly window is used up, the pet is replaced
// by a countdown to that window's reset (bridge sends minutes-until-reset).
// A spent weekly window blocks usage even after the 5h one resets, so the
// weekly countdown takes priority when both are exhausted.

enum CdType { CD_NONE, CD_5H, CD_WEEK };

float currentHourPct() {
  return currentApp == APP_CLAUDE ? claudeStatus.fiveHourPct : codexStatus.primaryPct;
}

int currentHourResetMin() {
  return currentApp == APP_CLAUDE ? claudeStatus.fiveHourResetMin : codexStatus.primaryResetMin;
}

float currentWeekPct() {
  return currentApp == APP_CLAUDE ? claudeStatus.sevenDayPct : codexStatus.weeklyPct;
}

int currentWeekResetMin() {
  return currentApp == APP_CLAUDE ? claudeStatus.sevenDayResetMin : codexStatus.weeklyResetMin;
}

CdType desiredCountdown() {
  if (currentWeekPct() >= 99.9f && currentWeekResetMin() >= 0) return CD_WEEK;
  if (currentHourPct() >= 99.9f && currentHourResetMin() >= 0) return CD_5H;
  return CD_NONE;
}

CdType showingCd = CD_NONE; // what's on screen now (vs desiredCountdown())
String lastCountdown;

// The bridge only reports whole minutes, so the seconds tick locally against
// a deadline anchored at millis(). Re-anchor only when the bridge disagrees
// by more than ~a minute (new window, big clock drift), otherwise a poll
// landing mid-minute would make the seconds jump around.
unsigned long cdDeadlineMs = 0; // 0 = not anchored
ActiveApp cdApp = APP_CLAUDE;   // which app/window the anchor belongs to
CdType cdAnchorType = CD_NONE;

void syncCountdownDeadline() {
  int m = showingCd == CD_WEEK ? currentWeekResetMin() : currentHourResetMin();
  if (m < 0) {
    cdDeadlineMs = 0;
    return;
  }
  long bridgeSec = (long)m * 60 + 30; // bridge floors to minutes: assume mid-minute
  long ourSec = (long)(cdDeadlineMs - millis()) / 1000;
  if (cdDeadlineMs == 0 || cdApp != currentApp || cdAnchorType != showingCd || ourSec < 0 ||
      labs(ourSec - bridgeSec) > 90) {
    cdDeadlineMs = millis() + (unsigned long)bridgeSec * 1000UL;
    cdApp = currentApp;
    cdAnchorType = showingCd;
  }
}

void drawCountdown(bool force) {
  long remain = cdDeadlineMs ? (long)(cdDeadlineMs - millis()) / 1000
                             : (long)(showingCd == CD_WEEK ? currentWeekResetMin() : currentHourResetMin()) * 60;
  if (remain < 0) remain = 0;
  char buf[16];
  long hours = remain / 3600;
  if (hours >= 100) // weekly can be up to 168h: h:mm:ss wouldn't fit the ring
    snprintf(buf, sizeof(buf), "%ld:%02ld", hours, (remain % 3600) / 60);
  else
    snprintf(buf, sizeof(buf), "%ld:%02ld:%02ld", hours, (remain % 3600) / 60, remain % 60);
  String t(buf);
  if (!force && t == lastCountdown) return;
  // in-place glyph overwrite can't erase a shrinking string (h:mm:ss width is
  // constant, but 100:00 -> 99:59:59 changes layout once) - clear on any
  // length change
  if (t.length() != lastCountdown.length()) force = true;
  lastCountdown = t;
  tft.setTextDatum(TC_DATUM);
  if (force) {
    tft.fillRect(SCREEN_CX - 99, 66, 198, 84, TFT_BLACK);
    drawBoldString(showingCd == CD_WEEK ? "Wk RESET IN" : "5h RESET IN", SCREEN_CX, 72, 2, TFT_LIGHTGREY);
  }
  // Background-color draw overwrites glyphs in place (no clear-then-draw
  // flash between seconds).
  tft.setTextColor(TFT_ORANGE, TFT_BLACK);
  tft.drawString(t, SCREEN_CX, 102, 6);
}

// App logo in the top-left corner (inside the quota ring) so a glance tells
// which app the screen is currently showing. Drawn row-by-row from PROGMEM
// through rowBuf, same as the sprite path.
const int LOGO_X = 14, LOGO_Y = 18;

void drawAppLogo() {
  const uint16_t *logo = (currentApp == APP_CLAUDE) ? claude_logo_0 : codex_logo_0;
  int w = (currentApp == APP_CLAUDE) ? CLAUDE_LOGO_W : CODEX_LOGO_W;
  int h = (currentApp == APP_CLAUDE) ? CLAUDE_LOGO_H : CODEX_LOGO_H;
  for (int r = 0; r < h; r++) {
    memcpy_P(rowBuf, logo + (size_t)r * w, (size_t)w * 2);
    tft.pushImage(LOGO_X, LOGO_Y + r, w, 1, rowBuf);
  }
}

// Compact Claude + Codex quota overview. It reuses TFT_eSPI's built-in fonts
// and repaints only the section whose values changed, so status polling does
// not flash the whole screen.
String dualLastClaudeKey, dualLastCodexKey;

String quotaResetText(int minutes) {
  if (minutes < 0) return "";
  if (minutes >= 1440) return String(minutes / 1440) + "d " + String((minutes % 1440) / 60) + "h";
  if (minutes >= 60) return String(minutes / 60) + "h " + String(minutes % 60) + "m";
  return String(minutes) + "m";
}

uint16_t quotaBarColor(float pct) {
  if (pct >= 99.5f) return TFT_RED;
  if (pct >= 80) return TFT_YELLOW;
  return TFT_GREEN;
}

uint16_t dualPlanColor(const String &plan) {
  if (plan == "PRO" || plan == "PRO LITE" || plan == "MAX"
      || plan == "MAX 5X" || plan == "MAX 20X") return TFT_ORANGE;
  if (plan == "PLUS") return TFT_CYAN;
  if (plan == "TEAM" || plan == "BUSINESS" || plan == "ENTERPRISE") return TFT_MAGENTA;
  return TFT_LIGHTGREY;
}

void drawDualPlanBadge(const String &plan, int top) {
  if (plan.length() == 0) return;
  uint16_t color = dualPlanColor(plan);
  tft.setTextFont(2);
  int width = constrain(tft.textWidth(plan) + 16, 40, 112);
  int left = 220 - width;
  tft.fillRoundRect(left, top, width, 17, 4, TFT_BLACK);
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(color);
  tft.drawString(plan, left + width / 2, top + 8, 2);
  // Draw the outline last. An opaque font background used to erase the top
  // and bottom edges, leaving only two parenthesis-like side arcs.
  tft.drawRoundRect(left, top, width, 17, 4, color);
}

void drawDualResetCreditBadge(int top) {
  if (codexStatus.resetCreditsAvailable <= 0) return;
  uint16_t color = TFT_GREEN;
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(color, TFT_BLACK);
  tft.drawString("R*" + String(codexStatus.resetCreditsAvailable), 98, top + 8, 2);
}

void drawDualRow(const char *label, float pct, int resetMin, int y) {
  tft.setTextDatum(TL_DATUM);
  tft.setTextColor(0x7BEF, TFT_BLACK);
  tft.drawString(label, 20, y + 4, 2);
  tft.drawString(quotaResetText(resetMin), 54, y + 7, 1);
  tft.setTextDatum(TR_DATUM);
  tft.setTextColor(TFT_WHITE, TFT_BLACK);
  tft.drawString(pctText(pct), 220, y, 4);
  tft.fillRoundRect(20, y + 24, 200, 6, 3, 0x2104);
  if (pct >= 0) {
    int width = constrain((int)(200 * min(pct, 100.0f) / 100.0f), 0, 200);
    if (width > 0) tft.fillRoundRect(20, y + 24, width, 6, 3, quotaBarColor(pct));
  }
}

void drawDualSection(bool claude, bool force) {
  String plan = claude ? claudeStatus.plan : codexStatus.plan;
  String status = claude ? claudeStatus.status : codexStatus.status;
  float firstPct = claude ? claudeStatus.fiveHourPct : codexStatus.primaryPct;
  int firstReset = claude ? claudeStatus.fiveHourResetMin : codexStatus.primaryResetMin;
  float weekPct = claude ? claudeStatus.sevenDayPct : codexStatus.weeklyPct;
  int weekReset = claude ? claudeStatus.sevenDayResetMin : codexStatus.weeklyResetMin;
  bool single = !claude && firstPct < 0;
  String key = plan + "|" + status + "|" + String(firstPct, 1) + "|" + String(firstReset)
      + "|" + String(weekPct, 1) + "|" + String(weekReset) + "|" + String(single)
      + "|" + String(claude ? -1 : codexStatus.resetCreditsAvailable);
  String &lastKey = claude ? dualLastClaudeKey : dualLastCodexKey;
  if (!force && key == lastKey) return;
  lastKey = key;

  int top = claude ? 29 : 126;
  int height = claude ? 90 : 106;
  tft.fillRect(0, top, SCREEN_W, height, TFT_BLACK);
  uint16_t statusColor = status == "working" ? TFT_GREEN
      : status == "idle" ? TFT_YELLOW : 0x39E7;
  tft.fillCircle(18, top + 9, 4, statusColor);
  tft.setTextDatum(TL_DATUM);
  tft.setTextColor(claude ? TFT_ORANGE : TFT_CYAN, TFT_BLACK);
  drawBoldString(claude ? "CLAUDE" : "CODEX", 31, top, 2, claude ? TFT_ORANGE : TFT_CYAN);
  if (!claude) drawDualResetCreditBadge(top);
  drawDualPlanBadge(plan, top);

  if (single) {
    drawDualRow("WK", weekPct, weekReset, top + 34);
  } else {
    drawDualRow("5H", firstPct, firstReset, top + 21);
    drawDualRow("WK", weekPct, weekReset, top + 54);
  }
}

void drawDualScreen(bool force = false) {
  if (force) {
    tft.fillScreen(TFT_BLACK);
    dualLastClaudeKey = "";
    dualLastCodexKey = "";
    tft.setTextDatum(TC_DATUM);
    drawBoldString("USAGE OVERVIEW", SCREEN_CX, 8, 2, TFT_WHITE);
    tft.drawFastHLine(18, 121, 204, 0x2945);
  }
  drawDualSection(true, force);
  drawDualSection(false, force);
}

String currentPlan() {
  return currentApp == APP_CLAUDE ? claudeStatus.plan : codexStatus.plan;
}

uint16_t planColor(const String &plan) {
  if (plan == "PRO" || plan == "PRO LITE") return TFT_ORANGE;
  if (plan == "PLUS") return TFT_CYAN;
  if (plan == "TEAM" || plan == "BUSINESS" || plan == "ENTERPRISE") return TFT_MAGENTA;
  if (plan == "MAX" || plan == "MAX 5X" || plan == "MAX 20X") return TFT_ORANGE;
  return TFT_LIGHTGREY;
}

String lastPlanBadge;
String lastResetCreditBadge;

void drawPlanBadge(bool force) {
  String plan = currentPlan();
  bool reserveResetSpace = currentApp == APP_CODEX && codexStatus.resetCreditsAvailable > 0;
  String key = plan + "|" + String(reserveResetSpace);
  if (!force && key == lastPlanBadge) return;
  lastPlanBadge = key;
  int clearWidth = reserveResetSpace ? 92 : 101;
  int maxWidth = reserveResetSpace ? 88 : 98;
  tft.fillRect(60, 27, clearWidth, 22, TFT_BLACK); // erase a previous, longer label
  if (plan.length() == 0) return;
  uint16_t color = planColor(plan);
  int w = constrain(tft.textWidth(plan, 2) + 12, 34, maxWidth);
  tft.fillRoundRect(61, 29, w, 18, 5, TFT_BLACK);
  tft.drawRoundRect(61, 29, w, 18, 5, color);
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(color, TFT_BLACK);
  tft.drawString(plan, 61 + w / 2, 38, 2);
}

void drawResetCreditBadge(bool force) {
  String count = currentApp == APP_CODEX && codexStatus.resetCreditsAvailable > 0
      ? "R*" + String(codexStatus.resetCreditsAvailable) : "";
  String expiry;
  if (codexStatus.resetCreditExpiresAt > 0) {
    int year, month, day, hour, minute, second, weekday;
    epochToLocal(codexStatus.resetCreditExpiresAt, bridgeUtcOffsetS,
                 year, month, day, hour, minute, second, weekday);
    expiry = String(month) + "/" + String(day);
  }
  String key = count + "|" + expiry;
  if (!force && key == lastResetCreditBadge) return;
  lastResetCreditBadge = key;
  const int badgeX = 153;
  const int badgeW = 68;
  const int badgeCenterX = badgeX + badgeW / 2;
  tft.fillRect(badgeX - 1, 27, badgeW + 2, 22, TFT_BLACK);
  if (count.length() == 0) return;
  tft.fillRoundRect(badgeX, 29, badgeW, 18, 5, TFT_BLACK);
  tft.drawRoundRect(badgeX, 29, badgeW, 18, 5, TFT_GREEN);
  int gap = expiry.length() > 0 ? 3 : 0;
  int countWidth = tft.textWidth(count, 2);
  int expiryWidth = expiry.length() > 0 ? tft.textWidth(expiry, 2) : 0;
  int x = badgeCenterX - (countWidth + gap + expiryWidth) / 2;
  tft.setTextDatum(ML_DATUM);
  tft.setTextColor(TFT_GREEN, TFT_BLACK);
  tft.drawString(count, x, 38, 2);
  if (expiry.length() > 0)
    tft.drawString(expiry, x + countWidth + gap, 38, 2);
}

// Claude's ring percentage is only a real 5h quota. Unknown never becomes an
// elapsed-time estimate: that was the source of the misleading "5%" display.
float claudeRingPct() {
  return max(claudeStatus.fiveHourPct, 0.0f);
}

float codexRingPct() {
  if (codexStatus.weeklyPct >= 0) return codexStatus.weeklyPct;
  return max(codexStatus.primaryPct, 0.0f);
}

// Redraws whichever app is currently active, full screen: quota ring +
// sprite (or the reset countdown while the 5h window is exhausted).
// Full clear + repaint - only for real transitions (app switch, mode return,
// sprite change); steady-state data updates go through refreshActiveApp().
void drawActiveApp() {
  tft.fillScreen(TFT_BLACK);
  ringLastPct = -1000; // screen was cleared: force the ring repaint
  showingCd = desiredCountdown();
  if (showingCd != CD_NONE) syncCountdownDeadline();
  else cdDeadlineMs = 0;
  if (currentApp == APP_CLAUDE) {
    drawSquareRing(claudeRingPct(), currentStatusColor());
    if (showingCd == CD_NONE) drawClaudeSprite(claudeFrame);
    drawQuotaText(claudeStatus.fiveHourPct, claudeStatus.fiveHourResetMin,
                  claudeStatus.sevenDayPct, claudeStatus.sevenDayResetMin, true);
  } else {
    drawSquareRing(codexRingPct(), currentStatusColor());
    if (showingCd == CD_NONE) drawCodexSprite(codexFrame);
    drawQuotaText(codexStatus.primaryPct, codexStatus.primaryResetMin,
                  codexStatus.weeklyPct, codexStatus.weeklyResetMin, true);
  }
  if (showingCd != CD_NONE) drawCountdown(true);
  drawAppLogo();
  drawResetCreditBadge(true);
  drawPlanBadge(true);
}

// In-place refresh after a bridge poll: ring repaint + only the text that
// actually changed. No fillScreen, so the 5s poll doesn't blank the screen.
void refreshActiveApp() {
  if (desiredCountdown() != showingCd) { // pet <-> countdown (or 5h <-> weekly) swap
    drawActiveApp();
    return;
  }
  if (currentApp == APP_CLAUDE) {
    drawSquareRing(claudeRingPct(), currentStatusColor());
    drawQuotaText(claudeStatus.fiveHourPct, claudeStatus.fiveHourResetMin,
                  claudeStatus.sevenDayPct, claudeStatus.sevenDayResetMin, false);
  } else {
    drawSquareRing(codexRingPct(), currentStatusColor());
    drawQuotaText(codexStatus.primaryPct, codexStatus.primaryResetMin,
                  codexStatus.weeklyPct, codexStatus.weeklyResetMin, false);
  }
  if (showingCd != CD_NONE) {
    syncCountdownDeadline();
    drawCountdown(false);
  }
  drawResetCreditBadge(false);
  drawPlanBadge(false);
}

// Redraws just the ring (cheap) - used for status color animation ticks
// between full redraws.
void redrawRingOnly() {
  if (currentApp == APP_CLAUDE) {
    drawSquareRing(claudeRingPct(), currentStatusColor());
  } else {
    drawSquareRing(codexRingPct(), currentStatusColor());
  }
}

// Draw five smooth completion pulses on an independent cadence, then restore
// the real quota ring exactly once.
void drawCompletionPulse(unsigned long nowMs) {
  if (!completionAlertActive()
      || nowMs - completionFlashLastMs < COMPLETION_PULSE_INTERVAL_MS) return;

  completionFlashLastMs = nowMs;
  if (completionFlashPhase < COMPLETION_FLASH_PHASES) {
    static const uint8_t brightness[COMPLETION_PULSE_STEPS] = {
      40, 88, 144, 208, 255, 255, 208, 144, 88, 0
    };
    uint8_t level = brightness[completionFlashPhase % COMPLETION_PULSE_STEPS];
    drawFullBorder(tft.color565(0, level, 0));
    completionFlashPhase++;
    return;
  }

  redrawRingOnly();
  completionFlashPhase++;
}

// Who gets the screen:
//   - display mode pinned (Mac app) -> that app, always
//   - exactly one app working       -> that app, immediately
//   - both working                  -> alternate every SWITCH_BOTH_MS (2s)
//   - neither working               -> alternate slowly (SWITCH_IDLE_MS)
bool updateActiveApp() {
  ActiveApp desired = currentApp;

  if (claudeStatus.needsInput && !codexStatus.needsInput) {
    desired = APP_CLAUDE; // approval prompt wins the screen
  } else if (codexStatus.needsInput && !claudeStatus.needsInput) {
    desired = APP_CODEX;
  } else if (completionAlertActive()) {
    desired = APP_CODEX;
  } else if (displayMode == MODE_CLAUDE) {
    desired = APP_CLAUDE;
  } else if (displayMode == MODE_CODEX) {
    desired = APP_CODEX;
  } else {
    bool claudeWorking = claudeStatus.status == "working";
    bool codexWorking = codexStatus.status == "working";
    if (claudeWorking && !codexWorking) {
      desired = APP_CLAUDE;
    } else if (codexWorking && !claudeWorking) {
      desired = APP_CODEX;
    } else {
      unsigned long interval = (claudeWorking && codexWorking) ? SWITCH_BOTH_MS : SWITCH_IDLE_MS;
      if (millis() - lastSwitchMs >= interval) {
        lastSwitchMs = millis();
        desired = (currentApp == APP_CLAUDE) ? APP_CODEX : APP_CLAUDE;
      }
    }
  }

  if (desired != currentApp) {
    currentApp = desired;
    lastSwitchMs = millis();
    return true;
  }
  return false;
}

// ---------- net speed screen ----------

String speedText(long bps) {
  char buf[16];
  if (bps >= 1000000) snprintf(buf, sizeof(buf), "%.1fM", bps / 1000000.0);
  else if (bps >= 1000) snprintf(buf, sizeof(buf), "%.0fK", bps / 1000.0);
  else snprintf(buf, sizeof(buf), "%ldB", bps);
  return String(buf);
}

// pushImage() colors must be pre-byte-swapped (this firmware never enables
// setSwapBytes; see the sprite pipeline). Natural RGB565 -> wire order:
inline uint16_t swap565(uint16_t c) { return (uint16_t)((c << 8) | (c >> 8)); }

void resetNetChart() {
  memset(netHistRx, 0, sizeof(netHistRx));
  memset(netHistTx, 0, sizeof(netHistTx));
  netScale = 10240;
  netLastDl = "";
  netLastUl = "";
  netLastScaleText = "";
  netLastCpuVal = "";
  netLastMemVal = "";
  netSysLabelsDrawn = false;
  netQHead = 0;
  netQCount = 0;
  netSeq = -1;
}

long adaptiveNetScale(long maxV) {
  long scale = maxV + maxV / 7;
  return max(scale, 10240L);
}

// Static chrome: labels that never change while in net mode.
void drawNetChrome() {
  tft.fillScreen(TFT_BLACK);
  tft.setTextDatum(TL_DATUM);
  tft.setTextColor(0x7BEF, TFT_BLACK);
  tft.drawString("DOWN", 14, 10, 1);
  tft.drawString("UP", 134, 10, 1);
  tft.setTextDatum(TC_DATUM);
  tft.drawString("SYSTEM MONITOR", SCREEN_CX, 226, 1);
}

void drawNetSysinfoIfChanged() {
  const int rowY = 192;
  tft.setTextDatum(TL_DATUM);
  if (!netSysLabelsDrawn) {
    netSysLabelsDrawn = true;
    tft.setTextColor(0x7BEF, TFT_BLACK);
    tft.drawString("CPU", 28, rowY + 6, 2);
    tft.drawString("MEM", 130, rowY + 6, 2);
  }
  String cpu = String(netCpuPct) + "%";
  String mem = String(netMemPct) + "%";
  tft.setTextColor(TFT_WHITE, TFT_BLACK);
  if (cpu != netLastCpuVal) {
    netLastCpuVal = cpu;
    tft.fillRect(62, rowY, 64, 26, TFT_BLACK);
    tft.drawString(cpu, 62, rowY, 4);
  }
  if (mem != netLastMemVal) {
    netLastMemVal = mem;
    tft.fillRect(164, rowY, 64, 26, TFT_BLACK);
    tft.drawString(mem, 164, rowY, 4);
  }
}

// Header readouts (1s-averaged), each repainted only when its text changes.
void drawNetHeaderIfChanged() {
  String dl = speedText(netCurRx) + "/s";
  String ul = speedText(netCurTx) + "/s";
  tft.setTextDatum(TL_DATUM);
  if (dl != netLastDl) {
    netLastDl = dl;
    tft.fillRect(12, 20, 116, 28, TFT_BLACK);
    tft.setTextColor(TFT_GREEN, TFT_BLACK);
    tft.drawString(dl, 12, 20, 4);
  }
  if (ul != netLastUl) {
    netLastUl = ul;
    tft.fillRect(132, 20, 108, 28, TFT_BLACK);
    tft.setTextColor(TFT_YELLOW, TFT_BLACK);
    tft.drawString(ul, 132, 20, 4);
  }
}

// Repaints the whole chart region from the sample ring, one row at a time
// through rowBuf (a single pushImage per row = no clear-then-draw flicker).
// Download is a dim-green filled area with a bright top edge; upload is a
// 2px yellow line on top; faint gridlines at 25/50/75%.
void drawNetChart() {
  static const uint16_t COL_GRID = swap565(0x2104);   // very dark grey
  static const uint16_t COL_FILL = swap565(0x02A0);   // dim green
  static const uint16_t COL_EDGE = swap565(TFT_GREEN);
  static const uint16_t COL_UL = swap565(TFT_YELLOW);
  static const uint16_t COL_BLACK = swap565(TFT_BLACK);

  long maxV = 0;
  for (int i = 0; i < NET_CHART_W; i++) {
    if (netHistRx[i] > maxV) maxV = netHistRx[i];
    if (netHistTx[i] > maxV) maxV = netHistTx[i];
  }
  netScale = adaptiveNetScale(maxV);

  // Per-column heights (3-tap smoothed), then per-column line "bands": each
  // band spans from the previous column's height to this one's, so steep
  // rises/falls render as connected vertical strokes instead of detached
  // stair-step dots — that's what makes the undulation read as a continuous
  // line, like the Mac mirror's stroked polyline.
  static uint8_t hRx[NET_CHART_W], hTx[NET_CHART_W];
  static uint8_t dlLo[NET_CHART_W], dlHi[NET_CHART_W]; // DL edge band, incl. 3px weight
  static uint8_t ulLo[NET_CHART_W], ulHi[NET_CHART_W]; // UL line band
  const int LINE_T = 5; // calibrated for the physical 240px panel
  for (int i = 0; i < NET_CHART_W; i++) {
    int lo = i > 0 ? i - 1 : 0, hi = i < NET_CHART_W - 1 ? i + 1 : NET_CHART_W - 1;
    long rx = (netHistRx[lo] + netHistRx[i] + netHistRx[hi]) / 3;
    long tx = (netHistTx[lo] + netHistTx[i] + netHistTx[hi]) / 3;
    int hr = (int)((float)rx / netScale * (NET_CHART_H - 2));
    int ht = (int)((float)tx / netScale * (NET_CHART_H - 2));
    hRx[i] = (uint8_t)constrain(hr, 0, NET_CHART_H - 1);
    hTx[i] = (uint8_t)constrain(ht, 0, NET_CHART_H - 1);
  }
  for (int i = 0; i < NET_CHART_W; i++) {
    int prevR = i > 0 ? hRx[i - 1] : hRx[0];
    int prevT = i > 0 ? hTx[i - 1] : hTx[0];
    dlHi[i] = (uint8_t)max((int)hRx[i], prevR);
    dlLo[i] = (uint8_t)max(0, min((int)hRx[i], prevR) - (LINE_T - 1));
    ulHi[i] = (uint8_t)max((int)hTx[i], prevT);
    ulLo[i] = (uint8_t)max(0, min((int)hTx[i], prevT) - (LINE_T - 1));
  }

  for (int row = 0; row < NET_CHART_H; row++) {
    int yFromBot = NET_CHART_H - 1 - row;
    bool gridRow = (row == NET_CHART_H / 4 || row == NET_CHART_H / 2 || row == 3 * NET_CHART_H / 4);
    for (int i = 0; i < NET_CHART_W; i++) {
      uint16_t c = gridRow ? COL_GRID : COL_BLACK;
      if (yFromBot <= dlHi[i] && yFromBot >= dlLo[i]) c = COL_EDGE;
      else if (yFromBot < dlLo[i]) c = COL_FILL;
      if (ulHi[i] > 0 && yFromBot <= ulHi[i] && yFromBot >= ulLo[i]) c = COL_UL;
      rowBuf[i] = c;
    }
    tft.pushImage(NET_CHART_X, NET_CHART_Y + row, NET_CHART_W, 1, rowBuf);
    if ((row & 31) == 31) yield();
  }

  // axis label (outside the chart, so it never gets repainted over)
  String scaleText = speedText(netScale);
  if (scaleText != netLastScaleText) {
    netLastScaleText = scaleText;
    tft.fillRect(120, 48, 112, 10, TFT_BLACK);
    tft.setTextDatum(TR_DATUM);
    tft.setTextColor(0x7BEF, TFT_BLACK);
    tft.drawString(scaleText, NET_CHART_X + NET_CHART_W, 48, 1);
    tft.setTextDatum(TL_DATUM);
  }
}

// Chart tick, every NET_DRAW_INTERVAL_MS: shift in queued sample(s), then
// one atomic repaint. If the queue backs up after a slow poll, it works off
// up to three samples per tick until it's back in step.
void netDrawTick() {
  if (!netChromeDrawn) {
    resetNetChart();
    drawNetChrome();
    netChromeDrawn = true;
    netHeaderDirty = true;
  }
  if (netHeaderDirty) {
    drawNetHeaderIfChanged();
    drawNetSysinfoIfChanged();
    netHeaderDirty = false;
  }
  if (netQCount == 0) return;
  int steps = min(netQCount, netQCount > 16 ? 3 : 1);
  while (steps-- > 0 && netQCount > 0) {
    memmove(netHistRx, netHistRx + 1, sizeof(long) * (NET_CHART_W - 1));
    memmove(netHistTx, netHistTx + 1, sizeof(long) * (NET_CHART_W - 1));
    netHistRx[NET_CHART_W - 1] = netQRx[netQHead];
    netHistTx[NET_CHART_W - 1] = netQTx[netQHead];
    netQHead = (netQHead + 1) % NET_QUEUE;
    netQCount--;
  }
  drawNetChart();
}

// Refills the sample queue from the bridge's /net endpoint. The seq field
// tells us which samples we've already queued, so overlapping tails are fine.
bool applyNetJson(JsonObject doc) {
  netCurRx = doc["rx_bps"] | 0L;
  netCurTx = doc["tx_bps"] | 0L;
  netCpuPct = constrain(doc["cpu_pct"] | 0, 0, 100);
  netMemPct = constrain(doc["mem_pct"] | 0, 0, 100);
  netHeaderDirty = true;
  long seq = doc["seq"] | -1L;
  JsonArray rx = doc["rx"], tx = doc["tx"];
  int n = min(rx.size(), tx.size());
  int fresh = (netSeq < 0) ? min(n, 8) : (int)min((long)n, seq - netSeq);
  if (fresh < 0) fresh = 0;
  for (int i = n - fresh; i < n; i++) {
    if (netQCount >= NET_QUEUE) break;
    int tail = (netQHead + netQCount) % NET_QUEUE;
    netQRx[tail] = rx[i].as<long>();
    netQTx[tail] = tx[i].as<long>();
    netQCount++;
  }
  if (seq >= 0) netSeq = seq;
  return true;
}

String pctOrDash(float pct) {
  return pct >= 0 ? String((int)pct) + "%" : "--";
}

void epochToLocal(uint32_t utc, int offset, int &year, int &month, int &day,
                  int &hour, int &minute, int &second, int &weekday);

String domesticPlanResetText(uint32_t resetAt, int resetMin) {
  if (resetAt > 0) {
    int year, month, day, hour, minute, second, weekday;
    epochToLocal(resetAt, bridgeUtcOffsetS, year, month, day, hour, minute, second, weekday);
    char buf[16];
    snprintf(buf, sizeof(buf), "%02d-%02d %02d:%02d", month, day, hour, minute);
    return String(buf);
  }
  return quotaResetText(resetMin);
}

String fitDomesticText(String text, int maxWidth, int font) {
  while (text.length() > 0 && tft.textWidth(text, font) > maxWidth) {
    text.remove(text.length() - 1);
  }
  return text;
}

struct DomesticDrawCache {
  String provider;
  String model;
  String tokens;
  String reset;
  String plan;
  String remaining;
  bool windowed = false;
  bool balance = false;
  bool initialized = false;
};

DomesticDrawCache domesticDrawCache;

void drawDomesticScreen(bool force = false) {
  const DomesticProviderStatus &p = domesticStatus.active;
  String provider = domesticStatus.activeProvider.length() ? domesticStatus.activeProvider : "qwen";
  provider.toUpperCase();
  bool isKimi = provider == "KIMI";
  bool isBalance = provider == "DEEPSEEK" && p.balance >= 0;
  bool isWindowed = isKimi || p.fiveHourPct >= 0 || p.weeklyPct >= 0;
  String model = p.model.length() ? fitDomesticText(p.model, 112, 2) : "--";
  float displayPct = isWindowed && p.weeklyPct >= 0 ? p.weeklyPct : p.planPct;
  String tokens = isBalance
      ? (p.usedCost >= 0 ? String(p.usedCost, 2) + " " + p.currency : "--") : isWindowed
      ? (p.fiveHourPct >= 0 ? String((int)p.fiveHourPct) + "%" : "--")
      : domesticPlanResetText(p.planResetAt, p.planResetMin);
  String reset = isWindowed ? quotaResetText(p.fiveHourResetMin) : "";
  String planNumber = isBalance ? String(p.balance, 2) : displayPct >= 0
      ? (!isWindowed && p.planPctText.length() ? p.planPctText : String((int)displayPct)) : "--";
  String plan = isBalance ? planNumber + p.currency : displayPct >= 0 ? planNumber + "%" : "--";
  String remaining = isBalance ? "" : isWindowed ? quotaResetText(p.weeklyResetMin)
      : p.planPct >= 0
          ? (p.remainingPctText.length() ? p.remainingPctText
              : String(floorf(max(0.0f, 100.0f - p.planPct) * 100.0f) / 100.0f, 2)) + "% LEFT"
          : "QUOTA UNKNOWN";
  const uint16_t panelColor = 0x1082;
  const uint16_t mutedColor = 0x7BEF;
  const uint16_t numberColor = 0xFFDF;

  if (force) {
    tft.fillScreen(TFT_BLACK);
    ringLastPct = -1000;
    domesticDrawCache.initialized = false;
    tft.fillCircle(25, 30, 4, TFT_GREEN);
    tft.fillRect(20, 53, 200, 1, mutedColor);
    tft.fillRoundRect(20, 177, 200, 38, 8, panelColor);
    tft.drawRoundRect(20, 177, 200, 38, 8, 0x29A5);
    tft.setTextDatum(TL_DATUM);
  }
  drawSquareRing(isBalance ? 0.0f : max(displayPct, 0.0f), TFT_GREEN);
  if (force || !domesticDrawCache.initialized || provider != domesticDrawCache.provider
      || isWindowed != domesticDrawCache.windowed || isBalance != domesticDrawCache.balance) {
    tft.fillRect(34, 20, 72, 22, TFT_BLACK);
    tft.setTextDatum(TL_DATUM);
    drawBoldString(provider, 36, 24, 2, TFT_GREEN);
    if (!isBalance) {
      const int contentLeft = RING_MARGIN + RING_THICKNESS;
      tft.fillRect(contentLeft, 69, SCREEN_W - contentLeft * 2, 16, TFT_BLACK);
      tft.setTextDatum(TC_DATUM);
      tft.setTextColor(mutedColor, TFT_BLACK);
      tft.drawString(isWindowed ? "WEEKLY" : "PLAN", SCREEN_CX, 73, 1);
    }
    tft.fillRect(28, 181, 76, 31, panelColor);
    if (isBalance) {
      tft.setTextDatum(MC_DATUM);
      drawBoldString("USED", 53, 196, 2, TFT_GREEN);
    } else if (isWindowed) {
      tft.setTextDatum(MC_DATUM);
      drawBoldString("5H", 53, 196, 2, TFT_GREEN);
    } else {
      tft.setTextDatum(MC_DATUM);
      drawBoldString("RESET", 53, 196, 2, TFT_GREEN);
    }
  }
  if (force || !domesticDrawCache.initialized || model != domesticDrawCache.model
      || provider != domesticDrawCache.provider) {
    tft.fillRect(106, 20, 114, 22, TFT_BLACK);
    if (p.membershipBadge && p.model.length()) {
      const uint16_t badgeColor = TFT_ORANGE;
      int w = constrain(tft.textWidth(model, 2) + 12, 34, 112);
      int x = 218 - w;
      tft.fillRoundRect(x, 22, w, 18, 5, TFT_BLACK);
      tft.drawRoundRect(x, 22, w, 18, 5, badgeColor);
      tft.setTextDatum(MC_DATUM);
      tft.setTextColor(badgeColor, TFT_BLACK);
      tft.drawString(model, x + w / 2, 31, 2);
    } else {
      tft.setTextDatum(TR_DATUM);
      tft.setTextColor(TFT_LIGHTGREY, TFT_BLACK);
      tft.drawString(model, 218, 24, 2);
    }
  }
  if (force || !domesticDrawCache.initialized || plan != domesticDrawCache.plan) {
    String suffix = isBalance ? p.currency : "%";
    if (isBalance) {
      const String caption = "AVAILABLE BALANCE";
      const int areaX = 20, areaY = 62, areaW = 200, areaH = 106;
      const int maxContentW = areaW - 16, maxContentH = areaH - 16;
      int captionFont = 2, valueFont = 6, currencyFont = 4;
      int lineGap = 8, valueGap = suffix.length() ? 6 : 0;
      int captionWidth = tft.textWidth(caption, captionFont);
      int numberWidth = tft.textWidth(planNumber, valueFont) + 1;
      int suffixWidth = suffix.length() ? tft.textWidth(suffix, currencyFont) : 0;
      int valueWidth = numberWidth + valueGap + suffixWidth;
      int componentHeight = tft.fontHeight(captionFont) + lineGap + tft.fontHeight(valueFont);

      // Keep the balance dominant and CNY secondary. Built-in fonts have
      // discrete sizes, so choose the largest complete layout that fits.
      if (max(captionWidth, valueWidth) > maxContentW || componentHeight > maxContentH) {
        valueFont = 4;
        captionWidth = tft.textWidth(caption, captionFont);
        numberWidth = tft.textWidth(planNumber, valueFont) + 1;
        suffixWidth = suffix.length() ? tft.textWidth(suffix, currencyFont) : 0;
        valueWidth = numberWidth + valueGap + suffixWidth;
        componentHeight = tft.fontHeight(captionFont) + lineGap + tft.fontHeight(valueFont);
      }
      if (max(captionWidth, valueWidth) > maxContentW || componentHeight > maxContentH) {
        captionFont = 1;
        valueFont = 2;
        currencyFont = 1;
        lineGap = 6;
        valueGap = suffix.length() ? 4 : 0;
        captionWidth = tft.textWidth(caption, captionFont);
        numberWidth = tft.textWidth(planNumber, valueFont) + 1;
        suffixWidth = suffix.length() ? tft.textWidth(suffix, currencyFont) : 0;
        valueWidth = numberWidth + valueGap + suffixWidth;
        componentHeight = tft.fontHeight(captionFont) + lineGap + tft.fontHeight(valueFont);
      }

      const int centerX = areaX + areaW / 2;
      int top = areaY + (areaH - componentHeight) / 2;
      int valueTop = top + tft.fontHeight(captionFont) + lineGap;
      int valueLeft = centerX - valueWidth / 2;
      tft.fillRect(areaX, areaY, areaW, areaH, TFT_BLACK);
      tft.setTextDatum(TL_DATUM);
      tft.setTextColor(mutedColor, TFT_BLACK);
      tft.drawString(caption, centerX - captionWidth / 2, top, captionFont);
      drawBoldString(planNumber, valueLeft, valueTop, valueFont, numberColor);
      if (suffixWidth) {
        tft.setTextColor(TFT_GREEN, TFT_BLACK);
        int baselineCorrection = valueFont == 6 && currencyFont == 4 ? 8
            : valueFont == currencyFont ? 0 : 2;
        int currencyTop = valueTop + tft.fontHeight(valueFont)
            - tft.fontHeight(currencyFont) - baselineCorrection;
        tft.drawString(suffix, valueLeft + numberWidth + valueGap, currencyTop, currencyFont);
      }
    } else {
      tft.fillRect(28, 90, 184, 54, TFT_BLACK);
      int numberFont = planNumber.length() <= 3 ? 7 : planNumber.length() <= 6 ? 4 : 2;
      int percentFont = numberFont == 7 ? 4 : 2;
      int numberY = numberFont == 7 ? 90 : numberFont == 6 ? 86 : numberFont == 4 ? 101 : 108;
      int percentY = numberFont == 7 ? 105 : numberFont == 6 ? 114 : numberFont == 4 ? 108 : 108;
      int numberWidth = tft.textWidth(planNumber, numberFont);
      int percentWidth = displayPct >= 0 ? tft.textWidth(suffix, percentFont) : 0;
      int left = SCREEN_CX - (numberWidth + (percentWidth ? 4 + percentWidth : 0)) / 2;
      tft.setTextDatum(TL_DATUM);
      drawBoldString(planNumber, left, numberY, numberFont, numberColor);
      if (percentWidth) {
        tft.setTextColor(TFT_GREEN, TFT_BLACK);
        tft.drawString(suffix, left + numberWidth + 4, percentY, percentFont);
      }
    }
  }
  if (force || !domesticDrawCache.initialized || remaining != domesticDrawCache.remaining) {
    tft.fillRect(30, 151, 180, 16, TFT_BLACK);
    if (!isBalance) {
      tft.setTextDatum(TL_DATUM);
      tft.setTextColor(mutedColor, TFT_BLACK);
      tft.drawString(isWindowed ? "RESET" : "REMAINING", 37, 153, 1);
      tft.setTextDatum(TR_DATUM);
      tft.setTextColor(TFT_GREEN, TFT_BLACK);
      tft.drawString(remaining, 203, 151, 2);
    }
  }
  if (force || !domesticDrawCache.initialized || tokens != domesticDrawCache.tokens
      || reset != domesticDrawCache.reset || isWindowed != domesticDrawCache.windowed
      || isBalance != domesticDrawCache.balance) {
    tft.fillRect(87, 181, 131, 29, panelColor);
    if (isBalance) {
      tft.setTextDatum(MC_DATUM);
      if (p.usedCost >= 0) {
        String amount = String(p.usedCost, 2);
        int amountWidth = tft.textWidth(amount, 2);
        int currencyWidth = tft.textWidth(p.currency, 2);
        int gap = p.currency.length() ? 5 : 0;
        int left = 153 - (amountWidth + gap + currencyWidth) / 2;
        tft.setTextDatum(ML_DATUM);
        drawBoldString(amount, left, 196, 2, numberColor);
        if (currencyWidth) {
          drawBoldString(p.currency, left + amountWidth + gap, 196, 2, TFT_GREEN);
        }
      } else {
        drawBoldString("--", 153, 196, 2, numberColor);
      }
    } else if (isWindowed) {
      tft.setTextDatum(MC_DATUM);
      drawBoldString(tokens, 120, 196, 2, TFT_WHITE);
      drawBoldString(reset, 187, 196, 2, TFT_CYAN);
    } else {
      tft.setTextDatum(MC_DATUM);
      drawBoldString(tokens, 153, 196, 2, TFT_CYAN);
    }
  }
  domesticDrawCache.provider = provider;
  domesticDrawCache.model = model;
  domesticDrawCache.tokens = tokens;
  domesticDrawCache.reset = reset;
  domesticDrawCache.plan = plan;
  domesticDrawCache.remaining = remaining;
  domesticDrawCache.windowed = isWindowed;
  domesticDrawCache.balance = isBalance;
  domesticDrawCache.initialized = true;
}

void pollNet() {
  if (usbBridgeActive()) return;
  if (WiFi.status() != WL_CONNECTED || bridgeHost.length() == 0) return;
  WiFiClient client;
  HTTPClient http;
  String url = "http://" + bridgeHost + "/net";
  http.setTimeout(BRIDGE_HTTP_TIMEOUT_MS);
  if (!http.begin(client, url)) return;
  int code = http.GET();
  if (code == HTTP_CODE_OK) {
    JsonDocument doc;
    if (!deserializeJson(doc, http.getString())) {
      applyNetJson(doc.as<JsonObject>());
    }
  }
  http.end();
}

String timeText(int sec) {
  if (sec < 0) sec = 0;
  char buf[12];
  snprintf(buf, sizeof(buf), "%d:%02d", sec / 60, sec % 60);
  return String(buf);
}

String fitText(String s, int maxPx, int font) {
  if (tft.textWidth(s, font) <= maxPx) return s;
  while (s.length() > 0 && tft.textWidth(s + "...", font) > maxPx) {
    s.remove(s.length() - 1);
  }
  return s + "...";
}

void drawMusicCoverPlaceholder() {
  const int x = (SCREEN_W - MUSIC_COVER_W) / 2;
  const int y = 14;
  tft.fillRect(x, y, MUSIC_COVER_W, MUSIC_COVER_H, TFT_DARKGREY);
  tft.drawRect(x, y, MUSIC_COVER_W, MUSIC_COVER_H, TFT_DARKGREY);
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(TFT_LIGHTGREY, TFT_DARKGREY);
  tft.drawString("No Art", SCREEN_CX, y + MUSIC_COVER_H / 2, 2);
}

bool drawMusicCoverFromBridge() {
  if (usbBridgeActive()) return false; // binary artwork remains WiFi-only in v1
  if (WiFi.status() != WL_CONNECTED || bridgeHost.length() == 0 || !musicHasArtwork) return false;
  WiFiClient client;
  HTTPClient http;
  String url = "http://" + bridgeHost + "/music/cover.raw";
  http.setTimeout(BRIDGE_HTTP_TIMEOUT_MS);
  if (!http.begin(client, url)) return false;
  int code = http.GET();
  if (code != HTTP_CODE_OK) {
    http.end();
    return false;
  }
  WiFiClient *stream = http.getStreamPtr();
  const int x = (SCREEN_W - MUSIC_COVER_W) / 2;
  const int y = 14;
  const size_t rowBytes = (size_t)MUSIC_COVER_W * 2;
  bool ok = true;
  for (int r = 0; r < MUSIC_COVER_H; r++) {
    int got = stream->readBytes((uint8_t *)rowBuf, rowBytes);
    if (got != (int)rowBytes) {
      ok = false;
      break;
    }
    tft.pushImage(x, y + r, MUSIC_COVER_W, 1, rowBuf);
    yield();
  }
  http.end();
  return ok;
}

// Streams the Mac-rendered 232x44 title/artist strip and blits it row by
// row — the only way to get CJK on screen without shipping a font.
bool drawMusicTextFromBridge() {
  if (usbBridgeActive()) return false; // use the metadata fallback over USB
  if (WiFi.status() != WL_CONNECTED || bridgeHost.length() == 0) return false;
  WiFiClient client;
  HTTPClient http;
  String url = "http://" + bridgeHost + "/music/text.raw";
  http.setTimeout(BRIDGE_HTTP_TIMEOUT_MS);
  if (!http.begin(client, url)) return false;
  int code = http.GET();
  if (code != HTTP_CODE_OK) {
    http.end();
    return false;
  }
  WiFiClient *stream = http.getStreamPtr();
  const size_t rowBytes = (size_t)MUSIC_TEXT_W * 2;
  bool ok = true;
  for (int r = 0; r < MUSIC_TEXT_H; r++) {
    int got = stream->readBytes((uint8_t *)rowBuf, rowBytes);
    if (got != (int)rowBytes) {
      ok = false;
      break;
    }
    tft.pushImage(MUSIC_TEXT_X, MUSIC_TEXT_Y + r, MUSIC_TEXT_W, 1, rowBuf);
    yield();
  }
  http.end();
  return ok;
}

// ASCII-only fallback if the strip fetch fails (CJK will stay blank, but at
// least latin titles show something).
void drawMusicTextFallback() {
  tft.fillRect(MUSIC_TEXT_X, MUSIC_TEXT_Y, MUSIC_TEXT_W, MUSIC_TEXT_H, TFT_BLACK);
  tft.setTextDatum(TC_DATUM);
  tft.setTextColor(TFT_WHITE, TFT_BLACK);
  String title = musicTitle.length() ? musicTitle : "No Music";
  tft.drawString(fitText(title, 216, 2), SCREEN_CX, MUSIC_TEXT_Y + 4, 2);
  tft.setTextColor(TFT_LIGHTGREY, TFT_BLACK);
  tft.drawString(fitText(musicArtist, 216, 2), SCREEN_CX, MUSIC_TEXT_Y + 24, 2);
}

// Regions repaint independently: cover / text strip only when their rev
// changes, progress bar + time on every poll (partial fill, no flicker
// elsewhere).
void drawMusicScreen(bool coverChanged, bool textChanged) {
  if (!musicChromeDrawn) {
    tft.fillScreen(TFT_BLACK);
    coverChanged = true;
    textChanged = true;
    musicChromeDrawn = true;
  }
  if (coverChanged) {
    if (!drawMusicCoverFromBridge()) drawMusicCoverPlaceholder();
  }
  if (textChanged) {
    if (!drawMusicTextFromBridge()) drawMusicTextFallback();
  }

  const int bx = 20, by = 204, bw = 200, bh = 8;
  tft.fillRect(0, by - 2, SCREEN_W, SCREEN_H - by + 2, TFT_BLACK);
  tft.fillRect(bx, by, bw, bh, TFT_DARKGREY);
  float progress = musicDuration > 0 ? (float)musicElapsed / (float)musicDuration : 0;
  if (progress < 0) progress = 0;
  if (progress > 1) progress = 1;
  uint16_t color = musicPlaying ? TFT_GREEN : TFT_LIGHTGREY;
  tft.fillRect(bx, by, (int)(bw * progress), bh, color);
  tft.setTextDatum(TC_DATUM);
  tft.setTextColor(TFT_LIGHTGREY, TFT_BLACK);
  tft.drawString(timeText(musicElapsed) + " / " + timeText(musicDuration), SCREEN_CX, 220, 1);
}

void applyMusicJson(JsonObject doc) {
  musicTitle = doc["title"] | "";
  musicArtist = doc["artist"] | "";
  musicAlbum = doc["album"] | "";
  musicPlaying = doc["playing"] | false;
  statusMusicPlaying = musicPlaying;
  musicElapsed = doc["elapsed"] | 0;
  musicDuration = doc["duration"] | 0;
  musicHasArtwork = doc["has_artwork"] | false;
  int rev = doc["artwork_rev"] | -1;
  bool coverChanged = rev != musicArtworkRev;
  musicArtworkRev = rev;
  int tRev = doc["text_rev"] | -1;
  bool textChanged = tRev != musicTextRev;
  musicTextRev = tRev;
  if (effectiveMode() == MODE_MUSIC) drawMusicScreen(coverChanged, textChanged);
}

void pollMusic() {
  if (usbBridgeActive()) return;
  if (WiFi.status() != WL_CONNECTED || bridgeHost.length() == 0) return;
  WiFiClient client;
  HTTPClient http;
  String url = "http://" + bridgeHost + "/music";
  http.setTimeout(BRIDGE_HTTP_TIMEOUT_MS);
  if (!http.begin(client, url)) return;
  int code = http.GET();
  if (code == HTTP_CODE_OK) {
    JsonDocument doc;
    if (!deserializeJson(doc, http.getString())) {
      applyMusicJson(doc.as<JsonObject>());
    }
  }
  http.end();
}

// ---------- stock watchlist ----------

bool applyStockJson(JsonObject doc) {
  int oldPageCount = stockCount > 0 ? (stockCount + STOCK_ROWS_PER_PAGE - 1) / STOCK_ROWS_PER_PAGE : 1;
  JsonArray rows = doc["stocks"];
  stockCount = 0;
  for (JsonObject row : rows) {
    if (stockCount >= MAX_STOCKS) break;
    stocks[stockCount].code = row["code"] | "";
    stocks[stockCount].price = row["price"] | "";
    stocks[stockCount].pct = row["pct"] | "";
    stocks[stockCount].up = row["up"] | 0;
    stockCount++;
  }
  stockNamesRev = doc["names_rev"] | -1;
  int pageCount = stockCount > 0 ? (stockCount + STOCK_ROWS_PER_PAGE - 1) / STOCK_ROWS_PER_PAGE : 1;
  if (stockPage >= pageCount) stockPage = 0;
  if (pageCount != oldPageCount) stockChromeDrawn = false;
  stockEverLoaded = true;
  stockDirty = true;
  return true;
}

void pollStock() {
  if (usbBridgeActive() || WiFi.status() != WL_CONNECTED || bridgeHost.length() == 0) return;
  WiFiClient client;
  HTTPClient http;
  if (!http.begin(client, "http://" + bridgeHost + "/stock")) return;
  http.setTimeout(BRIDGE_HTTP_TIMEOUT_MS);
  if (http.GET() == HTTP_CODE_OK) {
    JsonDocument doc;
    if (!deserializeJson(doc, http.getString())) applyStockJson(doc.as<JsonObject>());
  }
  http.end();
}

bool drawStockNamesFromBridge() {
  if (WiFi.status() != WL_CONNECTED || bridgeHost.length() == 0) return false;
  WiFiClient client;
  HTTPClient http;
  if (!http.begin(client, "http://" + bridgeHost + "/stock/names.raw")) return false;
  http.setTimeout(BRIDGE_HTTP_TIMEOUT_MS);
  if (http.GET() != HTTP_CODE_OK) { http.end(); return false; }
  WiFiClient *stream = http.getStreamPtr();
  bool ok = true;
  const size_t rowBytes = (size_t)STOCK_NAME_W * 2;
  for (int stock = 0; stock < MAX_STOCKS && ok; stock++) {
    for (int row = 0; row < STOCK_NAME_H; row++) {
      if (stream->readBytes((uint8_t *)rowBuf, rowBytes) != (int)rowBytes) { ok = false; break; }
      int pageRow = stock - stockPage * STOCK_ROWS_PER_PAGE;
      if (effectiveMode() == MODE_STOCK && stock < stockCount
          && pageRow >= 0 && pageRow < STOCK_ROWS_PER_PAGE)
        tft.pushImage(70, 6 + pageRow * 54 + row, STOCK_NAME_W, 1, rowBuf);
      yield();
    }
  }
  http.end();
  return ok;
}

void drawStockScreen(bool force = false) {
  if (force || !stockChromeDrawn) {
    tft.fillScreen(TFT_BLACK);
    stockChromeDrawn = true;
    stockNamesDrawnRev = -1;
    for (int i = 0; i < STOCK_ROWS_PER_PAGE; i++) { stockLastCode[i] = "\x01"; stockLastValue[i] = "\x01"; }
    tft.setTextDatum(TC_DATUM);
    tft.setTextColor(TFT_DARKGREY, TFT_BLACK);
    int pageCount = stockCount > 0 ? (stockCount + STOCK_ROWS_PER_PAGE - 1) / STOCK_ROWS_PER_PAGE : 1;
    String footer = pageCount > 1 ? "STOCKS " + String(stockPage + 1) + "/" + String(pageCount) : "STOCKS";
    tft.drawString(footer, SCREEN_CX, 228, 1);
  }
  stockDirty = false;
  if (stockCount == 0) {
    tft.fillRect(0, 0, SCREEN_W, 220, TFT_BLACK);
    tft.setTextDatum(TC_DATUM);
    tft.setTextColor(TFT_LIGHTGREY, TFT_BLACK);
    tft.drawString(stockEverLoaded ? "No valid quotes" : "Waiting for bridge...", SCREEN_CX, 104, 2);
    return;
  }
  int pageStart = stockPage * STOCK_ROWS_PER_PAGE;
  for (int i = 0; i < STOCK_ROWS_PER_PAGE; i++) {
    int y = 6 + i * 54;
    int stockIndex = pageStart + i;
    bool has = stockIndex < stockCount;
    String code = has ? stocks[stockIndex].code : "";
    if (code != stockLastCode[i]) {
      stockLastCode[i] = code;
      stockNamesDrawnRev = -1;
      tft.fillRect(0, y, SCREEN_W, 17, TFT_BLACK);
      if (has) { tft.setTextDatum(TL_DATUM); tft.setTextColor(TFT_DARKGREY, TFT_BLACK); tft.drawString(code, 14, y, 2); }
    }
    String value = has ? stocks[stockIndex].price + "|" + stocks[stockIndex].pct + "|" + String(stocks[stockIndex].up) : "";
    if (value != stockLastValue[i]) {
      stockLastValue[i] = value;
      tft.fillRect(0, y + 21, SCREEN_W, 33, TFT_BLACK);
      if (has) {
        tft.setTextDatum(TL_DATUM); tft.setTextColor(TFT_WHITE, TFT_BLACK);
        tft.drawString(stocks[stockIndex].price, 14, y + 22, 4);
        uint16_t color = stocks[stockIndex].up > 0 ? TFT_RED : stocks[stockIndex].up < 0 ? TFT_GREEN : TFT_LIGHTGREY;
        tft.setTextDatum(TR_DATUM); tft.setTextColor(color, TFT_BLACK);
        tft.drawString(stocks[stockIndex].pct, 226, y + 22, 4);
      }
    }
  }
  if (!usbBridgeActive() && stockNamesRev >= 0 && stockNamesDrawnRev != stockNamesRev && drawStockNamesFromBridge())
    stockNamesDrawnRev = stockNamesRev;
}

// ---------- weather clock ----------

bool applyWeatherJson(JsonObject doc) {
  weatherStatus.temp = doc["temp"] | 0.0f;
  weatherStatus.high = doc["high"] | 0.0f;
  weatherStatus.low = doc["low"] | 0.0f;
  weatherStatus.pm25 = doc["pm25"] | -1.0f;
  weatherStatus.humidity = doc["humidity"] | 0;
  weatherStatus.icon = doc["icon"] | -1;
  String animation = doc["animation"] | "robot";
  weatherStatus.animation = animation == "house" ? 1 : animation == "plant" ? 2
    : animation == "off" ? 3 : animation == "pet" ? 4 : 0;
  weatherStatus.epochUtc = doc["epoch_utc"] | 0UL;
  weatherStatus.utcOffsetS = doc["utc_offset_s"] | 0;
  weatherStatus.stale = doc["stale"] | false;
  weatherStatus.textRev = doc["text_rev"] | -1;
  weatherStatus.dateCenterX = doc["date_center_x"] | (WEATHER_DATE_W / 2);
  weatherStatus.headerCenterX = doc["header_center_x"] | (WEATHER_HEADER_W / 2);
  weatherStatus.rangeY = constrain((int)(doc["range_y"] | 34), 32, 35);
  weatherStatus.loaded = weatherStatus.epochUtc > 0;
  weatherSyncMs = millis();
  if (effectiveMode() == MODE_WEATHER) drawWeatherScreen(false);
  return weatherStatus.loaded;
}

void pollWeather() {
  if (usbBridgeActive() || WiFi.status() != WL_CONNECTED || bridgeHost.length() == 0) return;
  WiFiClient client;
  HTTPClient http;
  if (!http.begin(client, "http://" + bridgeHost + "/weather")) return;
  http.setTimeout(BRIDGE_HTTP_TIMEOUT_MS);
  if (http.GET() == HTTP_CODE_OK) {
    JsonDocument doc;
    if (!deserializeJson(doc, http.getString())) applyWeatherJson(doc.as<JsonObject>());
  }
  http.end();
}

bool drawWeatherLabelFromBridge(const char *path, int width, int height, int x, int y) {
  if (WiFi.status() != WL_CONNECTED || bridgeHost.length() == 0) return false;
  WiFiClient client;
  HTTPClient http;
  if (!http.begin(client, "http://" + bridgeHost + path)) return false;
  http.setTimeout(BRIDGE_HTTP_TIMEOUT_MS);
  if (http.GET() != HTTP_CODE_OK) { http.end(); return false; }
  WiFiClient *stream = http.getStreamPtr();
  const size_t rowBytes = (size_t)width * 2;
  bool ok = true;
  for (int row = 0; row < height; row++) {
    if (stream->readBytes((uint8_t *)rowBuf, rowBytes) != (int)rowBytes) { ok = false; break; }
    if (effectiveMode() == MODE_WEATHER) tft.pushImage(x, y + row, width, 1, rowBuf);
    yield();
  }
  http.end();
  return ok;
}

bool drawWeatherHeaderFromBridge() {
  tft.fillRect(0, 0, WEATHER_AIR_X, 28, TFT_BLACK);
  return drawWeatherLabelFromBridge("/weather/header.raw", WEATHER_HEADER_W, WEATHER_HEADER_H, weatherHeaderX(), WEATHER_HEADER_Y);
}

bool drawWeatherDateFromBridge() {
  return drawWeatherLabelFromBridge("/weather/date.raw", WEATHER_DATE_W, WEATHER_DATE_H, WEATHER_DATE_X, WEATHER_DATE_Y);
}

bool drawWeatherAirFromBridge() {
  return drawWeatherLabelFromBridge("/weather/air.raw", WEATHER_AIR_W, WEATHER_AIR_H, WEATHER_AIR_X, WEATHER_AIR_Y);
}

void epochToLocal(uint32_t utc, int offset, int &year, int &month, int &day, int &hour, int &minute, int &second, int &weekday) {
  int64_t total = (int64_t)utc + offset;
  int64_t days = total / 86400;
  int64_t rest = total % 86400;
  if (rest < 0) { rest += 86400; days--; }
  hour = rest / 3600; minute = (rest % 3600) / 60; second = rest % 60;
  weekday = (int)((days + 4) % 7); if (weekday < 0) weekday += 7;
  int64_t z = days + 719468;
  int era = (z >= 0 ? z : z - 146096) / 146097;
  unsigned doe = (unsigned)(z - era * 146097);
  unsigned yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
  year = (int)yoe + era * 400;
  unsigned doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
  unsigned mp = (5 * doy + 2) / 153;
  day = doy - (153 * mp + 2) / 5 + 1;
  month = mp + (mp < 10 ? 3 : -9);
  year += month <= 2;
}

uint32_t currentBridgeUtc() {
  uint32_t bridgeNow = bridgeEpochUtc > 0
    ? bridgeEpochUtc + (millis() - bridgeClockSyncMs) / 1000 : 0;
  if (bridgeNow > 0 && !hostGoingAway && !bridgeStale()) return bridgeNow;
  time_t ntpNow = time(nullptr);
  if (ntpNow >= NTP_VALID_AFTER) return (uint32_t)ntpNow;
  // Hold over from the last trusted source when the internet is temporarily
  // unavailable. ESP8266 millis() may drift, but the clock keeps progressing.
  if (bridgeNow > 0) return bridgeNow;
  if (weatherStatus.loaded) return weatherStatus.epochUtc + (millis() - weatherSyncMs) / 1000;
  return 0;
}

const char *currentTimeSource() {
  if (bridgeEpochUtc > 0 && !hostGoingAway && !bridgeStale()) return "bridge";
  if (time(nullptr) >= NTP_VALID_AFTER) return "ntp";
  if (bridgeEpochUtc > 0 || weatherStatus.loaded) return "holdover";
  return "none";
}

void drawWeekdayGlyph(int glyph, int x, int y, uint16_t color, int size = 18) {
  for (int row = 0; row < size; row++) {
    int sourceRow = row * 18 / size;
    uint32_t mask = pgm_read_dword(&WEEKDAY_GLYPHS[glyph][sourceRow]);
    int runStart = -1;
    for (int col = 0; col <= size; col++) {
      int sourceCol = col < size ? col * 18 / size : 18;
      bool set = col < size && (mask & (1UL << (17 - sourceCol)));
      if (set && runStart < 0) runStart = col;
      if (!set && runStart >= 0) {
        int width = min(size - runStart, col - runStart + 1);
        tft.fillRect(x + runStart, y + row, width, 1, color);
        runStart = -1;
      }
    }
  }
}

void drawScreenSaverDigit(int digit, int x, int y, uint16_t color) {
  static const uint8_t masks[10] = { 0x3f, 0x06, 0x5b, 0x4f, 0x66, 0x6d, 0x7d, 0x07, 0x7f, 0x6f };
  const int w = 42, h = 76, t = 9, half = h / 2;
  uint8_t mask = masks[constrain(digit, 0, 9)];
  if (mask & 0x01) tft.fillRoundRect(x + t, y, w - t * 2, t, 3, color);
  if (mask & 0x02) tft.fillRoundRect(x + w - t, y + t, t, half - t, 3, color);
  if (mask & 0x04) tft.fillRoundRect(x + w - t, y + half, t, half - t, 3, color);
  if (mask & 0x08) tft.fillRoundRect(x + t, y + h - t, w - t * 2, t, 3, color);
  if (mask & 0x10) tft.fillRoundRect(x, y + half, t, half - t, 3, color);
  if (mask & 0x20) tft.fillRoundRect(x, y + t, t, half - t, 3, color);
  if (mask & 0x40) tft.fillRoundRect(x + t, y + half - t / 2, w - t * 2, t, 3, color);
}

void drawHostOfflineMark() {
  const int w = 56, h = 18;
  const int x = SCREEN_W - w - 3, y = SCREEN_H - h - 3;
  tft.fillRect(x - 1, y - 1, w + 2, h + 2, TFT_BLACK);
  if (!(hostGoingAway || bridgeStale())) return;
  const uint16_t color = TFT_ORANGE;
  tft.drawRoundRect(x, y, w, h, 3, color);
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(color, TFT_BLACK);
  tft.drawString("PC OFF", x + w / 2, y + h / 2, 1);
}

void drawScreenSaver(bool force) {
  uint32_t utc = currentBridgeUtc();
  if (force) {
    tft.fillScreen(TFT_BLACK);
    screenSaverOldX = -1;
    screenSaverLastTick = -1;
  }
  if (utc == 0) {
    if (screenSaverOldX < 0) {
      tft.setTextDatum(MC_DATUM);
      tft.setTextColor(TFT_DARKGREY, TFT_BLACK);
      tft.drawString("AI CLOCK", SCREEN_W / 2, SCREEN_H / 2, 4);
      screenSaverOldX = 0;
    }
    drawHostOfflineMark();
    return;
  }

  int year, month, day, hour, minute, second, weekday;
  int offset = bridgeEpochUtc > 0 || !weatherStatus.loaded
    ? bridgeUtcOffsetS : weatherStatus.utcOffsetS;
  epochToLocal(utc, offset, year, month, day, hour, minute, second, weekday);
  long refreshTick = utc / 5;
  if (!force && refreshTick == screenSaverLastTick) return;

  char dateBuf[6];
  snprintf(dateBuf, sizeof(dateBuf), "%02d-%02d", month, day);
  tft.setTextDatum(TL_DATUM);
  const int dateFont = 4;
  const int dateTextHeight = tft.fontHeight(dateFont);
  const int dateW = tft.textWidth(dateBuf, dateFont) + 1;
  const int weekdayGlyphSize = dateTextHeight;
  const int weekdayW = weekdayGlyphSize * 2 + 2;
  const int dateLineW = dateW + 10 + weekdayW;
  const int timeW = 204;
  const int calendarTop = 86;
  int groupW = max(timeW, dateLineW);
  int groupH = calendarTop + dateTextHeight;
  int rangeX = max(1, SCREEN_W - groupW - 12);
  // Keep a fixed bottom status lane clear for the explicit PC OFF badge.
  int rangeY = max(1, SCREEN_H - groupH - 38);
  uint32_t motionTick = utc / 5;
  int phaseX = (motionTick * 2) % (rangeX * 2);
  int phaseY = motionTick % (rangeY * 2);
  int x = 6 + (phaseX <= rangeX ? phaseX : rangeX * 2 - phaseX);
  int y = 12 + (phaseY <= rangeY ? phaseY : rangeY * 2 - phaseY);

  if (screenSaverOldX >= 0)
    tft.fillRect(screenSaverOldX - 2, screenSaverOldY - 2, screenSaverOldW + 4, screenSaverOldH + 4, TFT_BLACK);
  int timeX = x + (groupW - timeW) / 2;
  const int digitX[] = { timeX, timeX + 47, timeX + 115, timeX + 162 };
  const int digitValue[] = { hour / 10, hour % 10, minute / 10, minute % 10 };
  const uint16_t dateColor = 0xC618; // soft white-grey
  const uint16_t accentColor = TFT_YELLOW;
  for (int i = 0; i < 4; i++) drawScreenSaverDigit(digitValue[i], digitX[i], y, TFT_CYAN);
  // Exact centre of the 26px gap between the hour and minute groups, and
  // vertically symmetric around the 76px digit centre (y + 38).
  tft.fillCircle(timeX + 102, y + 26, 5, accentColor);
  tft.fillCircle(timeX + 102, y + 50, 5, accentColor);
  // Centre the date under the actually lit clock, not the four fixed digit
  // cells. From 10:00-19:59 the leading "1" starts 33px inside its cell,
  // which otherwise makes the visible clock look right-shifted.
  int firstDigitVisibleLeft = hour / 10 == 1 ? 33 : 0;
  int timeVisibleCenter = timeX + (firstDigitVisibleLeft + timeW) / 2;
  int dateX = timeVisibleCenter - dateLineW / 2;
  tft.setTextDatum(TL_DATUM);
  drawBoldString(dateBuf, dateX, y + calendarTop, dateFont, dateColor);
  int weekdayX = dateX + dateW + 10;
  drawWeekdayGlyph(0, weekdayX, y + calendarTop, dateColor, weekdayGlyphSize);
  drawWeekdayGlyph(weekday + 1, weekdayX + weekdayGlyphSize + 2, y + calendarTop,
                   accentColor, weekdayGlyphSize);
  screenSaverOldX = x;
  screenSaverOldY = y;
  screenSaverOldW = groupW;
  screenSaverOldH = groupH;
  screenSaverLastTick = refreshTick;
  drawHostOfflineMark();
}

void drawWeatherDigit(int digit, int x, int y, uint16_t color) {
  static const uint8_t masks[10] = { 0x3f, 0x06, 0x5b, 0x4f, 0x66, 0x6d, 0x7d, 0x07, 0x7f, 0x6f };
  const int w = 16, h = 30, t = 3, half = h / 2;
  uint8_t mask = masks[constrain(digit, 0, 9)];
  if (mask & 0x01) tft.fillRoundRect(x + t, y, w - t * 2, t, 1, color);
  if (mask & 0x02) tft.fillRoundRect(x + w - t, y + t, t, half - t, 1, color);
  if (mask & 0x04) tft.fillRoundRect(x + w - t, y + half, t, half - t, 1, color);
  if (mask & 0x08) tft.fillRoundRect(x + t, y + h - t, w - t * 2, t, 1, color);
  if (mask & 0x10) tft.fillRoundRect(x, y + half, t, half - t, 1, color);
  if (mask & 0x20) tft.fillRoundRect(x, y + t, t, half - t, 1, color);
  if (mask & 0x40) tft.fillRoundRect(x + t, y + half - 1, w - t * 2, t, 1, color);
}

void drawWeatherClock() {
  if (!weatherStatus.loaded) return;
  // Prefer the frequently refreshed bridge clock. The weather timestamp can
  // legitimately be old when the network is down and cached data is shown.
  uint32_t utc = currentBridgeUtc();
  int year, month, day, hour, minute, second, weekday;
  epochToLocal(utc, weatherStatus.utcOffsetS, year, month, day, hour, minute, second, weekday);
  String hourText = String(hour < 10 ? "0" : "") + String(hour);
  String minuteText = String(minute < 10 ? "0" : "") + String(minute);
  const int hourMinuteGap = 10;
  const int secondGap = 8;
  const int secondDigitStep = 20;
  const int secondWidth = 36;
  int hourWidth = tft.textWidth(hourText, 7);
  int minuteWidth = tft.textWidth(minuteText, 7);
  int hourMinuteWidth = hourWidth + hourMinuteGap + minuteWidth;
  int groupWidth = hourMinuteWidth + secondGap + secondWidth;
  // Font 7 has more visible side-bearing on the left than the hand-drawn
  // seconds have on the right, so geometric centring looks right-heavy.
  const int visualCenterOffset = -4;
  int timeX = (SCREEN_W - groupWidth) / 2 + visualCenterOffset;
  int secondX = timeX + hourMinuteWidth + secondGap;
  if (hour != weatherLastHour || minute != weatherLastMinute) {
    tft.fillRect(0, 52, SCREEN_W, 68, TFT_BLACK);
    tft.setTextDatum(TL_DATUM);
    tft.setTextColor(TFT_WHITE, TFT_BLACK);
    tft.drawString(hourText, timeX, 57, 7);
    tft.setTextColor(TFT_ORANGE, TFT_BLACK);
    tft.drawString(minuteText, timeX + hourWidth + hourMinuteGap, 57, 7);
    weatherLastHour = hour;
    weatherLastMinute = minute;
    weatherLastSecond = -1;
  }
  if (second != weatherLastSecond) {
    tft.fillRect(secondX - 2, 70, secondWidth + 4, 39, TFT_BLACK);
    drawWeatherDigit(second / 10, secondX, 75, TFT_LIGHTGREY);
    drawWeatherDigit(second % 10, secondX + secondDigitStep, 75, TFT_LIGHTGREY);
    weatherLastSecond = second;
  }
  int stale = weatherStatus.stale ? 1 : 0;
  if (stale != weatherLastStale) {
    tft.fillRect(232, 39, 7, 7, TFT_BLACK);
    weatherLastStale = stale;
  }
}

void drawWeatherRobotAnimation() {
  // A tiny weather buddy: its colour follows the condition, it floats on a
  // cloud, blinks, and sends a moving antenna pulse. It is deliberately more
  // characterful than a second copy of the weather icon in the upper corner.
  const int x = 184, y = 188 + ((weatherAnimFrame % 6) == 0 ? -2 : (weatherAnimFrame % 6) == 3 ? 2 : 0);
  const int phase = weatherAnimFrame % 12;
  const uint16_t body = weatherStatus.icon == 4 ? TFT_CYAN
                      : weatherStatus.icon == 5 ? TFT_WHITE
                      : weatherStatus.icon == 6 ? TFT_ORANGE
                      : weatherStatus.icon == 3 ? TFT_LIGHTGREY : TFT_YELLOW;
  // drifting cloud
  int cloud = (phase % 4) * 2;
  tft.fillCircle(156 + cloud, WEATHER_ANIM_BOTTOM - 8, 8, TFT_DARKGREY);
  tft.fillCircle(168 + cloud, WEATHER_ANIM_BOTTOM - 13, 11, TFT_DARKGREY);
  tft.fillCircle(181 + cloud, WEATHER_ANIM_BOTTOM - 8, 8, TFT_DARKGREY);
  tft.fillRoundRect(149 + cloud, WEATHER_ANIM_BOTTOM - 8, 40, 8, 4, TFT_DARKGREY);
  // rounded robot head and a live antenna
  tft.fillRoundRect(x - 22, y - 22, 44, 36, 10, body);
  tft.drawLine(x, y - 22, x + (phase < 6 ? 5 : -5), y - 31, body);
  tft.fillCircle(x + (phase < 6 ? 5 : -5), y - 33, 3, phase % 3 == 0 ? TFT_MAGENTA : body);
  bool blink = phase == 0 || phase == 1;
  tft.setTextColor(TFT_BLACK, body);
  if (blink) {
    tft.drawFastHLine(x - 13, y - 6, 8, TFT_BLACK);
    tft.drawFastHLine(x + 5, y - 6, 8, TFT_BLACK);
  } else {
    tft.fillCircle(x - 9, y - 6, 4, TFT_BLACK);
    tft.fillCircle(x + 9, y - 6, 4, TFT_BLACK);
    tft.fillCircle(x - 8, y - 7, 1, TFT_WHITE);
    tft.fillCircle(x + 10, y - 7, 1, TFT_WHITE);
  }
  tft.drawArc(x, y + 3, 9, 6, 25, 155, TFT_BLACK, body);
  if (weatherStatus.icon == 4 || weatherStatus.icon == 6) {
    for (int i = 0; i < 3; i++) {
      int dropY = 158 + ((phase * 5 + i * 17) % 28);
      tft.drawLine(151 + i * 13, dropY, 148 + i * 13, dropY + 5, TFT_CYAN);
    }
  } else if (weatherStatus.icon == 5) {
    for (int i = 0; i < 4; i++) {
      int sx = 150 + ((i * 19 + phase * 3) % 78);
      tft.drawFastHLine(sx - 2, 159 + i * 9, 5, TFT_WHITE);
      tft.drawFastVLine(sx, 156 + i * 9, 7, TFT_WHITE);
    }
  } else {
    tft.fillCircle(218, 162, 5 + (phase % 3), body);
    for (int i = 0; i < 4; i++) {
      float a = i * 1.57f + phase * 0.12f;
      tft.drawLine(218 + (int)(cos(a) * 9), 162 + (int)(sin(a) * 9),
                   218 + (int)(cos(a) * 13), 162 + (int)(sin(a) * 13), body);
    }
  }
}

void drawWeatherHouseAnimation() {
  const int phase = weatherAnimFrame % 12;
  // ground and a compact house
  tft.drawFastHLine(153, WEATHER_ANIM_BOTTOM, 78, TFT_DARKGREY);
  tft.fillRect(166, 190, 48, 33, TFT_ORANGE);
  tft.fillTriangle(158, 192, 190, 167, 222, 192, TFT_RED);
  tft.fillRect(173, 199, 12, 24, TFT_BROWN);
  tft.fillRect(194, 198, 13, 12, (phase < 6) ? TFT_YELLOW : TFT_ORANGE);
  tft.drawRect(194, 198, 13, 12, TFT_WHITE);
  tft.drawFastVLine(200, 198, 12, TFT_WHITE);
  tft.drawFastHLine(194, 204, 13, TFT_WHITE);
  // chimney smoke drifts instead of leaving static pixels behind.
  tft.fillRect(205, 170, 7, 15, TFT_DARKGREY);
  int drift = phase / 3;
  tft.fillCircle(211 + drift, 164, 3, TFT_LIGHTGREY);
  tft.fillCircle(215 + drift, 158, 2, TFT_DARKGREY);
  if (weatherStatus.icon == 4 || weatherStatus.icon == 6) {
    for (int i = 0; i < 5; i++) {
      int dropY = 158 + ((phase * 4 + i * 13) % 30);
      tft.drawLine(153 + i * 16, dropY, 150 + i * 16, dropY + 5, TFT_CYAN);
    }
  } else if (weatherStatus.icon == 5) {
    for (int i = 0; i < 5; i++) {
      int sx = 153 + ((i * 17 + phase * 3) % 74);
      int sy = 158 + ((i * 11 + phase * 2) % 29);
      tft.drawPixel(sx, sy, TFT_WHITE); tft.drawPixel(sx + 1, sy, TFT_WHITE);
    }
  } else {
    tft.fillCircle(224, 163, 5 + (phase % 2), TFT_YELLOW);
  }
}

void drawWeatherPlantAnimation() {
  const int phase = weatherAnimFrame % 12;
  const int sway = phase < 6 ? phase / 2 : (11 - phase) / 2;
  const int stemX = 188 + sway - 1;
  // pot
  tft.fillRoundRect(171, 204, 36, 8, 3, TFT_ORANGE);
  tft.fillTriangle(175, 211, 203, 211, 199, WEATHER_ANIM_BOTTOM, TFT_BROWN);
  tft.fillTriangle(175, 211, 199, WEATHER_ANIM_BOTTOM, 179, WEATHER_ANIM_BOTTOM, TFT_BROWN);
  // swaying stem and leaves
  tft.drawLine(189, 204, stemX, 171, TFT_GREEN);
  tft.fillEllipse(stemX - 10, 177, 11, 6, TFT_GREEN);
  tft.fillEllipse(stemX + 1, 185, 12, 6, TFT_GREEN);
  tft.fillEllipse(stemX - 9, 193, 10, 5, TFT_DARKGREEN);
  if (weatherStatus.icon == 4 || weatherStatus.icon == 6) {
    for (int i = 0; i < 4; i++) {
      int dropY = 154 + ((phase * 5 + i * 15) % 38);
      tft.drawLine(154 + i * 21, dropY, 152 + i * 21, dropY + 5, TFT_CYAN);
    }
  } else if (weatherStatus.icon == 5) {
    for (int i = 0; i < 5; i++) {
      int sx = 153 + ((i * 18 + phase * 2) % 75);
      tft.fillCircle(sx, 157 + i * 7, 1, TFT_WHITE);
    }
  } else {
    tft.fillCircle(219, 162, 6 + (phase % 2), TFT_YELLOW);
    for (int i = 0; i < 4; i++) {
      float a = i * 1.57f + phase * 0.1f;
      tft.drawLine(219 + (int)(cos(a) * 9), 162 + (int)(sin(a) * 9),
                   219 + (int)(cos(a) * 13), 162 + (int)(sin(a) * 13), TFT_YELLOW);
    }
  }
}

void drawWeatherPetAnimation() {
  const int phase = weatherAnimFrame % 12;
  const int petY = -6; // align the paws with the humidity row's lower edge
  const bool blink = phase == 0 || phase == 1;
  const uint16_t fur = weatherStatus.icon == 5 ? TFT_LIGHTGREY : TFT_ORANGE;
  // Only erase pixels that can move. Clearing the full 88x76 area before
  // repainting the large pet made the black intermediate frame visible.
  tft.fillRect(148, 152, 88, 28, TFT_BLACK);
  tft.fillRect(207, 193 + petY, 22, 24, TFT_BLACK);
  // Curled tail swishes behind the body.
  int tailLift = phase < 6 ? phase / 2 : (11 - phase) / 2;
  tft.drawLine(207, 210 + petY, 219, 207 + petY - tailLift, fur);
  tft.drawLine(219, 207 + petY - tailLift, 224, 198 + petY + tailLift, fur);
  tft.fillEllipse(190, 208 + petY, 20, 16, fur);
  // Head, ears and paws.
  tft.fillTriangle(173, 181 + petY, 178, 166 + petY, 184, 181 + petY, fur);
  tft.fillTriangle(196, 181 + petY, 203, 166 + petY, 207, 183 + petY, fur);
  tft.fillRoundRect(174, 176 + petY, 34, 29, 10, fur);
  tft.fillEllipse(180, WEATHER_ANIM_BOTTOM - 4 + petY, 9, 4, TFT_LIGHTGREY);
  tft.fillEllipse(201, WEATHER_ANIM_BOTTOM - 4 + petY, 9, 4, TFT_LIGHTGREY);
  // Face alternates between open eyes and a blink.
  if (blink) {
    tft.drawFastHLine(180, 187 + petY, 7, TFT_BLACK);
    tft.drawFastHLine(196, 187 + petY, 7, TFT_BLACK);
  } else {
    tft.fillCircle(183, 187 + petY, 3, TFT_BLACK);
    tft.fillCircle(199, 187 + petY, 3, TFT_BLACK);
    tft.drawPixel(184, 186 + petY, TFT_WHITE); tft.drawPixel(200, 186 + petY, TFT_WHITE);
  }
  tft.fillTriangle(188, 193 + petY, 194, 193 + petY, 191, 197 + petY, TFT_MAGENTA);
  tft.drawLine(191, 197 + petY, 188, 200 + petY, TFT_BLACK);
  tft.drawLine(191, 197 + petY, 194, 200 + petY, TFT_BLACK);
  // Weather-reactive detail around the pet.
  if (weatherStatus.icon == 4) {
    for (int i = 0; i < 4; i++) {
      int dropY = 154 + ((phase * 5 + i * 17) % 24);
      tft.drawLine(153 + i * 22, dropY, 150 + i * 22, dropY + 5, TFT_CYAN);
    }
  } else if (weatherStatus.icon == 5) {
    for (int i = 0; i < 5; i++) {
      int sx = 153 + ((i * 18 + phase * 3) % 75);
      tft.fillCircle(sx, 155 + (i % 3) * 8, 1, TFT_WHITE);
    }
  } else if (weatherStatus.icon == 6) {
    uint16_t flash = phase == 0 || phase == 6 ? TFT_WHITE : TFT_YELLOW;
    tft.drawLine(222, 154, 216, 166, flash);
    tft.drawLine(216, 166, 222, 164, flash);
    tft.drawLine(222, 164, 217, 176, flash);
  } else {
    tft.fillCircle(220, 159, 5 + (phase % 2), TFT_YELLOW);
  }
}

void drawWeatherAnimation() {
  bool animationChanged = weatherStatus.animation != weatherLastAnimation;
  if (weatherStatus.animation != 4 || animationChanged) {
    tft.fillRect(148, 152, 88, 76, TFT_BLACK);
  }
  weatherLastAnimation = weatherStatus.animation;
  if (weatherStatus.animation == 1) drawWeatherHouseAnimation();
  else if (weatherStatus.animation == 2) drawWeatherPlantAnimation();
  else if (weatherStatus.animation == 4) drawWeatherPetAnimation();
  else if (weatherStatus.animation != 3) drawWeatherRobotAnimation();
}

void drawWeatherScreen(bool force) {
  if (force || !weatherChromeDrawn) {
    tft.fillScreen(TFT_BLACK);
    weatherChromeDrawn = true;
    weatherTextDrawnRev = -1;
    weatherLastHour = weatherLastMinute = weatherLastSecond = weatherLastStale = -1;
  }
  if (!weatherStatus.loaded) {
    tft.setTextDatum(TC_DATUM); tft.setTextColor(TFT_LIGHTGREY, TFT_BLACK);
    tft.drawString("Set weather city in tray", SCREEN_CX, 102, 2);
    return;
  }
  bool textNeedsUpdate = !usbBridgeActive() && weatherStatus.textRev >= 0
    && weatherTextDrawnRev != weatherStatus.textRev;
  if (textNeedsUpdate) textNeedsUpdate = drawWeatherHeaderFromBridge() && drawWeatherDateFromBridge();
  tft.fillRect(0, 28, WEATHER_AIR_X - 2, 23, TFT_BLACK);
  int high = (int)(weatherStatus.high + (weatherStatus.high >= 0 ? 0.5f : -0.5f));
  int low = (int)(weatherStatus.low + (weatherStatus.low >= 0 ? 0.5f : -0.5f));
  String lowText = "L " + String(low) + "C";
  String highText = "H " + String(high) + "C";
  const int rangeGap = 10;
  int lowWidth = tft.textWidth(lowText, 2);
  int highWidth = tft.textWidth(highText, 2);
  int rangeX = max(2, weatherHeaderCenter() - (lowWidth + rangeGap + highWidth) / 2);
  tft.setTextDatum(TL_DATUM);
  tft.setTextColor(TFT_CYAN, TFT_BLACK);
  tft.drawString(lowText, rangeX, weatherStatus.rangeY, 2); tft.drawString(lowText, rangeX + 1, weatherStatus.rangeY, 2);
  tft.setTextColor(TFT_ORANGE, TFT_BLACK);
  int highX = rangeX + lowWidth + rangeGap;
  tft.drawString(highText, highX, weatherStatus.rangeY, 2); tft.drawString(highText, highX + 1, weatherStatus.rangeY, 2);
  if (textNeedsUpdate && drawWeatherAirFromBridge()) weatherTextDrawnRev = weatherStatus.textRev;
  drawWeatherClock();
  tft.fillRect(WEATHER_CONTENT_LEFT, 158, 134, 72, TFT_BLACK);
  int current = (int)(weatherStatus.temp + (weatherStatus.temp >= 0 ? 0.5f : -0.5f));
  tft.setTextDatum(TL_DATUM); tft.setTextColor(TFT_RED, TFT_BLACK);
  tft.fillCircle(WEATHER_CONTENT_LEFT + 4, 175, 4, TFT_RED);
  tft.fillRoundRect(WEATHER_CONTENT_LEFT + 2, 162, 5, 14, 2, TFT_RED);
  tft.setTextColor(TFT_LIGHTGREY, TFT_BLACK); tft.drawString("TEMP", WEATHER_CONTENT_LEFT + 16, 162, 1);
  tft.fillRoundRect(WEATHER_CONTENT_LEFT + 16, 177, 60, 5, 3, TFT_DARKGREY);
  tft.fillRoundRect(WEATHER_CONTENT_LEFT + 16, 177, constrain((current + 10) * 3 / 2, 0, 60), 5, 3, TFT_RED);
  const int metricGap = 2;
  const int metricRight = 144;
  const int metricNumberRight = metricRight - max(tft.textWidth("C", 4), tft.textWidth("%", 4)) - metricGap;
  tft.setTextDatum(TR_DATUM); tft.setTextColor(TFT_WHITE, TFT_BLACK); tft.drawString(String(current), metricNumberRight, 162, 4);
  tft.setTextDatum(TL_DATUM); tft.drawString("C", metricNumberRight + metricGap, 162, 4);
  tft.setTextDatum(TL_DATUM); tft.setTextColor(TFT_GREEN, TFT_BLACK);
  tft.fillCircle(WEATHER_CONTENT_LEFT + 5, 212, 5, TFT_GREEN);
  tft.fillTriangle(WEATHER_CONTENT_LEFT, 212, WEATHER_CONTENT_LEFT + 10, 212, WEATHER_CONTENT_LEFT + 5, 199, TFT_GREEN);
  tft.setTextColor(TFT_LIGHTGREY, TFT_BLACK); tft.drawString("HUMID", WEATHER_CONTENT_LEFT + 16, 198, 1);
  tft.fillRoundRect(WEATHER_CONTENT_LEFT + 16, 214, 60, 5, 3, TFT_DARKGREY);
  tft.fillRoundRect(WEATHER_CONTENT_LEFT + 16, 214, constrain(weatherStatus.humidity * 60 / 100, 0, 60), 5, 3, TFT_GREEN);
  tft.setTextDatum(TR_DATUM); tft.setTextColor(TFT_WHITE, TFT_BLACK); tft.drawString(String(weatherStatus.humidity), metricNumberRight, 199, 4);
  tft.setTextDatum(TL_DATUM); tft.drawString("%", metricNumberRight + metricGap, 199, 4);
  drawWeatherAnimation();
}

// ---------- WiFi / bridge polling ----------

void configModeCallback(WiFiManager *wm) {
  tft.fillScreen(TFT_BLACK);
  tft.setTextDatum(TL_DATUM);
  tft.setTextColor(TFT_WHITE, TFT_BLACK);
  tft.drawString("WiFi setup needed", 8, 40, 2);
  tft.drawString("Connect phone to AP:", 8, 70, 2);
  tft.setTextColor(TFT_YELLOW, TFT_BLACK);
  tft.drawString(WIFI_PORTAL_AP_NAME, 8, 95, 2);
  tft.setTextColor(TFT_WHITE, TFT_BLACK);
  tft.drawString("then open 192.168.4.1", 8, 125, 2);
  tft.setTextColor(TFT_DARKGREY, TFT_BLACK);
  tft.drawString("Firmware v" FW_VERSION, 8, 215, 2);
}

void setupWiFi() {
  wifiManager.setAPCallback(configModeCallback);
  wifiManager.setConfigPortalBlocking(false);
  WiFi.mode(WIFI_STA);
  WiFi.setAutoReconnect(true);
  WiFi.begin();
  wifiDisconnectedSinceMs = millis();
  lastWifiRetryMs = millis();
  Serial.println("[wifi] connecting in background...");
  Serial.printf("[wifi] bridge host = '%s'\n", bridgeHost.c_str());
}

void readDomesticProvider(JsonObject source, DomesticProviderStatus &target) {
  if (source.isNull()) return;
  target.model = source["model"] | "";
  target.membershipBadge = source["membership_badge"] | false;
  target.tokensToday = source["tokens_today"] | 0;
  target.planPct = source["plan_pct"] | -1.0;
  target.planPctText = source["plan_pct_text"] | "";
  target.remainingPctText = source["remaining_pct_text"] | "";
  target.fiveHourPct = source["five_hour_pct"] | -1.0;
  target.fiveHourResetMin = source["five_hour_reset_min"] | -1;
  target.weeklyPct = source["weekly_pct"] | -1.0;
  target.weeklyResetMin = source["weekly_reset_min"] | -1;
  target.planResetAt = source["plan_reset_at"] | 0UL;
  target.planResetMin = source["plan_reset_min"] | -1;
  target.balance = source["balance"] | -1.0;
  target.usedCost = source["used_cost"] | -1.0;
  target.currency = source["currency"] | "";
}

void applyCodexCompletionState(uint32_t incomingCompletionAt,
                               uint32_t incomingCompletionSeq,
                               bool incomingCompletionActive) {
  if (!codexCompletionInitialized) {
    bool wasActive = codexCompletionActive || completionAlertActive();
    codexCompletionInitialized = true;
    codexStatus.completionAt = incomingCompletionAt;
    codexCompletionSeq = incomingCompletionSeq;
    if (incomingCompletionActive) startCodexCompletionAlert();
    else {
      codexCompletionActive = false;
      completionFlashPhase = COMPLETION_FLASH_PHASES + 1;
      if (wasActive) redrawRingOnly();
    }
    return;
  }
  bool wasActive = codexCompletionActive;
  if (incomingCompletionSeq > codexCompletionSeq) {
    codexCompletionSeq = incomingCompletionSeq;
    codexStatus.completionAt = incomingCompletionAt;
    if (incomingCompletionActive) startCodexCompletionAlert();
  }
  codexCompletionActive = incomingCompletionActive;
  if (wasActive && !codexCompletionActive) redrawRingOnly();
}

bool parseStatusJson(const String &payload, bool applyAlertState = true) {
  JsonDocument doc;
  DeserializationError err = deserializeJson(doc, payload);
  if (err) return false;

  JsonObject c = doc["claude"];
  if (!c.isNull()) {
    claudeStatus.plan = c["plan"] | "";
    claudeStatus.status = c["status"] | "unknown";
    claudeStatus.tokensToday = c["tokens_today"] | 0;
    claudeStatus.sessionMin = c["session_min"] | 0;
    claudeStatus.sessionWindowMin = c["session_window_min"] | 300;
    claudeStatus.fiveHourPct = c["five_hour_pct"] | -1.0;
    claudeStatus.fiveHourResetMin = c["five_hour_reset_min"] | -1;
    claudeStatus.sevenDayPct = c["seven_day_pct"] | -1.0;
    claudeStatus.sevenDayResetMin = c["seven_day_reset_min"] | -1;
    if (applyAlertState) claudeStatus.needsInput = c["needs_input"] | false;
  }

  JsonObject x = doc["codex"];
  if (!x.isNull()) {
    codexStatus.plan = x["plan"] | "";
    codexStatus.status = x["status"] | "unknown";
    codexStatus.tokensToday = x["tokens_today"] | 0;
    codexStatus.primaryPct = x["primary_pct"] | -1.0;
    codexStatus.primaryResetMin = x["primary_reset_min"] | -1;
    codexStatus.weeklyPct = x["weekly_pct"] | -1.0;
    codexStatus.weeklyResetMin = x["weekly_reset_min"] | -1;
    codexStatus.resetCreditsAvailable = x["reset_credits_available"] | -1;
    codexStatus.resetCreditExpiresAt = x["reset_credit_expires_at"] | 0UL;
    if (applyAlertState) {
      codexStatus.needsInput = x["needs_input"] | false;
      uint32_t incomingCompletionAt = x["completion_at"] | 0UL;
      uint32_t incomingCompletionSeq = x["completion_seq"] | incomingCompletionAt;
      bool incomingCompletionActive = x["completion_active"] | false;
      applyCodexCompletionState(incomingCompletionAt, incomingCompletionSeq,
                                incomingCompletionActive);
    }
  }
  JsonObject d = doc["domestic"];
  if (!d.isNull()) {
    domesticStatus.status = d["status"] | "offline";
    domesticStatus.activeProvider = d["active_provider"] | "";
    if (applyAlertState) domesticStatus.needsInput = d["needs_input"] | false;
    readDomesticProvider(d["qwen"], domesticStatus.qwen);
    readDomesticProvider(d["xiaomi"], domesticStatus.xiaomi);
    readDomesticProvider(d["kimi"], domesticStatus.kimi);
    readDomesticProvider(d["minimax"], domesticStatus.minimax);
    readDomesticProvider(d["deepseek"], domesticStatus.deepseek);
    JsonObject active = d["active"];
    if (!active.isNull()) readDomesticProvider(active, domesticStatus.active);
    else if (domesticStatus.activeProvider == "xiaomi") domesticStatus.active = domesticStatus.xiaomi;
    else if (domesticStatus.activeProvider == "kimi") domesticStatus.active = domesticStatus.kimi;
    else if (domesticStatus.activeProvider == "minimax") domesticStatus.active = domesticStatus.minimax;
    else if (domesticStatus.activeProvider == "deepseek") domesticStatus.active = domesticStatus.deepseek;
    else domesticStatus.active = domesticStatus.qwen;
  }
  statusMusicPlaying = doc["music_playing"] | false;
  uint32_t statusEpoch = doc["ts"] | 0UL;
  if (statusEpoch > 0) {
    bridgeEpochUtc = statusEpoch;
    int incomingOffset = doc["local_utc_offset_s"] | bridgeUtcOffsetS;
    if (incomingOffset >= -12 * 3600 && incomingOffset <= 14 * 3600)
      saveUtcOffset(incomingOffset);
    bridgeClockSyncMs = millis();
  }
  return true;
}

// The mode actually rendered. In AUTO: a pending approval prompt wins (stay on
// the pet so its border can flash red at you), otherwise audio promotes to the
// music page.
DisplayMode effectiveMode() {
  // A dead Windows host must never leave a stale quota/weather/stock frame on
  // screen. Keep the user's configured mode intact and temporarily render the
  // standalone clock until a bridge heartbeat returns.
  if (hostGoingAway || bridgeStale()) return MODE_SCREENSAVER;
  if (displayMode == MODE_SCREENSAVER && screenSaverPreview) return MODE_SCREENSAVER;
  if (domesticStatus.needsInput) return MODE_DOMESTIC;
  if (claudeStatus.needsInput || codexStatus.needsInput || completionAlertActive()) return MODE_AUTO;
  if (displayMode == MODE_SCREENSAVER) {
    if (domesticStatus.status == "working") return MODE_DOMESTIC;
    if (claudeStatus.status == "working" || codexStatus.status == "working") return MODE_AUTO;
  }
  if (displayMode == MODE_AUTO) {
    if (statusMusicPlaying) return MODE_MUSIC;
    if (domesticStatus.status == "working") return MODE_DOMESTIC;
  }
  return displayMode;
}

void drawEffectiveMode(DisplayMode eff, bool force, unsigned long nowMs = 0) {
  if (eff == MODE_NET) {
    netChromeDrawn = false;
    lastNetPollMs = 0;
  } else if (eff == MODE_MUSIC) {
    musicChromeDrawn = false;
    lastMusicPollMs = 0;
  } else if (eff == MODE_DUAL) {
    drawDualScreen(force);
  } else if (eff == MODE_DOMESTIC) {
    drawDomesticScreen(force);
  } else if (eff == MODE_STOCK) {
    stockPage = 0;
    lastStockPageMs = nowMs ? nowMs : millis();
    lastStockPollMs = 0;
    drawStockCachedOrLoading();
  } else if (eff == MODE_WEATHER) {
    lastWeatherPollMs = 0;
    drawWeatherCachedOrLoading();
  } else if (eff == MODE_SCREENSAVER) {
    drawScreenSaver(force);
  } else {
    updateActiveApp();
    drawActiveApp();
  }
}

void pollBridge() {
  if (usbBridgeActive()) return;
  if (WiFi.status() != WL_CONNECTED || bridgeHost.length() == 0) {
    Serial.printf("[bridge] skip poll: wifi=%d host='%s'\n", WiFi.status() == WL_CONNECTED, bridgeHost.c_str());
    return;
  }

  WiFiClient client;
  HTTPClient http;
  String url = "http://" + bridgeHost + BRIDGE_DEFAULT_PATH;
  http.setTimeout(BRIDGE_HTTP_TIMEOUT_MS);

  if (!http.begin(client, url)) {
    Serial.println("[bridge] http.begin() failed");
    return;
  }
  int code = http.GET();
  Serial.printf("[bridge] GET %s -> %d\n", url.c_str(), code);
  if (code == HTTP_CODE_OK) {
    String payload = http.getString();
    if (parseStatusJson(payload)) {
      hostGoingAway = false;
      lastSuccessMs = millis();
      everPolled = true;
      Serial.printf("[bridge] claude=%s tok=%ld | codex=%s tok=%ld primary=%.0f%%\n",
                    claudeStatus.status.c_str(), claudeStatus.tokensToday,
                    codexStatus.status.c_str(), codexStatus.tokensToday, codexStatus.primaryPct);
    } else {
      Serial.println("[bridge] JSON parse failed");
    }
  } else {
    claudeStatus.status = "offline";
    codexStatus.status = "offline";
  }
  http.end();
  DisplayMode eff = effectiveMode();
  if (eff != lastEffectiveMode) {
    lastEffectiveMode = eff;
    drawEffectiveMode(eff, true);
  } else if (eff == MODE_DOMESTIC) {
    drawDomesticScreen();
  } else if (eff == MODE_DUAL) {
    drawDualScreen();
  } else if (eff != MODE_NET && eff != MODE_MUSIC && eff != MODE_STOCK && eff != MODE_WEATHER && eff != MODE_SCREENSAVER) {
    // Only a real app switch clears the screen; a plain data refresh paints
    // in place so the poll doesn't flash the whole display.
    if (updateActiveApp()) drawActiveApp();
    else refreshActiveApp();
  }
}

// ---------- web admin ----------

String htmlEscape(const String &s) {
  String out = s;
  out.replace("&", "&amp;");
  out.replace("<", "&lt;");
  out.replace(">", "&gt;");
  out.replace("\"", "&quot;");
  return out;
}

void handleRoot() {
  String age = everPolled ? String((millis() - lastSuccessMs) / 1000) + "s ago" : "never";
  String html;
  html.reserve(3072);
  html += "<!DOCTYPE html><html><head><meta charset='utf-8'>";
  html += "<meta name='viewport' content='width=device-width, initial-scale=1'>";
  html += "<title>AI Clock 设置</title>";
  html += "<style>body{font-family:-apple-system,sans-serif;max-width:480px;margin:24px "
          "auto;padding:0 16px;color:#222} h1{font-size:20px} label{display:block;margin-top:16px;font-weight:600}"
          "input{width:100%;box-sizing:border-box;padding:8px;font-size:16px;margin-top:4px}"
          "button{margin-top:16px;padding:10px 20px;font-size:16px;background:#2563eb;color:#fff;"
          "border:none;border-radius:6px}"
          "table{margin-top:20px;border-collapse:collapse;width:100%}"
          "td{padding:4px 8px;border-bottom:1px solid #eee;font-size:14px}"
          ".dot{display:inline-block;width:10px;height:10px;border-radius:50%;margin-right:6px}"
          "</style></head><body>";
  html += "<h1>AI Clock 设置</h1>";

  html += "<form method='POST' action='/save'>";
  html += "<label>Bridge host (ip:port)</label>";
  html += "<input name='bridge' value='" + htmlEscape(bridgeHost) + "' placeholder='192.168.1.181:8765'>";
  html += "<button type='submit'>保存</button>";
  html += "</form>";

  // Backlight brightness slider: applies live on release (PWM, persisted).
  html += "<h2 style='font-size:16px;margin-top:28px'>屏幕亮度</h2>";
  html += "<input type='range' min='0' max='100' value='" + String(brightness) + "' id='bri' "
          "oninput=\"document.getElementById('briv').textContent=this.value+'%'\" "
          "onchange=\"fetch('/api/brightness',{method:'POST',headers:{'Content-Type':"
          "'application/x-www-form-urlencoded'},body:'level='+this.value})\">";
  html += "<div style='font-size:13px;color:#555'>当前：<span id='briv'>" + String(brightness) +
          "%</span>（0 = 熄屏，设置立即生效并记住）</div>";

  // On-device GIF upload: replaces a character's animation without reflashing.
  html += "<h2 style='font-size:16px;margin-top:28px'>桌宠动画（上传 GIF）</h2>";
  html += "<p style='font-size:13px;color:#555'>上传一个 .gif，设备会在板上解码并缩放到对应角色的尺寸，"
          "立刻替换动画，无需重新编译或烧录。GIF 太大可能因内存不足解码失败，换小一点的即可。</p>";
  html += "<form id='gifForm' method='POST' enctype='multipart/form-data' onsubmit='return setGifAction()'>";
  html += "<label>角色</label>";
  html += "<select id='gifTarget'><option value='claude'>Claude</option><option value='codex'>Codex</option></select>";
  html += "<label>GIF 文件</label><input type='file' name='file' accept='.gif' required>";
  html += "<button type='submit'>上传并应用</button>";
  html += "</form>";
  html += "<script>function setGifAction(){"
          "document.getElementById('gifForm').action='/sprite/'+document.getElementById('gifTarget').value;"
          "return true;}</script>";

  html += "<table>";
  html += "<tr><td>WiFi SSID</td><td>" + htmlEscape(WiFi.SSID()) + "</td></tr>";
  html += "<tr><td>设备 IP</td><td>" + WiFi.localIP().toString() + "</td></tr>";
  html += "<tr><td>上次桥接更新</td><td>" + age + "</td></tr>";
  html += "<tr><td>Claude</td><td>" + htmlEscape(claudeStatus.status) + ", " +
          formatTokens(claudeStatus.tokensToday) + " tok</td></tr>";
  html += "<tr><td>Codex</td><td>" + htmlEscape(codexStatus.status) + ", " +
          formatTokens(codexStatus.tokensToday) + " tok, 5h " +
          (codexStatus.primaryPct >= 0 ? String(codexStatus.primaryPct, 0) + "%" : "?") + "</td></tr>";
  html += "</table>";

  html += "<form method='POST' action='/reset-wifi' onsubmit=\"return confirm('清除 WiFi "
          "设置并重启？设备会开启配网热点。');\">";
  html += "<button type='submit' style='background:#dc2626'>重置 WiFi</button>";
  html += "</form>";

  html += "</body></html>";
  webServer.send(200, "text/html", html);
}

void handleSave() {
  String newHost = webServer.arg("bridge");
  newHost.trim();
  bridgeHost = newHost;
  saveBridgeHost(bridgeHost);
  Serial.printf("[web] bridge host updated to '%s'\n", bridgeHost.c_str());
  webServer.sendHeader("Location", "/");
  webServer.send(303);
}

// ---------- JSON API for the Mac app ----------

const char *displayModeName(DisplayMode m) {
  if (m == MODE_CLAUDE) return "claude";
  if (m == MODE_CODEX) return "codex";
  if (m == MODE_DUAL) return "dual";
  if (m == MODE_DOMESTIC) return "domestic";
  if (m == MODE_NET) return "net";
  if (m == MODE_MUSIC) return "music";
  if (m == MODE_STOCK) return "stock";
  if (m == MODE_WEATHER) return "weather";
  if (m == MODE_SCREENSAVER) return "screensaver";
  return "auto";
}

uint32_t crc32Update(uint32_t crc, const uint8_t *data, size_t length);

uint32_t cachedFileCrc(const char *path) {
  File file = LittleFS.open(path, "r");
  if (!file) return 0;
  uint32_t crc = 0xffffffff;
  uint8_t buffer[128];
  while (file.available()) {
    int count = file.read(buffer, sizeof(buffer));
    if (count <= 0) { file.close(); return 0; }
    crc = crc32Update(crc, buffer, count);
  }
  file.close();
  return crc ^ 0xffffffff;
}

String deviceInfoJson() {
  JsonDocument doc;
  doc["ip"] = WiFi.localIP().toString();
  doc["ssid"] = WiFi.SSID();
  doc["bridge"] = bridgeHost;
  doc["mode"] = displayModeName(displayMode);           // configured mode
  doc["effective"] = displayModeName(effectiveMode());   // what's on screen now
  doc["host_offline"] = hostGoingAway || bridgeStale();
  doc["time_source"] = currentTimeSource();
  doc["ntp_synced"] = ntpSynced;
  doc["music_playing"] = statusMusicPlaying;
  doc["showing"] = (currentApp == APP_CLAUDE) ? "claude" : "codex";
  doc["last_update_s"] = everPolled ? (long)((millis() - lastSuccessMs) / 1000) : -1;
  doc["sprite_rev"] = spriteRev;
  doc["brightness"] = brightness;
  doc["fw"] = FW_VERSION;
  doc["stock_page"] = stockPage;
  doc["stock_page_count"] = stockCount > 0
    ? (stockCount + STOCK_ROWS_PER_PAGE - 1) / STOCK_ROWS_PER_PAGE : 1;
  JsonObject domestic = doc["domestic"].to<JsonObject>();
  domestic["active_provider"] = domesticStatus.activeProvider;
  domestic["active_model"] = domesticStatus.active.model;
  domestic["qwen_model"] = domesticStatus.qwen.model;
  domestic["kimi_model"] = domesticStatus.kimi.model;
  domestic["minimax_model"] = domesticStatus.minimax.model;
  domestic["deepseek_model"] = domesticStatus.deepseek.model;
  domestic["deepseek_balance"] = domesticStatus.deepseek.balance;
  domestic["deepseek_used_cost"] = domesticStatus.deepseek.usedCost;
  domestic["deepseek_currency"] = domesticStatus.deepseek.currency;
  JsonObject cache = doc["ui_cache"].to<JsonObject>();
  cache["stock"] = LittleFS.exists(STOCK_UI_CACHE_FILE);
  cache["weather"] = LittleFS.exists(WEATHER_UI_CACHE_FILE);
  cache["stock_crc"] = cachedFileCrc(STOCK_UI_CACHE_FILE);
  cache["weather_crc"] = cachedFileCrc(WEATHER_UI_CACHE_FILE);
  JsonObject c = doc["claude"].to<JsonObject>();
  c["plan"] = claudeStatus.plan;
  c["status"] = claudeStatus.status;
  c["custom_sprite"] = claudeCustom;
  c["w"] = CLAUDE_SPRITE_W;
  c["h"] = CLAUDE_SPRITE_H;
  JsonObject x = doc["codex"].to<JsonObject>();
  x["plan"] = codexStatus.plan;
  x["status"] = codexStatus.status;
  x["custom_sprite"] = codexCustom;
  x["w"] = CODEX_SPRITE_W;
  x["h"] = CODEX_SPRITE_H;
  String out;
  serializeJson(doc, out);
  return out;
}

void handleApiInfo() {
  String out = deviceInfoJson();
  webServer.send(200, "application/json", out);
}

void handleApiDisplay() {
  String mode = webServer.arg("mode");
  screenSaverPreview = mode == "screensaver_preview";
  if (mode == "auto") displayMode = MODE_AUTO;
  else if (mode == "claude") displayMode = MODE_CLAUDE;
  else if (mode == "codex") displayMode = MODE_CODEX;
  else if (mode == "dual") displayMode = MODE_DUAL;
  else if (mode == "domestic") displayMode = MODE_DOMESTIC;
  else if (mode == "net") displayMode = MODE_NET;
  else if (mode == "music") displayMode = MODE_MUSIC;
  else if (mode == "stock") displayMode = MODE_STOCK;
  else if (mode == "weather") displayMode = MODE_WEATHER;
  else if (mode == "screensaver" || mode == "screensaver_preview") displayMode = MODE_SCREENSAVER;
  else {
    screenSaverPreview = false;
    webServer.send(400, "text/plain", "mode must be auto|claude|codex|dual|domestic|net|music|stock|weather|screensaver");
    return;
  }
  Serial.printf("[api] display mode = %s\n", mode.c_str());
  lastEffectiveMode = effectiveMode();
  if (displayMode == MODE_NET) {
    netChromeDrawn = false;
    lastNetPollMs = 0; // poll + draw on the next loop tick
  } else if (displayMode == MODE_MUSIC) {
    musicChromeDrawn = false;
    lastMusicPollMs = 0; // poll + draw on the next loop tick
  } else if (displayMode == MODE_DUAL) {
    drawDualScreen(true);
  } else if (displayMode == MODE_DOMESTIC) {
    drawDomesticScreen(true);
  } else if (displayMode == MODE_STOCK) {
    lastStockPollMs = 0;
    drawStockCachedOrLoading();
  } else if (displayMode == MODE_WEATHER) {
    lastWeatherPollMs = 0;
    drawWeatherCachedOrLoading();
  } else if (displayMode == MODE_SCREENSAVER) {
    drawScreenSaver(true);
  } else {
    updateActiveApp();
    drawActiveApp(); // unconditional: also repaints over a previous net chart
  }
  webServer.send(200, "text/plain", "ok");
}

void handleApiBrightness() {
  String levelArg = webServer.arg("level");
  if (levelArg.length() == 0) {
    webServer.send(400, "text/plain", "missing level (0-100)");
    return;
  }
  int level = levelArg.toInt();
  if (level < 0) level = 0;
  if (level > 100) level = 100;
  brightness = level;
  applyBrightness();
  saveBrightness();
  Serial.printf("[api] brightness = %d\n", brightness);
  webServer.send(200, "text/plain", "ok");
}

void sendUsbFrame(const char *type, const String &data = "") {
  Serial.print(USB_FRAME_PREFIX);
  Serial.print("{\"type\":\"");
  Serial.print(type);
  Serial.print("\",\"version\":1");
  if (data.length()) {
    Serial.print(",\"data\":");
    Serial.print(data);
  }
  Serial.println("}");
}

void sendUsbAlertAck(uint32_t alertSession, uint32_t alertSeq) {
  Serial.print(USB_FRAME_PREFIX);
  Serial.print("{\"type\":\"alert_ack\",\"version\":1,\"alert_session\":");
  Serial.print(alertSession);
  Serial.print(",\"alert_seq\":");
  Serial.print(alertSeq);
  Serial.println("}");
}

uint32_t crc32Update(uint32_t crc, const uint8_t *data, size_t length) {
  while (length--) {
    crc ^= *data++;
    for (int bit = 0; bit < 8; bit++)
      crc = (crc & 1) ? (crc >> 1) ^ 0xedb88320UL : crc >> 1;
  }
  return crc;
}

void sendBinaryAck(uint16_t transfer, int seq, const char *stage, bool ok, const char *reason = "") {
  JsonDocument doc;
  doc["type"] = "binary_ack";
  doc["version"] = 1;
  doc["transfer"] = transfer;
  doc["seq"] = seq;
  doc["stage"] = stage;
  doc["ok"] = ok;
  if (reason[0]) doc["reason"] = reason;
  Serial.print(USB_FRAME_PREFIX);
  serializeJson(doc, Serial);
  Serial.println();
}

size_t cobsDecode(const uint8_t *input, size_t length, uint8_t *output, size_t capacity) {
  size_t read = 0, write = 0;
  while (read < length) {
    uint8_t code = input[read++];
    if (code == 0 || read + code - 1 > length) return 0;
    for (uint8_t i = 1; i < code; i++) {
      if (write >= capacity) return 0;
      output[write++] = input[read++];
    }
    if (code != 0xff && read < length) {
      if (write >= capacity) return 0;
      output[write++] = 0;
    }
  }
  return write;
}

size_t cobsEncode(const uint8_t *input, size_t length, uint8_t *output, size_t capacity) {
  size_t read = 0, write = 1, codeAt = 0;
  uint8_t code = 1;
  while (read < length) {
    if (input[read] == 0) {
      if (codeAt >= capacity || write >= capacity) return 0;
      output[codeAt] = code; codeAt = write++; code = 1; read++;
    } else {
      if (write >= capacity) return 0;
      output[write++] = input[read++];
      if (++code == 0xff) {
        if (codeAt >= capacity || write >= capacity) return 0;
        output[codeAt] = code; codeAt = write++; code = 1;
      }
    }
  }
  if (codeAt >= capacity) return 0;
  output[codeAt] = code;
  return write;
}

void cancelUsbBlob() {
  if (usbBlob.file) usbBlob.file.close();
  usbBlob.active = false;
  usbBlob.kind = USB_BLOB_NONE;
}

void beginUsbBlob(JsonDocument &doc) {
  cancelUsbBlob();
  String kind = doc["kind"] | "";
  if (kind == "music_cover") usbBlob.kind = USB_MUSIC_COVER;
  else if (kind == "music_text") usbBlob.kind = USB_MUSIC_TEXT;
  else if (kind == "stock_names") usbBlob.kind = USB_STOCK_NAMES;
  else if (kind == "stock_names_rle") usbBlob.kind = USB_STOCK_NAMES_RLE;
  else if (kind == "weather_header") usbBlob.kind = USB_WEATHER_HEADER;
  else if (kind == "weather_date") usbBlob.kind = USB_WEATHER_DATE;
  else if (kind == "weather_air") usbBlob.kind = USB_WEATHER_AIR;
  else if (kind == "weather_labels") usbBlob.kind = USB_WEATHER_LABELS;
  else if (kind == "weather_labels_rle") usbBlob.kind = USB_WEATHER_LABELS_RLE;
  else if (kind == "gif_claude") usbBlob.kind = USB_GIF_CLAUDE;
  else if (kind == "gif_codex") usbBlob.kind = USB_GIF_CODEX;
  usbBlob.transfer = doc["transfer"] | 0;
  usbBlob.expectedSize = doc["size"] | 0UL;
  usbBlob.expectedCrc = doc["crc32"] | 0UL;
  usbBlob.width = doc["width"] | 0;
  usbBlob.height = doc["height"] | 0;
  usbBlob.nextSeq = 0; usbBlob.received = 0; usbBlob.crc = 0xffffffff;
  usbBlob.rowFill = 0; usbBlob.rowIndex = 0;

  bool ok = usbBlob.kind != USB_BLOB_NONE && usbBlob.expectedSize > 0;
  if (usbBlob.kind == USB_MUSIC_COVER)
    ok = ok && usbBlob.width == MUSIC_COVER_W && usbBlob.height == MUSIC_COVER_H
      && usbBlob.expectedSize == (uint32_t)MUSIC_COVER_W * MUSIC_COVER_H * 2;
  if (usbBlob.kind == USB_MUSIC_TEXT)
    ok = ok && usbBlob.width == MUSIC_TEXT_W && usbBlob.height == MUSIC_TEXT_H
      && usbBlob.expectedSize == (uint32_t)MUSIC_TEXT_W * MUSIC_TEXT_H * 2;
  if (usbBlob.kind == USB_STOCK_NAMES)
    ok = ok && usbBlob.width == STOCK_NAME_W && usbBlob.height == STOCK_NAME_H * MAX_STOCKS
      && usbBlob.expectedSize == (uint32_t)STOCK_NAME_W * STOCK_NAME_H * MAX_STOCKS * 2;
  if (usbBlob.kind == USB_STOCK_NAMES_RLE)
    ok = ok && usbBlob.width == STOCK_NAME_W && usbBlob.height == STOCK_NAME_H * MAX_STOCKS
      && usbBlob.expectedSize <= (uint32_t)STOCK_NAME_W * STOCK_NAME_H * MAX_STOCKS * 2;
  if (usbBlob.kind == USB_WEATHER_HEADER)
    ok = ok && usbBlob.width == WEATHER_HEADER_W && usbBlob.height == WEATHER_HEADER_H
      && usbBlob.expectedSize == (uint32_t)WEATHER_HEADER_W * WEATHER_HEADER_H * 2;
  if (usbBlob.kind == USB_WEATHER_DATE)
    ok = ok && usbBlob.width == WEATHER_DATE_W && usbBlob.height == WEATHER_DATE_H
      && usbBlob.expectedSize == (uint32_t)WEATHER_DATE_W * WEATHER_DATE_H * 2;
  if (usbBlob.kind == USB_WEATHER_AIR)
    ok = ok && usbBlob.width == WEATHER_AIR_W && usbBlob.height == WEATHER_AIR_H
      && usbBlob.expectedSize == (uint32_t)WEATHER_AIR_W * WEATHER_AIR_H * 2;
  if (usbBlob.kind == USB_WEATHER_LABELS)
    ok = ok && usbBlob.expectedSize == (uint32_t)WEATHER_HEADER_W * WEATHER_HEADER_H * 2
      + (uint32_t)WEATHER_DATE_W * WEATHER_DATE_H * 2
      + (uint32_t)WEATHER_AIR_W * WEATHER_AIR_H * 2;
  if (usbBlob.kind == USB_WEATHER_LABELS_RLE)
    ok = ok && usbBlob.expectedSize <= (uint32_t)WEATHER_HEADER_W * WEATHER_HEADER_H * 2
      + (uint32_t)WEATHER_DATE_W * WEATHER_DATE_H * 2
      + (uint32_t)WEATHER_AIR_W * WEATHER_AIR_H * 2;
  if (usbBlob.kind == USB_STOCK_NAMES || usbBlob.kind == USB_STOCK_NAMES_RLE
      || usbBlob.kind == USB_WEATHER_LABELS || usbBlob.kind == USB_WEATHER_LABELS_RLE) {
    LittleFS.remove(USB_UI_TEMP_FILE);
    usbBlob.file = LittleFS.open(USB_UI_TEMP_FILE, "w");
    ok = ok && (bool)usbBlob.file;
  }
  if (usbBlob.kind == USB_GIF_CLAUDE || usbBlob.kind == USB_GIF_CODEX) {
    ok = ok && usbBlob.expectedSize <= 1500000UL;
    const char *path = usbBlob.kind == USB_GIF_CLAUDE ? CLAUDE_GIF_FILE : CODEX_GIF_FILE;
    LittleFS.remove(path);
    usbBlob.file = LittleFS.open(path, "w");
    ok = ok && (bool)usbBlob.file;
  }
  usbBlob.active = ok;
  sendBinaryAck(usbBlob.transfer, -1, "begin", ok);
}

void handleUsbBinaryFrame(const uint8_t *encoded, size_t encodedLen) {
  uint8_t frame[524];
  size_t frameLen = cobsDecode(encoded, encodedLen, frame, sizeof(frame));
  if (frameLen < 12 || frame[0] != 1 || frame[1] != 1) return;
  uint16_t transfer = frame[2] | ((uint16_t)frame[3] << 8);
  uint16_t seq = frame[4] | ((uint16_t)frame[5] << 8);
  uint16_t length = frame[6] | ((uint16_t)frame[7] << 8);
  if (length > 512 || frameLen != (size_t)12 + length) return;
  uint32_t expected = (uint32_t)frame[8 + length]
    | ((uint32_t)frame[9 + length] << 8) | ((uint32_t)frame[10 + length] << 16)
    | ((uint32_t)frame[11 + length] << 24);
  uint32_t actual = crc32Update(0xffffffff, frame, 8 + length) ^ 0xffffffff;
  if (!usbBlob.active || transfer != usbBlob.transfer || seq != usbBlob.nextSeq
      || expected != actual || usbBlob.received + length > usbBlob.expectedSize) {
    sendBinaryAck(transfer, seq, "chunk", false);
    return;
  }
  lastUsbStatusMs = millis();
  everUsbStatus = true;

  const uint8_t *payload = frame + 8;
  usbBlob.crc = crc32Update(usbBlob.crc, payload, length);
  if (usbBlob.file) {
    if (usbBlob.file.write(payload, length) != length) {
      cancelUsbBlob();
      sendBinaryAck(transfer, seq, "chunk", false);
      return;
    }
  } else {
    int rowBytes = usbBlob.width * 2;
    for (uint16_t i = 0; i < length; i++) {
      ((uint8_t *)rowBuf)[usbBlob.rowFill++] = payload[i];
      if (usbBlob.rowFill == rowBytes) {
        if (effectiveMode() == MODE_MUSIC) {
          int x = usbBlob.kind == USB_MUSIC_COVER ? (SCREEN_W - MUSIC_COVER_W) / 2 : MUSIC_TEXT_X;
          int y = usbBlob.kind == USB_MUSIC_COVER ? 14 : MUSIC_TEXT_Y;
          tft.pushImage(x, y + usbBlob.rowIndex, usbBlob.width, 1, rowBuf);
        } else if (effectiveMode() == MODE_STOCK && usbBlob.kind == USB_STOCK_NAMES) {
          int stock = usbBlob.rowIndex / STOCK_NAME_H;
          int row = usbBlob.rowIndex % STOCK_NAME_H;
          if (stock < stockCount) tft.pushImage(70, 6 + stock * 54 + row, STOCK_NAME_W, 1, rowBuf);
        } else if (effectiveMode() == MODE_WEATHER && usbBlob.kind == USB_WEATHER_HEADER) {
          tft.pushImage(weatherHeaderX(), WEATHER_HEADER_Y + usbBlob.rowIndex, WEATHER_HEADER_W, 1, rowBuf);
        } else if (effectiveMode() == MODE_WEATHER && usbBlob.kind == USB_WEATHER_DATE) {
          tft.pushImage(WEATHER_DATE_X, WEATHER_DATE_Y + usbBlob.rowIndex, WEATHER_DATE_W, 1, rowBuf);
        } else if (effectiveMode() == MODE_WEATHER && usbBlob.kind == USB_WEATHER_AIR) {
          tft.pushImage(WEATHER_AIR_X, WEATHER_AIR_Y + usbBlob.rowIndex, WEATHER_AIR_W, 1, rowBuf);
        }
        usbBlob.rowFill = 0;
        usbBlob.rowIndex++;
      }
    }
  }
  usbBlob.received += length;
  usbBlob.nextSeq++;
  sendBinaryAck(transfer, seq, "chunk", true);
}

struct Rle565Reader {
  int remaining = 0;
  bool repeat = false;
  uint8_t hi = 0, lo = 0;
};

bool readUiPixels(File &file, bool compressed, Rle565Reader &rle, uint8_t *output, size_t pixels) {
  if (!compressed) return file.read(output, pixels * 2) == (int)(pixels * 2);
  for (size_t pixel = 0; pixel < pixels; pixel++) {
    if (rle.remaining == 0) {
      int header = file.read();
      if (header < 0) return false;
      rle.repeat = (header & 0x80) != 0;
      rle.remaining = (header & 0x7f) + 1;
      if (rle.repeat) {
        int hi = file.read(), lo = file.read();
        if (hi < 0 || lo < 0) return false;
        rle.hi = hi; rle.lo = lo;
      }
    }
    if (rle.repeat) {
      output[pixel * 2] = rle.hi; output[pixel * 2 + 1] = rle.lo;
    } else {
      int hi = file.read(), lo = file.read();
      if (hi < 0 || lo < 0) return false;
      output[pixel * 2] = hi; output[pixel * 2 + 1] = lo;
    }
    rle.remaining--;
  }
  return true;
}

bool drawBufferedUiBlob(UsbBlobKind kind, const char *path, bool render) {
  File file = LittleFS.open(path, "r");
  if (!file) return false;
  bool ok = true;
  bool compressed = kind == USB_STOCK_NAMES_RLE || kind == USB_WEATHER_LABELS_RLE;
  bool stockBlob = kind == USB_STOCK_NAMES || kind == USB_STOCK_NAMES_RLE;
  Rle565Reader rle;
  if (stockBlob) {
    if (render && effectiveMode() == MODE_STOCK) {
      tft.fillScreen(TFT_BLACK);
      stockChromeDrawn = true;
      stockNamesDrawnRev = stockNamesRev;
      for (int i = 0; i < STOCK_ROWS_PER_PAGE; i++) { stockLastCode[i] = "\x01"; stockLastValue[i] = "\x01"; }
      drawStockScreen(false);
    }
    for (int stock = 0; stock < MAX_STOCKS && ok; stock++) {
      for (int row = 0; row < STOCK_NAME_H; row++) {
        if (!readUiPixels(file, compressed, rle, (uint8_t *)rowBuf, STOCK_NAME_W)) { ok = false; break; }
        int pageRow = stock - stockPage * STOCK_ROWS_PER_PAGE;
        if (render && effectiveMode() == MODE_STOCK && stock < stockCount
            && pageRow >= 0 && pageRow < STOCK_ROWS_PER_PAGE)
          tft.pushImage(70, 6 + pageRow * 54 + row, STOCK_NAME_W, 1, rowBuf);
      }
    }
  } else {
    if (render && effectiveMode() == MODE_WEATHER) {
      tft.fillScreen(TFT_BLACK);
      weatherChromeDrawn = true;
      weatherTextDrawnRev = weatherStatus.textRev;
      weatherLastHour = weatherLastMinute = weatherLastSecond = weatherLastStale = -1;
      drawWeatherScreen(false);
    }
    for (int row = 0; row < WEATHER_HEADER_H && ok; row++) {
      if (!readUiPixels(file, compressed, rle, (uint8_t *)rowBuf, WEATHER_HEADER_W)) { ok = false; break; }
      if (render && effectiveMode() == MODE_WEATHER) tft.pushImage(weatherHeaderX(), WEATHER_HEADER_Y + row, WEATHER_HEADER_W, 1, rowBuf);
    }
    for (int row = 0; row < WEATHER_DATE_H && ok; row++) {
      if (!readUiPixels(file, compressed, rle, (uint8_t *)rowBuf, WEATHER_DATE_W)) { ok = false; break; }
      if (render && effectiveMode() == MODE_WEATHER) tft.pushImage(WEATHER_DATE_X, WEATHER_DATE_Y + row, WEATHER_DATE_W, 1, rowBuf);
    }
    for (int row = 0; row < WEATHER_AIR_H && ok; row++) {
      if (!readUiPixels(file, compressed, rle, (uint8_t *)rowBuf, WEATHER_AIR_W)) { ok = false; break; }
      if (render && effectiveMode() == MODE_WEATHER) tft.pushImage(WEATHER_AIR_X, WEATHER_AIR_Y + row, WEATHER_AIR_W, 1, rowBuf);
    }
  }
  if (compressed && (rle.remaining != 0 || file.available())) ok = false;
  file.close();
  return ok;
}

bool promoteUiCache(const char *cachePath) {
  LittleFS.remove(USB_UI_BACKUP_FILE);
  bool hadCache = LittleFS.exists(cachePath);
  if (hadCache && !LittleFS.rename(cachePath, USB_UI_BACKUP_FILE)) return false;
  if (LittleFS.rename(USB_UI_TEMP_FILE, cachePath)) {
    LittleFS.remove(USB_UI_BACKUP_FILE);
    return true;
  }
  if (hadCache) LittleFS.rename(USB_UI_BACKUP_FILE, cachePath);
  return false;
}

void drawStockCachedOrLoading() {
  if (drawBufferedUiBlob(USB_STOCK_NAMES_RLE, STOCK_UI_CACHE_FILE, true)) return;
  tft.fillScreen(TFT_BLACK); stockChromeDrawn = true;
  tft.setTextDatum(TC_DATUM); tft.setTextColor(TFT_DARKGREY, TFT_BLACK);
  tft.drawString("Loading stocks...", SCREEN_CX, 108, 2);
}

void drawWeatherCachedOrLoading() {
  if (drawBufferedUiBlob(USB_WEATHER_LABELS_RLE, WEATHER_UI_CACHE_FILE, true)) return;
  tft.fillScreen(TFT_BLACK); weatherChromeDrawn = true;
  tft.setTextDatum(TC_DATUM); tft.setTextColor(TFT_DARKGREY, TFT_BLACK);
  tft.drawString("Loading weather...", SCREEN_CX, 108, 2);
}

void finishUsbBlob(uint16_t transfer) {
  if (!usbBlob.active || transfer != usbBlob.transfer) {
    sendBinaryAck(transfer, usbBlob.nextSeq, "end", false);
    return;
  }
  if (usbBlob.file) usbBlob.file.close();
  const char *reason = "";
  bool ok = usbBlob.received == usbBlob.expectedSize;
  if (!ok) reason = "size";
  if (ok && (usbBlob.crc ^ 0xffffffff) != usbBlob.expectedCrc) { ok = false; reason = "crc"; }
  if (ok && usbBlob.rowFill != 0) { ok = false; reason = "row"; }
  bool uiBlob = usbBlob.kind == USB_STOCK_NAMES || usbBlob.kind == USB_STOCK_NAMES_RLE
    || usbBlob.kind == USB_WEATHER_LABELS || usbBlob.kind == USB_WEATHER_LABELS_RLE;
  if (ok && uiBlob) {
    ok = drawBufferedUiBlob(usbBlob.kind, USB_UI_TEMP_FILE, false);
    if (!ok) reason = "decode";
    bool cacheable = usbBlob.kind == USB_STOCK_NAMES_RLE || usbBlob.kind == USB_WEATHER_LABELS_RLE;
    const char *cachePath = usbBlob.kind == USB_STOCK_NAMES_RLE ? STOCK_UI_CACHE_FILE : WEATHER_UI_CACHE_FILE;
    if (ok && cacheable && !promoteUiCache(cachePath)) { ok = false; reason = "cache"; }
    if (ok && cacheable) ok = drawBufferedUiBlob(usbBlob.kind, cachePath, true);
    else if (ok) ok = drawBufferedUiBlob(usbBlob.kind, USB_UI_TEMP_FILE, true);
    if (!ok && !reason[0]) reason = "draw";
    LittleFS.remove(USB_UI_TEMP_FILE);
  } else if (uiBlob) {
    LittleFS.remove(USB_UI_TEMP_FILE);
  }
  if (ok && (usbBlob.kind == USB_GIF_CLAUDE || usbBlob.kind == USB_GIF_CODEX)) {
    ActiveApp slot = usbBlob.kind == USB_GIF_CLAUDE ? APP_CLAUDE : APP_CODEX;
    const char *gifPath = slot == APP_CLAUDE ? CLAUDE_GIF_FILE : CODEX_GIF_FILE;
    const char *binPath = slot == APP_CLAUDE ? CLAUDE_SPRITE_FILE : CODEX_SPRITE_FILE;
    int tw = slot == APP_CLAUDE ? CLAUDE_SPRITE_W : CODEX_SPRITE_W;
    int th = slot == APP_CLAUDE ? CLAUDE_SPRITE_H : CODEX_SPRITE_H;
    ok = decodeGifToBin(gifPath, binPath, tw, th);
    if (!ok) reason = "decode";
    LittleFS.remove(gifPath);
    if (ok) {
      spriteRev++;
      loadCustomSpriteState();
      if (slot == APP_CLAUDE) claudeFrame = 0; else codexFrame = 0;
      if (currentApp == slot) drawActiveApp();
    }
  }
  uint16_t seq = usbBlob.nextSeq;
  cancelUsbBlob();
  sendBinaryAck(transfer, seq, "end", ok, reason);
}

void writeUsbBinaryFrame(uint16_t transfer, uint16_t seq, const uint8_t *payload, uint16_t length) {
  uint8_t frame[524], encoded[528];
  frame[0] = 1; frame[1] = 1;
  frame[2] = transfer; frame[3] = transfer >> 8;
  frame[4] = seq; frame[5] = seq >> 8;
  frame[6] = length; frame[7] = length >> 8;
  memcpy(frame + 8, payload, length);
  uint32_t crc = crc32Update(0xffffffff, frame, 8 + length) ^ 0xffffffff;
  frame[8 + length] = crc; frame[9 + length] = crc >> 8;
  frame[10 + length] = crc >> 16; frame[11 + length] = crc >> 24;
  size_t encodedLen = cobsEncode(frame, 12 + length, encoded, sizeof(encoded));
  if (!encodedLen) return;
  Serial.write((uint8_t)0);
  Serial.write(encoded, encodedLen);
  Serial.write((uint8_t)0);
}

uint8_t builtinSpriteByte(ActiveApp slot, size_t offset) {
  int frames = slot == APP_CLAUDE ? CLAUDE_SPRITE_FRAMES : CODEX_SPRITE_FRAMES;
  if (offset == 0) return (uint8_t)frames;
  int w = slot == APP_CLAUDE ? CLAUDE_SPRITE_W : CODEX_SPRITE_W;
  int h = slot == APP_CLAUDE ? CLAUDE_SPRITE_H : CODEX_SPRITE_H;
  size_t frameBytes = (size_t)w * h * 2;
  size_t pixelOffset = offset - 1;
  int frame = pixelOffset / frameBytes;
  size_t within = pixelOffset % frameBytes;
  const uint16_t *const *arr = slot == APP_CLAUDE ? claude_sprite_frames : codex_sprite_frames;
  return pgm_read_byte(((const uint8_t *)arr[frame]) + within);
}

void sendSpriteUsb(ActiveApp slot, uint16_t transfer) {
  bool custom = slot == APP_CLAUDE ? claudeCustom : codexCustom;
  const char *path = slot == APP_CLAUDE ? CLAUDE_SPRITE_FILE : CODEX_SPRITE_FILE;
  int w = slot == APP_CLAUDE ? CLAUDE_SPRITE_W : CODEX_SPRITE_W;
  int h = slot == APP_CLAUDE ? CLAUDE_SPRITE_H : CODEX_SPRITE_H;
  int frames = slot == APP_CLAUDE ? claudeFrameCount() : codexFrameCount();
  size_t size = 1 + (size_t)frames * w * h * 2;
  uint32_t crc = 0xffffffff;
  File file;
  uint8_t buf[512];
  if (custom) {
    file = LittleFS.open(path, "r");
    if (!file) return;
    size = file.size();
    while (file.available()) {
      int n = file.read(buf, sizeof(buf));
      if (n > 0) crc = crc32Update(crc, buf, n);
    }
    file.seek(0);
  } else {
    for (size_t offset = 0; offset < size; offset++) {
      uint8_t value = builtinSpriteByte(slot, offset);
      crc = crc32Update(crc, &value, 1);
    }
  }
  crc ^= 0xffffffff;

  JsonDocument begin;
  begin["type"] = "binary_begin"; begin["version"] = 1;
  begin["direction"] = "device"; begin["kind"] = "sprite";
  begin["transfer"] = transfer; begin["size"] = size; begin["crc32"] = crc;
  Serial.print(USB_FRAME_PREFIX); serializeJson(begin, Serial); Serial.println();

  uint16_t seq = 0;
  for (size_t offset = 0; offset < size; offset += sizeof(buf), seq++) {
    int n = min((size_t)sizeof(buf), size - offset);
    if (custom) n = file.read(buf, n);
    else for (int i = 0; i < n; i++) buf[i] = builtinSpriteByte(slot, offset + i);
    if (n <= 0) break;
    writeUsbBinaryFrame(transfer, seq, buf, n);
    yield();
  }
  if (file) file.close();
  JsonDocument end;
  end["type"] = "binary_end"; end["version"] = 1;
  end["direction"] = "device"; end["transfer"] = transfer;
  Serial.print(USB_FRAME_PREFIX); serializeJson(end, Serial); Serial.println();
}

void applyUsbDisplayMode(const String &mode) {
  screenSaverPreview = mode == "screensaver_preview";
  if (mode == "auto") displayMode = MODE_AUTO;
  else if (mode == "claude") displayMode = MODE_CLAUDE;
  else if (mode == "codex") displayMode = MODE_CODEX;
  else if (mode == "dual") displayMode = MODE_DUAL;
  else if (mode == "domestic") displayMode = MODE_DOMESTIC;
  else if (mode == "net") displayMode = MODE_NET;
  else if (mode == "music") displayMode = MODE_MUSIC;
  else if (mode == "stock") displayMode = MODE_STOCK;
  else if (mode == "weather") displayMode = MODE_WEATHER;
  else if (mode == "screensaver" || mode == "screensaver_preview") displayMode = MODE_SCREENSAVER;
  else { screenSaverPreview = false; return; }
  lastEffectiveMode = effectiveMode();
  if (displayMode == MODE_NET) {
    // Initialise synchronously so a net frame sent immediately after the
    // set_display command is not discarded by the first draw tick.
    resetNetChart();
    drawNetChrome();
    netChromeDrawn = true;
    netHeaderDirty = true;
    lastNetDrawMs = millis();
    lastNetPollMs = 0;
  } else if (displayMode == MODE_MUSIC) {
    musicChromeDrawn = false;
    lastMusicPollMs = 0;
  } else if (displayMode == MODE_DUAL) {
    drawDualScreen(true);
  } else if (displayMode == MODE_DOMESTIC) {
    drawDomesticScreen(true);
  } else if (displayMode == MODE_STOCK) {
    stockPage = 0;
    lastStockPageMs = millis();
    drawStockCachedOrLoading();
  } else if (displayMode == MODE_WEATHER) {
    drawWeatherCachedOrLoading();
  } else if (displayMode == MODE_SCREENSAVER) {
    drawScreenSaver(true);
  } else {
    updateActiveApp();
    drawActiveApp();
  }
  sendUsbFrame("info", deviceInfoJson());
}

void handleUsbFrame(const String &json) {
  JsonDocument doc;
  if (deserializeJson(doc, json)) return;
  if ((int)(doc["version"] | 0) != 1) return;
  lastUsbStatusMs = millis();
  everUsbStatus = true;
  String type = doc["type"] | "";
  if (type == "host_going_away") {
    hostGoingAway = true;
    claudeStatus.needsInput = false;
    codexStatus.needsInput = false;
    domesticStatus.needsInput = false;
    lastEffectiveMode = MODE_SCREENSAVER;
    drawScreenSaver(true);
    sendUsbFrame("info", deviceInfoJson());
    return;
  }
  if (type == "hello") {
    hostGoingAway = false;
    lastUsbStatusMs = millis();
    everUsbStatus = true;
    sendUsbFrame("hello_ack");
    return;
  }
  if (type == "get_info") {
    sendUsbFrame("info", deviceInfoJson());
    return;
  }
  if (type == "status") {
    hostGoingAway = false;
    String payload;
    serializeJson(doc["data"], payload);
    if (parseStatusJson(payload, !usbAlertInitialized)) {
      lastUsbStatusMs = millis();
      everUsbStatus = true;
      lastSuccessMs = millis();
      everPolled = true;
      DisplayMode eff = effectiveMode();
      if (eff != lastEffectiveMode) {
        lastEffectiveMode = eff;
        drawEffectiveMode(eff, true);
      } else if (eff == MODE_DOMESTIC) {
        drawDomesticScreen();
      } else if (eff == MODE_DUAL) {
        drawDualScreen();
      } else if (eff != MODE_NET && eff != MODE_MUSIC && eff != MODE_STOCK
                 && eff != MODE_WEATHER && eff != MODE_SCREENSAVER) {
        if (updateActiveApp()) drawActiveApp();
        else refreshActiveApp();
      }
      sendUsbFrame("status_ack");
    }
    return;
  }
  if (type == "alert") {
    uint32_t alertSession = doc["alert_session"] | 0UL;
    uint32_t alertSeq = doc["alert_seq"] | 0UL;
    bool newer = !usbAlertInitialized || alertSession != usbAlertSession
                 || (int32_t)(alertSeq - usbAlertSeq) > 0;
    if (newer) {
      bool newSession = !usbAlertInitialized || alertSession != usbAlertSession;
      usbAlertInitialized = true;
      usbAlertSession = alertSession;
      usbAlertSeq = alertSeq;
      // The Windows bridge owns completion_seq only within one process.
      // A fresh alert session therefore establishes a new device baseline;
      // otherwise seq=1 after an app restart looks older than the previous run.
      if (newSession) codexCompletionInitialized = false;
      if (!doc["claude_needs_input"].isNull())
        claudeStatus.needsInput = doc["claude_needs_input"] | false;
      if (!doc["codex_needs_input"].isNull())
        codexStatus.needsInput = doc["codex_needs_input"] | false;
      if (!doc["domestic_needs_input"].isNull())
        domesticStatus.needsInput = doc["domestic_needs_input"] | false;
      applyCodexCompletionState(doc["completion_at"] | 0UL,
                                doc["completion_seq"] | 0UL,
                                doc["completion_active"] | false);
    }
    lastUsbStatusMs = millis();
    everUsbStatus = true;
    DisplayMode eff = effectiveMode();
    if (eff != lastEffectiveMode) {
      lastEffectiveMode = eff;
      drawEffectiveMode(eff, true);
    } else if (eff == MODE_DOMESTIC) {
      drawDomesticScreen();
    } else if (eff != MODE_NET && eff != MODE_MUSIC && eff != MODE_STOCK
               && eff != MODE_WEATHER && eff != MODE_SCREENSAVER
               && eff != MODE_DUAL) {
      if (updateActiveApp()) drawActiveApp();
      else refreshActiveApp();
    }
    sendUsbAlertAck(alertSession, alertSeq);
    return;
  }
  if (type == "binary_begin") {
    beginUsbBlob(doc);
    return;
  }
  if (type == "binary_end") {
    finishUsbBlob(doc["transfer"] | 0);
    return;
  }
  if (type == "get_sprite_usb") {
    String slot = doc["slot"] | "";
    uint16_t transfer = doc["transfer"] | 0;
    if (slot == "claude") sendSpriteUsb(APP_CLAUDE, transfer);
    else if (slot == "codex") sendSpriteUsb(APP_CODEX, transfer);
    return;
  }
  if (type == "reset_sprite") {
    String slotName = doc["slot"] | "";
    ActiveApp slot = slotName == "claude" ? APP_CLAUDE : APP_CODEX;
    const char *binPath = slot == APP_CLAUDE ? CLAUDE_SPRITE_FILE : CODEX_SPRITE_FILE;
    LittleFS.remove(binPath);
    spriteRev++;
    loadCustomSpriteState();
    if (slot == APP_CLAUDE) claudeFrame = 0; else codexFrame = 0;
    if (currentApp == slot) drawActiveApp();
    sendUsbFrame("info", deviceInfoJson());
    return;
  }
  if (type == "net" && !doc["data"].isNull()) {
    applyNetJson(doc["data"].as<JsonObject>());
    return;
  }
  if (type == "music" && !doc["data"].isNull()) {
    applyMusicJson(doc["data"].as<JsonObject>());
    return;
  }
  if (type == "stock" && !doc["data"].isNull()) {
    applyStockJson(doc["data"].as<JsonObject>());
    if (effectiveMode() == MODE_STOCK) drawStockScreen();
    return;
  }
  if (type == "weather" && !doc["data"].isNull()) {
    applyWeatherJson(doc["data"].as<JsonObject>());
    return;
  }
  if (type == "set_display") {
    applyUsbDisplayMode(doc["mode"] | "");
    return;
  }
  if (type == "set_brightness") {
    int level = doc["level"] | brightness;
    brightness = constrain(level, 0, 100);
    applyBrightness();
    saveBrightness();
    sendUsbFrame("info", deviceInfoJson());
  }
}

void handleUsbSerial() {
  static String line;
  static bool binary = false;
  static uint8_t encoded[528];
  static size_t encodedLen = 0;
  while (Serial.available()) {
    uint8_t value = (uint8_t)Serial.read();
    if (value == 0) {
      if (binary) {
        if (encodedLen) {
          handleUsbBinaryFrame(encoded, encodedLen);
          yield();
        }
        encodedLen = 0;
        binary = false;
      } else {
        line = "";
        binary = true;
      }
      continue;
    }
    if (binary) {
      if (encodedLen < sizeof(encoded)) encoded[encodedLen++] = value;
      else { encodedLen = 0; binary = false; }
      continue;
    }
    char ch = (char)value;
    if (ch == '\n') {
      line.trim();
      if (line.startsWith(USB_FRAME_PREFIX)) {
        handleUsbFrame(line.substring(strlen(USB_FRAME_PREFIX)));
      }
      line = "";
    } else if (ch != '\r') {
      if (line.length() < USB_TEXT_FRAME_MAX) line += ch;
      else line = "";
    }
  }
}

void handleApiBridge() {
  String newHost = webServer.arg("host");
  newHost.trim();
  if (newHost.length() == 0) {
    webServer.send(400, "text/plain", "missing host");
    return;
  }
  bridgeHost = newHost;
  saveBridgeHost(bridgeHost);
  Serial.printf("[api] bridge host = '%s'\n", bridgeHost.c_str());
  webServer.send(200, "text/plain", "ok");
  lastPollMs = 0; // poll the new bridge on the next loop tick
}

// Streams the animation currently in use for a slot, in the same wire format
// as the custom .bin: [1 byte frame count][RGB565 frames...]. Lets the Mac
// app mirror exactly what the device is showing (custom upload or built-in).
void handleSpriteRaw(ActiveApp slot) {
  bool custom = (slot == APP_CLAUDE) ? claudeCustom : codexCustom;
  const char *binPath = (slot == APP_CLAUDE) ? CLAUDE_SPRITE_FILE : CODEX_SPRITE_FILE;
  if (custom) {
    File f = LittleFS.open(binPath, "r");
    if (f) {
      webServer.streamFile(f, "application/octet-stream");
      f.close();
      return;
    }
  }
  int frames = (slot == APP_CLAUDE) ? CLAUDE_SPRITE_FRAMES : CODEX_SPRITE_FRAMES;
  int w = (slot == APP_CLAUDE) ? CLAUDE_SPRITE_W : CODEX_SPRITE_W;
  int h = (slot == APP_CLAUDE) ? CLAUDE_SPRITE_H : CODEX_SPRITE_H;
  const uint16_t *const *arr = (slot == APP_CLAUDE) ? claude_sprite_frames : codex_sprite_frames;
  size_t frameBytes = (size_t)w * h * 2;
  webServer.setContentLength(1 + (size_t)frames * frameBytes);
  webServer.send(200, "application/octet-stream", "");
  uint8_t cnt = (uint8_t)frames;
  webServer.sendContent((const char *)&cnt, 1);
  for (int i = 0; i < frames; i++) {
    webServer.sendContent_P((PGM_P)arr[i], frameBytes);
    yield();
  }
}

// Removes a custom sprite so the compiled-in default animation comes back.
void handleSpriteReset(ActiveApp slot) {
  const char *binPath = (slot == APP_CLAUDE) ? CLAUDE_SPRITE_FILE : CODEX_SPRITE_FILE;
  LittleFS.remove(binPath);
  spriteRev++;
  loadCustomSpriteState();
  if (slot == APP_CLAUDE) claudeFrame = 0;
  else codexFrame = 0;
  if (currentApp == slot) drawActiveApp();
  webServer.send(200, "text/plain", "ok");
}

void handleResetWifi() {
  webServer.send(200, "text/html", "<html><body>Resetting WiFi, device will restart...</body></html>");
  delay(200);
  WiFiManager wm;
  wm.resetSettings();
  ESP.restart();
}

// ---------- on-device GIF decode (AnimatedGIF) ----------
// AnimatedGIF hands us the image one horizontal line at a time (via the draw
// callback) at the GIF's native resolution, so we never need a full-canvas
// buffer. We nearest-neighbour rescale into the target slot size and stream the
// result straight to the .bin one target row at a time. Because the .bin can't
// hold a whole frame in RAM to composite against, GIFs that only re-encode a
// changed sub-rectangle (the common optimizer output, disposal method 1) are
// composited by reading the *previous frame's* rows back out of the .bin we're
// writing. (Disposal method 2 "restore to background" isn't distinguished -
// uncovered pixels keep the previous frame instead of clearing; fine for the
// looping character animations this is for.)

struct GifDecodeCtx {
  int canvasW, canvasH; // GIF native size
  int targetW, targetH; // slot size we're rescaling down to
  size_t rowBytes;      // targetW * 2
  File out;             // output .bin, written sequentially
  File prevFile;        // previous frame in the .bin, read sequentially for compositing
  bool hasPrev;         // false for frame 0 (nothing to composite over -> black)
  int producedRow;      // next target row still owed for the current frame
};

static File gifReadFile; // one decode runs at a time, so a single handle is fine

void *gifOpenCB(const char *fname, int32_t *pSize) {
  gifReadFile = LittleFS.open(fname, "r");
  if (!gifReadFile) return nullptr;
  *pSize = (int32_t)gifReadFile.size();
  return (void *)&gifReadFile;
}

void gifCloseCB(void *) {
  if (gifReadFile) gifReadFile.close();
}

int32_t gifReadCB(GIFFILE *pFile, uint8_t *pBuf, int32_t iLen) {
  File *f = (File *)pFile->fHandle;
  // AnimatedGIF's own SD example keeps this one-byte-short guard near EOF.
  if ((pFile->iSize - pFile->iPos) < iLen) iLen = pFile->iSize - pFile->iPos - 1;
  if (iLen <= 0) return 0;
  int32_t n = (int32_t)f->read(pBuf, iLen);
  pFile->iPos = (int32_t)f->position();
  return n;
}

int32_t gifSeekCB(GIFFILE *pFile, int32_t iPosition) {
  File *f = (File *)pFile->fHandle;
  f->seek(iPosition);
  pFile->iPos = iPosition;
  return iPosition;
}

// Loads the next previous-frame row into prevRowBuf (black if there's no
// previous frame). Reads are sequential and stay aligned with producedRow.
static void readPrevRow(GifDecodeCtx *ctx) {
  if (ctx->hasPrev)
    ctx->prevFile.read((uint8_t *)prevRowBuf, ctx->rowBytes);
  else
    memset(prevRowBuf, 0, ctx->rowBytes);
}

// Appends the current rowBuf as the next output row.
static void emitRow(GifDecodeCtx *ctx) {
  ctx->out.write((const uint8_t *)rowBuf, ctx->rowBytes);
  ctx->producedRow++;
}

// Emits a row that this frame doesn't touch: a straight copy of the previous
// frame (top/bottom gaps of a partial frame).
static void emitPrevRow(GifDecodeCtx *ctx) {
  readPrevRow(ctx);
  memcpy(rowBuf, prevRowBuf, ctx->rowBytes);
  emitRow(ctx);
}

// Rescales one decoded native line into target rows, compositing over the
// previous frame, and streams every target row it can now finalize.
void gifDrawCB(GIFDRAW *pDraw) {
  GifDecodeCtx *ctx = (GifDecodeCtx *)pDraw->pUser;
  int sy = pDraw->iY + pDraw->y; // absolute source line on the GIF canvas
  if (sy < 0 || sy >= ctx->canvasH) return;

  const uint8_t *pal = pDraw->pPalette24; // RGB888, 256 entries
  const uint8_t *src = pDraw->pPixels;    // palette indices, one per pixel of this line
  bool hasTrans = pDraw->ucHasTransparency;
  uint8_t transIdx = pDraw->ucTransparent;

  // Emit every target row whose nearest source line is <= sy and isn't done yet.
  while (ctx->producedRow < ctx->targetH) {
    int ty = ctx->producedRow;
    int srcRow = (int)((long)ty * ctx->canvasH / ctx->targetH);
    if (srcRow > sy) break;                       // needs a later source line
    if (srcRow < sy) { emitPrevRow(ctx); continue; } // source line was skipped -> previous frame

    // srcRow == sy: composite this source line over the previous frame's row.
    readPrevRow(ctx);
    memcpy(rowBuf, prevRowBuf, ctx->rowBytes);
    for (int tx = 0; tx < ctx->targetW; tx++) {
      int sx = (int)((long)tx * ctx->canvasW / ctx->targetW);
      int rel = sx - pDraw->iX;
      if (rel < 0 || rel >= pDraw->iWidth) continue; // outside this frame's rect: keep previous pixel
      uint8_t idx = src[rel];
      if (hasTrans && idx == transIdx) continue;     // transparent: keep previous pixel
      uint8_t r = pal[idx * 3 + 0], g = pal[idx * 3 + 1], b = pal[idx * 3 + 2];
      uint16_t val = ((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3);
      rowBuf[tx] = (uint16_t)(((val & 0xFF) << 8) | (val >> 8)); // byte-swap to match convert_sprites.py
    }
    emitRow(ctx);
  }
}

// Decodes gifPath into binPath in the [count][frames...] wire format the
// display path reads. Returns false on open/decode failure.
bool decodeGifToBin(const char *gifPath, const char *binPath, int targetW, int targetH) {
  // AnimatedGIF's internal state (~24KB of LZW/line/palette buffers) is big, so
  // allocate it on the heap only for the duration of a decode rather than
  // paying for it in .bss for the whole uptime.
  AnimatedGIF *gif = new AnimatedGIF();
  if (!gif) return false;
  gif->begin(GIF_PALETTE_RGB888);
  if (!gif->open(gifPath, gifOpenCB, gifCloseCB, gifReadCB, gifSeekCB, gifDrawCB)) {
    Serial.printf("[gif] open failed err=%d\n", gif->getLastError());
    delete gif;
    return false;
  }

  GifDecodeCtx ctx;
  ctx.canvasW = gif->getCanvasWidth();
  ctx.canvasH = gif->getCanvasHeight();
  ctx.targetW = targetW;
  ctx.targetH = targetH;
  ctx.rowBytes = (size_t)targetW * 2;
  ctx.hasPrev = false;
  size_t frameBytes = (size_t)targetW * targetH * 2;

  ctx.out = LittleFS.open(binPath, "w");
  if (!ctx.out) {
    gif->close();
    delete gif;
    return false;
  }
  ctx.out.write((uint8_t)0); // placeholder frame count, patched once we know the total

  uint8_t count = 0;
  int delayMs = 0, more = 1;
  while (count < MAX_CUSTOM_FRAMES) {
    ctx.producedRow = 0;
    ctx.hasPrev = false;
    if (count > 0) {
      ctx.out.flush(); // make the just-written previous frame visible to the read handle
      ctx.prevFile = LittleFS.open(binPath, "r");
      ctx.hasPrev = (bool)ctx.prevFile;
      if (ctx.hasPrev) ctx.prevFile.seek(1 + (size_t)(count - 1) * frameBytes);
    }

    more = gif->playFrame(false, &delayMs, &ctx);

    if (more >= 0) {
      // finalize any bottom rows this frame never touched
      while (ctx.producedRow < ctx.targetH) emitPrevRow(&ctx);
      count++;
    }
    if (ctx.prevFile) ctx.prevFile.close();
    if (more <= 0) break; // 0 = last frame, <0 = decode error
    yield();              // feed the WDT between frames
  }
  gif->close();
  delete gif;
  ctx.out.close();

  if (count == 0) {
    LittleFS.remove(binPath);
    return false;
  }
  File patch = LittleFS.open(binPath, "r+");
  if (patch) {
    patch.seek(0);
    patch.write(count);
    patch.close();
  }
  Serial.printf("[gif] decoded %d frame(s) %dx%d -> %dx%d\n", count, ctx.canvasW, ctx.canvasH, targetW, targetH);
  return true;
}

// ---------- sprite upload (raw .gif -> on-device decode) ----------
// ESP8266WebServer fully buffers a plain POST body into a heap String before
// the handler runs, which a whole GIF would blow RAM on - so we take the
// upload over its streaming multipart/HTTPUpload path, writing the raw .gif to
// LittleFS in small chunks, then decode it on the done callback.
File uploadFile;

void handleSpriteUploadChunk(const char *gifPath) {
  HTTPUpload &upload = webServer.upload();
  if (upload.status == UPLOAD_FILE_START) {
    uploadFile = LittleFS.open(gifPath, "w");
  } else if (upload.status == UPLOAD_FILE_WRITE) {
    if (uploadFile) uploadFile.write(upload.buf, upload.currentSize);
  } else if (upload.status == UPLOAD_FILE_END || upload.status == UPLOAD_FILE_ABORTED) {
    if (uploadFile) uploadFile.close();
  }
}

void handleSpriteUploadDone(ActiveApp slot) {
  const char *gifPath = (slot == APP_CLAUDE) ? CLAUDE_GIF_FILE : CODEX_GIF_FILE;
  const char *binPath = (slot == APP_CLAUDE) ? CLAUDE_SPRITE_FILE : CODEX_SPRITE_FILE;
  int tw = (slot == APP_CLAUDE) ? CLAUDE_SPRITE_W : CODEX_SPRITE_W;
  int th = (slot == APP_CLAUDE) ? CLAUDE_SPRITE_H : CODEX_SPRITE_H;

  bool ok = decodeGifToBin(gifPath, binPath, tw, th);
  LittleFS.remove(gifPath); // temp raw gif no longer needed once decoded

  spriteRev++;
  loadCustomSpriteState();
  if (slot == APP_CLAUDE) claudeFrame = 0;
  else codexFrame = 0;
  if (currentApp == slot) drawActiveApp();

  if (ok) {
    webServer.send(200, "text/plain", "ok");
    Serial.println("[sprite] gif decoded & applied");
  } else {
    webServer.send(500, "text/plain", "gif decode failed (too large or unsupported?)");
    Serial.println("[sprite] gif decode FAILED");
  }
}

void setupWebServer() {
  webServer.on("/", HTTP_GET, handleRoot);
  webServer.on("/save", HTTP_POST, handleSave);
  webServer.on("/reset-wifi", HTTP_POST, handleResetWifi);
  webServer.on("/api/info", HTTP_GET, handleApiInfo);
  webServer.on("/api/display", HTTP_POST, handleApiDisplay);
  webServer.on("/api/bridge", HTTP_POST, handleApiBridge);
  webServer.on("/api/brightness", HTTP_POST, handleApiBrightness);
  webServer.on("/sprite/claude/reset", HTTP_POST, []() { handleSpriteReset(APP_CLAUDE); });
  webServer.on("/sprite/codex/reset", HTTP_POST, []() { handleSpriteReset(APP_CODEX); });
  webServer.on("/sprite/claude/raw", HTTP_GET, []() { handleSpriteRaw(APP_CLAUDE); });
  webServer.on("/sprite/codex/raw", HTTP_GET, []() { handleSpriteRaw(APP_CODEX); });
  webServer.on(
      "/sprite/claude", HTTP_POST, []() { handleSpriteUploadDone(APP_CLAUDE); },
      []() { handleSpriteUploadChunk(CLAUDE_GIF_FILE); });
  webServer.on(
      "/sprite/codex", HTTP_POST, []() { handleSpriteUploadDone(APP_CODEX); },
      []() { handleSpriteUploadChunk(CODEX_GIF_FILE); });
  webServer.begin();
  Serial.printf("[web] admin server listening on http://%s/\n", WiFi.localIP().toString().c_str());
}

void serviceWiFi() {
  unsigned long nowMs = millis();

  if (WiFi.status() == WL_CONNECTED) {
    wifiDisconnectedSinceMs = 0;
    serviceNtp();
    if (wifiPortalActive) {
      wifiManager.stopConfigPortal();
      wifiPortalActive = false;
    }
    if (!webServerStarted) {
      setupWebServer();
      webServerStarted = true;
      Serial.printf("[wifi] connected ssid=%s ip=%s\n", WiFi.SSID().c_str(),
                    WiFi.localIP().toString().c_str());
    }
    webServer.handleClient();
    return;
  }

  if (wifiDisconnectedSinceMs == 0) wifiDisconnectedSinceMs = nowMs;

  if (wifiPortalActive && usbBridgeActive()) {
    Serial.println("[wifi] USB connected; stopping config portal");
    wifiManager.stopConfigPortal();
    wifiPortalActive = false;
    WiFi.mode(WIFI_STA);
    WiFi.begin();
    lastWifiRetryMs = nowMs;
    drawStaticChrome();
    drawActiveApp();
  }

  // A USB-connected clock must remain fully usable without WiFi. Only expose
  // the provisioning screen when neither transport is available.
  if (!wifiPortalActive && !usbBridgeActive() &&
      nowMs - wifiDisconnectedSinceMs >= WIFI_PORTAL_DELAY_MS) {
    Serial.println("[wifi] no USB or WiFi; starting non-blocking config portal");
    wifiManager.startConfigPortal(WIFI_PORTAL_AP_NAME);
    wifiPortalActive = true;
  }

  if (wifiPortalActive) {
    wifiManager.process();
  } else if (nowMs - lastWifiRetryMs >= WIFI_RETRY_INTERVAL_MS) {
    lastWifiRetryMs = nowMs;
    Serial.println("[wifi] retrying saved network in background");
    WiFi.begin();
  }
}

// ---------- Arduino entry points ----------

void setup() {
  Serial.setRxBufferSize(1024);
  Serial.begin(460800);
  LittleFS.begin();
  loadBridgeHost();
  loadUtcOffset();
  loadBrightness();
  loadCustomSpriteState();

  tft.init();
  tft.setRotation(0);
  tft.fillScreen(TFT_BLACK);
  analogWriteFreq(BRIGHTNESS_PWM_FREQ);
  analogWriteRange(100); // duty maps 1:1 to a 0-100 percentage
  applyBrightness();

  sendUsbFrame("hello");
  drawStaticChrome();
  drawActiveApp();
  setupWiFi();
  pollBridge();
}

void loop() {
  handleUsbSerial();
  serviceWiFi();
  unsigned long nowMs = millis();

  // Effective mode may differ from the configured one (AUTO -> music while
  // audio plays). On a transition, reset the incoming mode's chrome so it
  // repaints cleanly, and repaint the pet immediately when returning to it.
  DisplayMode eff = effectiveMode();
  if (eff != lastEffectiveMode) {
    lastEffectiveMode = eff;
    drawEffectiveMode(eff, true, nowMs);
  }

  if (eff == MODE_NET) {
    // net-speed mode: rendering (constant-rate sweep) is independent of the
    // bridge polls that refill its sample queue
    if (nowMs - lastNetDrawMs >= NET_DRAW_INTERVAL_MS) {
      lastNetDrawMs = nowMs;
      netDrawTick();
    }
    if (nowMs - lastNetPollMs >= NET_POLL_INTERVAL_MS) {
      lastNetPollMs = nowMs;
      pollNet();
    }
  } else if (eff == MODE_MUSIC) {
    // music now-playing mode: cover art + track metadata from the bridge
    if (nowMs - lastMusicPollMs >= MUSIC_POLL_INTERVAL_MS) {
      lastMusicPollMs = nowMs;
      pollMusic();
    }
  } else if (eff == MODE_DUAL) {
    // Static page; incoming status frames repaint only changed quota sections.
  } else if (eff == MODE_DOMESTIC) {
    if (nowMs - lastFlashMs >= FLASH_INTERVAL_MS) {
      lastFlashMs = nowMs;
      flashOn = !flashOn;
      if (domesticStatus.needsInput && flashOn) drawFullBorder(TFT_RED);
      else if (domesticStatus.needsInput) drawDomesticScreen(true);
    }
  } else if (eff == MODE_STOCK) {
    if (usbBlob.active && (usbBlob.kind == USB_STOCK_NAMES || usbBlob.kind == USB_STOCK_NAMES_RLE)) return;
    if (nowMs - lastStockPollMs >= STOCK_POLL_INTERVAL_MS) {
      lastStockPollMs = nowMs;
      pollStock();
    }
    int pageCount = stockCount > 0 ? (stockCount + STOCK_ROWS_PER_PAGE - 1) / STOCK_ROWS_PER_PAGE : 1;
    if (pageCount > 1 && nowMs - lastStockPageMs >= STOCK_PAGE_INTERVAL_MS) {
      lastStockPageMs = nowMs;
      stockPage = (stockPage + 1) % pageCount;
      if (!drawBufferedUiBlob(USB_STOCK_NAMES_RLE, STOCK_UI_CACHE_FILE, true))
        drawStockScreen(true);
    }
    if (!stockChromeDrawn || stockDirty) drawStockScreen();
  } else if (eff == MODE_WEATHER) {
    if (usbBlob.active && (usbBlob.kind == USB_WEATHER_LABELS || usbBlob.kind == USB_WEATHER_LABELS_RLE)) return;
    if (nowMs - lastWeatherPollMs >= WEATHER_POLL_INTERVAL_MS) {
      lastWeatherPollMs = nowMs;
      pollWeather();
    }
    if (!weatherChromeDrawn) drawWeatherScreen(true);
    if (nowMs - lastWeatherClockMs >= 1000) {
      lastWeatherClockMs = nowMs;
      drawWeatherClock();
    }
    if (nowMs - lastWeatherAnimMs >= 350) {
      lastWeatherAnimMs = nowMs;
      weatherAnimFrame++;
      drawWeatherAnimation();
    }
  } else if (eff == MODE_SCREENSAVER) {
    drawScreenSaver(false);
  } else {
    // Completion uses a dedicated 70ms pulse timer. Paint it before the pet
    // frame so it is not coupled to the slower urgent-alert cadence.
    if (!bridgeStale() && !currentAppNeedsInput()) drawCompletionPulse(nowMs);

    // sprite walk-cycle animation (only advances while that app is showing)
    if (nowMs - lastAnimMs >= ANIM_INTERVAL_MS) {
      lastAnimMs = nowMs;
      bool claudeWorking = claudeStatus.status == "working";
      bool codexWorking = codexStatus.status == "working";
      if (showingCd != CD_NONE) {
        // countdown owns the center area: no sprite frames over it
      } else if (currentApp == APP_CLAUDE && claudeWorking) {
        claudeFrame = (claudeFrame + 1) % claudeFrameCount();
        drawClaudeSprite(claudeFrame);
      } else if (currentApp == APP_CODEX && (codexWorking || completionAlertActive())) {
        codexFrame = (codexFrame + 1) % codexFrameCount();
        drawCodexSprite(codexFrame);
      }
    }

    // countdown seconds tick locally between bridge polls
    static unsigned long lastCdTickMs = 0;
    if (showingCd != CD_NONE && nowMs - lastCdTickMs >= 1000) {
      lastCdTickMs = nowMs;
      drawCountdown(false);
    }

    // "urgent" flash toggle (independent, faster cadence)
    if ((!completionAlertActive() || bridgeStale() || currentAppNeedsInput())
        && nowMs - lastFlashMs >= FLASH_INTERVAL_MS) {
      lastFlashMs = nowMs;
      flashOn = !flashOn;
      if (bridgeStale()) {
        redrawRingOnly();
      } else if (currentAppNeedsInput()) {
        // approval needed: blink the whole border red, restore the quota ring
        // on the off-phase so it doesn't erase the normal chrome permanently
        if (flashOn) drawFullBorder(TFT_RED);
        else redrawRingOnly();
      }
    }

    // alternate which app is shown when neither/both are uniquely working
    if (updateActiveApp()) {
      drawActiveApp();
    }
  }

  // status poll continues in every mode (feeds /api/info and the web page)
  if (nowMs - lastPollMs >= BRIDGE_POLL_INTERVAL_MS) {
    lastPollMs = nowMs;
    pollBridge();
  }
}
