using System.IO.Ports;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AIClockBridge;

// USB transport for the CH340-backed ESP8266 serial port. Business frames are
// prefixed so firmware debug output can safely share the same byte stream.
sealed class SerialBridge : IDisposable
{
    const string Prefix = "@AICLOCK ";
    const int BaudRate = 460800;
    // ESP8266's UART receive buffer cannot reliably absorb a full 512-byte
    // COBS payload. A 192-byte payload stays below the 256-byte
    // receive buffer after framing while avoiding excessive ACK round-trips.
    const int BlobChunkBytes = 192;
    readonly StatusService _status;
    readonly NetSpeedMonitor _net;
    readonly NowPlayingMonitor _music;
    readonly StockMonitor _stocks;
    readonly WeatherMonitor _weather;
    readonly object _writeLock = new();
    readonly object _receiveLock = new();
    readonly System.Threading.Timer _scanTimer;
    readonly System.Threading.Timer _pushTimer;
    SerialPort _port;
    DateTime _lastHelloAt = DateTime.MinValue;
    DateTime _lastNetAt = DateTime.MinValue;
    DateTime _lastMusicAt = DateTime.MinValue;
    DateTime _lastStockAt = DateTime.MinValue;
    DateTime _lastWeatherAt = DateTime.MinValue;
    DateTime _lastPingAt = DateTime.MinValue;
    DateTime _portOpenedAt = DateTime.MinValue;
    DeviceInfo _deviceInfo;
    bool _scanning;
    bool _statusAckLogged;
    readonly List<byte> _textBuffer = new();
    readonly List<byte> _binaryBuffer = new();
    bool _insideBinaryFrame;
    readonly SemaphoreSlim _transferLock = new(1, 1);
    readonly object _ackLock = new();
    TaskCompletionSource<bool> _ackWaiter;
    ushort _ackTransfer;
    int _ackSeq;
    string _ackStage = "";
    int _nextTransferId;
    int _lastTextRev = -1;
    int _lastArtworkRev = -1;
    int _musicImageTransfer;
    int _visualImageTransfer;
    int _pendingImageTransfer;
    int _lastStockNamesRev = -1;
    int _lastWeatherTextRev = -1;
    readonly bool _telemetryEnabled;
    volatile bool _transferActive;
    MemoryStream _download;
    TaskCompletionSource<byte[]> _downloadWaiter;
    ushort _downloadTransfer;
    int _downloadNextSeq;
    int _downloadExpectedSize;
    uint _downloadExpectedCrc;

    public bool Connected => _port?.IsOpen == true
        && DateTime.UtcNow - _lastHelloAt < TimeSpan.FromSeconds(15);
    public string PortName => Connected ? _port.PortName : "";
    public DeviceInfo DeviceInfo => Connected ? _deviceInfo : null;

    public SerialBridge(StatusService status, NetSpeedMonitor net, NowPlayingMonitor music,
                        StockMonitor stocks = null, WeatherMonitor weather = null,
                        bool telemetryEnabled = true)
    {
        _status = status;
        _net = net;
        _music = music;
        _stocks = stocks;
        _weather = weather;
        _telemetryEnabled = telemetryEnabled;
        _scanTimer = new System.Threading.Timer(_ => Scan(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        _pushTimer = new System.Threading.Timer(_ => Push(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    void Scan()
    {
        if (_port?.IsOpen == true || _scanning) return;
        _scanning = true;
        try
        {
            var preferred = Settings.Get("serial_port");
            var ports = CandidatePorts(preferred);
            foreach (var name in ports)
            {
                try
                {
                    var port = new SerialPort(name, BaudRate)
                    {
                        Encoding = Encoding.UTF8,
                        NewLine = "\n",
                        ReadTimeout = 1000,
                        WriteTimeout = 1000,
                        DtrEnable = false,
                        RtsEnable = false,
                    };
                    port.DataReceived += OnDataReceived;
                    port.Open();
                    _port = port;
                    _portOpenedAt = DateTime.UtcNow;
                    Send("hello");
                    // Opening a USB serial port may reset some CH340 boards;
                    // allow the firmware enough time to boot and join WiFi.
                    for (var i = 0; i < 80 && !Connected; i++) Thread.Sleep(100);
                    if (Connected)
                    {
                        Settings.Set("serial_port", name);
                        Console.Error.WriteLine($"[usb] connected on {name}");
                        return;
                    }
                    ClosePort();
                }
                catch { ClosePort(); }
            }
        }
        finally { _scanning = false; }
    }

    static string[] CandidatePorts(string preferred)
    {
        var available = SerialPort.GetPortNames();
        var ch340 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\USB");
            foreach (var deviceId in usb?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (!deviceId.StartsWith("VID_1A86&PID_", StringComparison.OrdinalIgnoreCase)) continue;
                using var device = usb.OpenSubKey(deviceId);
                foreach (var instanceId in device?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var parameters = device.OpenSubKey(instanceId + @"\Device Parameters");
                    if (parameters?.GetValue("PortName") is string port && port.Length > 0)
                        ch340.Add(port);
                }
            }
        }
        catch { }
        var candidates = ch340.Count > 0 ? available.Where(ch340.Contains) : available;
        return candidates
            .OrderByDescending(p => string.Equals(p, preferred, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        byte[] bytes;
        try
        {
            var port = sender as SerialPort;
            var count = port?.BytesToRead ?? 0;
            if (count <= 0) return;
            bytes = new byte[count];
            var read = port.Read(bytes, 0, count);
            if (read != bytes.Length) Array.Resize(ref bytes, read);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[usb] receive failed: {error.Message}");
            ClosePort();
            return;
        }

        lock (_receiveLock)
        {
            foreach (var value in bytes)
            {
                if (value == 0)
                {
                    if (_insideBinaryFrame)
                    {
                        if (_binaryBuffer.Count > 0) ProcessBinaryFrame(_binaryBuffer.ToArray());
                        _binaryBuffer.Clear();
                        _insideBinaryFrame = false;
                    }
                    else
                    {
                        _textBuffer.Clear();
                        _insideBinaryFrame = true;
                    }
                    continue;
                }
                if (_insideBinaryFrame)
                {
                    if (value == '\n')
                    {
                        var prefix = Encoding.ASCII.GetBytes(Prefix);
                        var start = FindSequence(_binaryBuffer, prefix);
                        if (start >= 0)
                        {
                            var line = Encoding.UTF8.GetString(_binaryBuffer.Skip(start).ToArray()).TrimEnd('\r');
                            _binaryBuffer.Clear();
                            _insideBinaryFrame = false;
                            ProcessTextLine(line);
                            continue;
                        }
                    }
                    if (_binaryBuffer.Count < 2048) _binaryBuffer.Add(value);
                    else { _binaryBuffer.Clear(); _insideBinaryFrame = false; }
                }
                else if (value == '\n')
                {
                    var line = Encoding.UTF8.GetString(_textBuffer.ToArray()).TrimEnd('\r');
                    _textBuffer.Clear();
                    ProcessTextLine(line);
                }
                else if (_textBuffer.Count < 8192) _textBuffer.Add(value);
                else _textBuffer.Clear();
            }
        }
    }

    static int FindSequence(List<byte> source, byte[] pattern)
    {
        for (var i = 0; i <= source.Count - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
                if (source[i + j] != pattern[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    void ProcessTextLine(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return;
        try
        {
            using var doc = JsonDocument.Parse(line[Prefix.Length..]);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
            if (type == "hello" || type == "hello_ack")
            {
                _lastHelloAt = DateTime.UtcNow;
                Send("get_info");
            }
            else if (type == "info" && root.TryGetProperty("data", out var data))
            {
                _deviceInfo = ParseInfo(data);
                _lastHelloAt = DateTime.UtcNow;
                QueuePendingImages();
            }
            else if (type == "status_ack" && !_statusAckLogged)
            {
                _statusAckLogged = true;
                Console.Error.WriteLine($"[usb] status acknowledged by {_port.PortName}");
            }
            ProcessTransferMessage(type, root);
        }
        catch { }
    }

    void ProcessTransferMessage(string type, JsonElement root)
    {
        if (type == "binary_ack")
        {
            _lastHelloAt = DateTime.UtcNow;
            var transfer = (ushort)Int(root, "transfer");
            var seq = Int(root, "seq", -1);
            var stage = Str(root, "stage");
            var ok = !root.TryGetProperty("ok", out var value) || value.GetBoolean();
            var reason = Str(root, "reason");
            if (!ok) Console.Error.WriteLine($"[usb] device rejected {stage} transfer={transfer} seq={seq} reason={reason}");
            lock (_ackLock)
            {
                if (_ackWaiter != null && transfer == _ackTransfer
                    && seq == _ackSeq && stage == _ackStage)
                    _ackWaiter.TrySetResult(ok);
            }
        }
        else if (type == "binary_begin" && Str(root, "direction") == "device")
        {
            var transfer = (ushort)Int(root, "transfer");
            if (transfer != _downloadTransfer) return;
            _downloadExpectedSize = Int(root, "size");
            _downloadExpectedCrc = UInt(root, "crc32");
            _downloadNextSeq = 0;
            _download = new MemoryStream(Math.Max(0, _downloadExpectedSize));
        }
        else if (type == "binary_end" && Str(root, "direction") == "device")
        {
            var transfer = (ushort)Int(root, "transfer");
            if (transfer != _downloadTransfer || _download == null) return;
            var data = _download.ToArray();
            var ok = data.Length == _downloadExpectedSize && Crc32(data) == _downloadExpectedCrc;
            _downloadWaiter?.TrySetResult(ok ? data : null);
            Send("binary_received", new() { ["transfer"] = (int)transfer, ["ok"] = ok });
        }
    }

    void ProcessBinaryFrame(byte[] encoded)
    {
        var frame = CobsDecode(encoded);
        if (frame == null || frame.Length < 12 || frame[0] != 1) return;
        var transfer = (ushort)(frame[2] | frame[3] << 8);
        var seq = frame[4] | frame[5] << 8;
        var length = frame[6] | frame[7] << 8;
        if (length > 512 || frame.Length != 12 + length) return;
        var expected = BitConverter.ToUInt32(frame, 8 + length);
        if (Crc32(frame.AsSpan(0, 8 + length)) != expected) return;
        _lastHelloAt = DateTime.UtcNow;
        if (transfer != _downloadTransfer || seq != _downloadNextSeq || _download == null) return;
        _download.Write(frame, 8, length);
        _downloadNextSeq++;
    }

    static DeviceInfo ParseInfo(JsonElement root)
    {
        var info = new DeviceInfo
        {
            Ip = Str(root, "ip"), Ssid = Str(root, "ssid"), Bridge = Str(root, "bridge"),
            Mode = Str(root, "mode", "auto"), Effective = Str(root, "effective", "auto"),
            Showing = Str(root, "showing"), LastUpdateS = Int(root, "last_update_s", -1),
            SpriteRev = Int(root, "sprite_rev"), Brightness = Int(root, "brightness", 100),
        };
        if (root.TryGetProperty("claude", out var c))
        {
            info.ClaudeCustomSprite = Bool(c, "custom_sprite");
            info.ClaudeW = Int(c, "w", 111); info.ClaudeH = Int(c, "h", 120);
        }
        if (root.TryGetProperty("codex", out var x))
        {
            info.CodexCustomSprite = Bool(x, "custom_sprite");
            info.CodexW = Int(x, "w", 120); info.CodexH = Int(x, "h", 120);
        }
        if (root.TryGetProperty("ui_cache", out var cache))
        {
            info.StockUiCached = Bool(cache, "stock");
            info.WeatherUiCached = Bool(cache, "weather");
            info.StockUiCacheCrc = UInt(cache, "stock_crc");
            info.WeatherUiCacheCrc = UInt(cache, "weather_crc");
        }
        return info;
    }

    void Push()
    {
        if (_port?.IsOpen != true) return;
        if (!Connected)
        {
            Send("hello");
            if (DateTime.UtcNow - _portOpenedAt > TimeSpan.FromSeconds(20)) ClosePort();
            return;
        }
        if (!_telemetryEnabled || _transferActive) return;
        if (DateTime.UtcNow - _lastPingAt >= TimeSpan.FromSeconds(5))
        {
            _lastPingAt = DateTime.UtcNow;
            Send("hello");
        }
        SendRaw("status", _status.Snapshot().ToJson());
        var now = DateTime.UtcNow;
        var activeMode = ActiveDisplayMode();
        if (now - _lastNetAt >= TimeSpan.FromSeconds(2))
        {
            _lastNetAt = now;
            SendRaw("net", _net.ToJson());
        }
        if (now - _lastMusicAt >= TimeSpan.FromSeconds(2))
        {
            _lastMusicAt = now;
            SendRaw("music", _music.ToJson()); // also renders the CJK text strip
        }
        if (_stocks != null && now - _lastStockAt >= TimeSpan.FromSeconds(5)
            && !(activeMode == "stock" && _stocks.NamesRev != _lastStockNamesRev))
        {
            _lastStockAt = now;
            SendRaw("stock", _stocks.ToJson());
        }
        if (_weather != null && now - _lastWeatherAt >= TimeSpan.FromSeconds(15)
            && !(activeMode == "weather" && _weather.TextRev != _lastWeatherTextRev))
        {
            _lastWeatherAt = now;
            SendRaw("weather", _weather.ToJson());
        }
        if (_deviceInfo != null) QueuePendingImages();
    }

    void QueuePendingImages()
    {
        if (Interlocked.CompareExchange(ref _pendingImageTransfer, 1, 0) == 0)
            _ = Task.Run(PushPendingImages);
    }

    async Task PushPendingImages()
    {
        try
        {
            // After a reconnect all telemetry timers are due at once. Let the
            // ESP8266 drain those small JSON frames before a COBS transfer.
            await Task.Delay(150);
            // Stock/weather labels are device-side caches, so preload both
            // after every connection even when neither page is visible.
            await PushVisualImagesOnce("stock");
            await PushVisualImagesOnce("weather");
            if (ActiveDisplayMode() == "music") await PushMusicImagesOnce();
        }
        finally { Interlocked.Exchange(ref _pendingImageTransfer, 0); }
    }

    Task PushMusicImagesOnce()
    {
        if (Interlocked.CompareExchange(ref _musicImageTransfer, 1, 0) != 0)
            return Task.CompletedTask;
        return PushMusicImages();
    }

    Task PushVisualImagesOnce(string mode)
    {
        if (Interlocked.CompareExchange(ref _visualImageTransfer, 1, 0) != 0)
            return Task.CompletedTask;
        return PushVisualImages(mode);
    }

    async Task PushMusicImages()
    {
        try
        {
            var textRev = _music.TextRev;
            var text = _music.TextRgb565;
            if (textRev != _lastTextRev && text.Length > 0
                && await SendBlob("music_text", text, NowPlayingMonitor.TextW, NowPlayingMonitor.TextH, 15))
            {
                _lastTextRev = textRev;
                Console.Error.WriteLine($"[usb] music text sent ({text.Length} bytes)");
            }

            var snapshot = _music.Snapshot;
            var cover = _music.CoverRgb565;
            if (snapshot.ArtworkRev != _lastArtworkRev && cover.Length > 0
                && await SendBlob("music_cover", cover, 128, 128, 20))
            {
                _lastArtworkRev = snapshot.ArtworkRev;
                Console.Error.WriteLine($"[usb] music cover sent ({cover.Length} bytes)");
            }
        }
        finally { Interlocked.Exchange(ref _musicImageTransfer, 0); }
    }

    string ActiveDisplayMode()
    {
        var configured = _deviceInfo?.Mode ?? "";
        return configured == "auto" ? _deviceInfo?.Effective ?? "" : configured;
    }

    async Task PushVisualImages(string mode)
    {
        try
        {
            if (mode == "stock" && _stocks != null && _stocks.NamesRev != _lastStockNamesRev)
            {
                var rev = _stocks.NamesRev;
                var data = _stocks.NameBitmap;
                var packed = Rgb565.Compress(data);
                var crc = Crc32(packed);
                SendRaw("stock", _stocks.ToJson());
                if (_deviceInfo?.StockUiCacheCrc is > 0 && _deviceInfo.StockUiCacheCrc == crc)
                {
                    _lastStockNamesRev = rev;
                    Console.Error.WriteLine("[usb] stock names cache hit");
                }
                else if (data.Length > 0 && packed.Length > 0
                    && await SendBlob("stock_names_rle", packed, StockMonitor.NameW, StockMonitor.NameH * StockMonitor.MaxRows, 15))
                {
                    Console.Error.WriteLine($"[usb] stock cache miss device={_deviceInfo?.StockUiCacheCrc ?? 0:X8} local={crc:X8}");
                    _lastStockNamesRev = rev;
                    if (_deviceInfo != null) { _deviceInfo.StockUiCached = true; _deviceInfo.StockUiCacheCrc = crc; }
                    Console.Error.WriteLine($"[usb] stock names sent ({packed.Length}/{data.Length} bytes RLE)");
                }
            }
            if (mode == "weather" && _weather != null && _weather.TextRev != _lastWeatherTextRev)
            {
                var rev = _weather.TextRev;
                var header = _weather.HeaderBitmap;
                var date = _weather.DateBitmap;
                var air = _weather.AirBitmap;
                var labels = new byte[header.Length + date.Length + air.Length];
                Buffer.BlockCopy(header, 0, labels, 0, header.Length);
                Buffer.BlockCopy(date, 0, labels, header.Length, date.Length);
                Buffer.BlockCopy(air, 0, labels, header.Length + date.Length, air.Length);
                var packed = Rgb565.Compress(labels);
                var crc = Crc32(packed);
                SendRaw("weather", _weather.ToJson());
                if (_deviceInfo?.WeatherUiCacheCrc is > 0 && _deviceInfo.WeatherUiCacheCrc == crc)
                {
                    _lastWeatherTextRev = rev;
                    Console.Error.WriteLine("[usb] weather labels cache hit");
                }
                else if (header.Length > 0 && date.Length > 0 && air.Length > 0
                    && packed.Length > 0 && await SendBlob("weather_labels_rle", packed, 0, 0, 15))
                {
                    Console.Error.WriteLine($"[usb] weather cache miss device={_deviceInfo?.WeatherUiCacheCrc ?? 0:X8} local={crc:X8}");
                    _lastWeatherTextRev = rev;
                    if (_deviceInfo != null) { _deviceInfo.WeatherUiCached = true; _deviceInfo.WeatherUiCacheCrc = crc; }
                    Console.Error.WriteLine($"[usb] weather labels sent ({packed.Length}/{labels.Length} bytes RLE)");
                }
            }
        }
        finally { Interlocked.Exchange(ref _visualImageTransfer, 0); }
    }

    public void SetDisplayMode(string mode)
    {
        // Prime the numeric payload before changing pages, then start the CJK
        // bitmap transfer immediately. Previously this waited for the next
        // 1-second push tick plus its 150ms reconnect guard, so Latin digits
        // visibly appeared well before stock/weather Chinese labels.
        if (mode == "stock" && _stocks != null) SendRaw("stock", _stocks.ToJson());
        if (mode == "weather" && _weather != null) SendRaw("weather", _weather.ToJson());
        if (_deviceInfo != null) _deviceInfo.Mode = mode == "screensaver_preview" ? "screensaver" : mode;
        if (mode == "music") { _lastTextRev = -1; _lastArtworkRev = -1; }
        Send("set_display", new() { ["mode"] = mode });
        if (mode == "net")
        {
            _lastNetAt = DateTime.UtcNow;
            SendRaw("net", _net.ToJson());
        }
        if (mode == "music") _ = PushMusicImagesOnce();
        if (mode == "stock" || mode == "weather") _ = PushVisualImagesOnce(mode);
    }

    public void SetBrightness(int level)
    {
        if (_deviceInfo != null) _deviceInfo.Brightness = level;
        Send("set_brightness", new() { ["level"] = level });
    }
    public void PushWeather() { if (_weather != null) SendRaw("weather", _weather.ToJson()); }
    public void RequestInfo() => Send("get_info");

    public Task<bool> UploadGif(byte[] gif, string slot) =>
        SendBlob(slot == "claude" ? "gif_claude" : "gif_codex", gif, 0, 0, 90);

    public Task<bool> SendMusicCoverForTest(byte[] cover) =>
        SendBlob("music_cover", cover, 128, 128, 20);

    public void ResetSprite(string slot) => Send("reset_sprite", new() { ["slot"] = slot });

    public async Task<byte[]> FetchSpriteRaw(string slot)
    {
        await _transferLock.WaitAsync();
        _transferActive = true;
        try
        {
            var transfer = NextTransferId();
            _downloadTransfer = transfer;
            _download = null;
            _downloadWaiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Send("get_sprite_usb", new() { ["slot"] = slot, ["transfer"] = (int)transfer });
            var completed = await Task.WhenAny(_downloadWaiter.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (completed != _downloadWaiter.Task || await _downloadWaiter.Task is not { } data)
                throw new DeviceException("USB 动画读取超时或校验失败");
            return data;
        }
        finally
        {
            _download = null;
            _downloadWaiter = null;
            _transferActive = false;
            _transferLock.Release();
        }
    }

    async Task<bool> SendBlob(string kind, byte[] data, int width, int height, int timeoutSeconds)
    {
        if (!Connected) return false;
        await _transferLock.WaitAsync();
        _transferActive = true;
        try
        {
            var transfer = NextTransferId();
            var crc = Crc32(data);
            var begin = new Dictionary<string, object>
            {
                ["kind"] = kind, ["transfer"] = (int)transfer, ["size"] = data.Length,
                ["crc32"] = crc, ["width"] = width, ["height"] = height,
            };
            if (!await SendAndWaitAck(transfer, -1, "begin", () => Send("binary_begin", begin), 5))
            {
                Console.Error.WriteLine($"[usb] {kind} begin rejected/timeout (transfer {transfer})");
                return false;
            }
            var seq = 0;
            for (var offset = 0; offset < data.Length; offset += BlobChunkBytes, seq++)
            {
                var length = Math.Min(BlobChunkBytes, data.Length - offset);
                var sent = false;
                for (var attempt = 0; attempt < 3 && !sent; attempt++)
                    sent = await SendAndWaitAck(transfer, seq, "chunk",
                        () => WriteBinaryFrame(transfer, seq, data.AsSpan(offset, length)), 3);
                if (!sent)
                {
                    Console.Error.WriteLine($"[usb] {kind} chunk {seq} failed (transfer {transfer})");
                    return false;
                }
            }
            var ended = await SendAndWaitAck(transfer, seq, "end", () => Send("binary_end", new()
            {
                ["transfer"] = (int)transfer,
            }), timeoutSeconds);
            if (!ended) Console.Error.WriteLine($"[usb] {kind} end rejected/timeout (transfer {transfer})");
            return ended;
        }
        catch { return false; }
        finally { _transferActive = false; _transferLock.Release(); }
    }

    async Task<bool> SendAndWaitAck(ushort transfer, int seq, string stage, Action send, int timeoutSeconds)
    {
        TaskCompletionSource<bool> waiter;
        lock (_ackLock)
        {
            _ackTransfer = transfer; _ackSeq = seq; _ackStage = stage;
            _ackWaiter = waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        send();
        var completed = await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        lock (_ackLock) if (ReferenceEquals(_ackWaiter, waiter)) _ackWaiter = null;
        return completed == waiter.Task && await waiter.Task;
    }

    ushort NextTransferId()
    {
        var value = (ushort)Interlocked.Increment(ref _nextTransferId);
        return value == 0 ? (ushort)Interlocked.Increment(ref _nextTransferId) : value;
    }

    void WriteBinaryFrame(ushort transfer, int seq, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[12 + payload.Length];
        frame[0] = 1; frame[1] = 1;
        frame[2] = (byte)transfer; frame[3] = (byte)(transfer >> 8);
        frame[4] = (byte)seq; frame[5] = (byte)(seq >> 8);
        frame[6] = (byte)payload.Length; frame[7] = (byte)(payload.Length >> 8);
        payload.CopyTo(frame.AsSpan(8));
        BitConverter.GetBytes(Crc32(frame.AsSpan(0, 8 + payload.Length))).CopyTo(frame, 8 + payload.Length);
        var encoded = CobsEncode(frame);
        lock (_writeLock)
        {
            try
            {
                if (_port?.IsOpen != true) return;
                _port.Write(new byte[] { 0 }, 0, 1);
                _port.Write(encoded, 0, encoded.Length);
                _port.Write(new byte[] { 0 }, 0, 1);
            }
            catch { ClosePort(); }
        }
    }

    void Send(string type, Dictionary<string, object> fields = null)
    {
        var message = fields ?? new();
        message["type"] = type;
        message["version"] = 1;
        WriteLine(JsonSerializer.Serialize(message));
    }

    void SendRaw(string type, byte[] json)
    {
        var body = Encoding.UTF8.GetString(json);
        WriteLine($"{{\"type\":\"{type}\",\"version\":1,\"data\":{body}}}");
    }

    void WriteLine(string json)
    {
        lock (_writeLock)
        {
            try { if (_port?.IsOpen == true) _port.WriteLine(Prefix + json); }
            catch { ClosePort(); }
        }
    }

    void ClosePort()
    {
        var port = Interlocked.Exchange(ref _port, null);
        if (port == null) return;
        try { port.DataReceived -= OnDataReceived; port.Close(); port.Dispose(); } catch { }
        _lastHelloAt = DateTime.MinValue;
        _portOpenedAt = DateTime.MinValue;
        _lastPingAt = DateTime.MinValue;
        _statusAckLogged = false;
        _lastTextRev = -1;
        _lastArtworkRev = -1;
        _lastStockNamesRev = -1;
        _lastWeatherTextRev = -1;
        lock (_receiveLock)
        {
            _textBuffer.Clear();
            _binaryBuffer.Clear();
            _insideBinaryFrame = false;
        }
        _deviceInfo = null;
    }

    public void Dispose()
    {
        _scanTimer.Dispose();
        _pushTimer.Dispose();
        ClosePort();
    }

    static string Str(JsonElement o, string k, string dflt = "")
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : dflt;
    static int Int(JsonElement o, string k, int dflt = 0)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : dflt;
    static bool Bool(JsonElement o, string k)
        => o.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();
    static uint UInt(JsonElement o, string k)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetUInt32() : 0;

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffff;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
        }
        return crc ^ 0xffffffff;
    }

    static byte[] CobsEncode(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length + input.Length / 254 + 2];
        var read = 0; var write = 1; var codeAt = 0; byte code = 1;
        while (read < input.Length)
        {
            if (input[read] == 0)
            {
                output[codeAt] = code; codeAt = write++; code = 1; read++;
            }
            else
            {
                output[write++] = input[read++];
                if (++code == 0xff)
                {
                    output[codeAt] = code; codeAt = write++; code = 1;
                }
            }
        }
        output[codeAt] = code;
        Array.Resize(ref output, write);
        return output;
    }

    static byte[] CobsDecode(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        var read = 0; var write = 0;
        while (read < input.Length)
        {
            var code = input[read++];
            if (code == 0 || read + code - 1 > input.Length) return null;
            for (var i = 1; i < code; i++) output[write++] = input[read++];
            if (code != 0xff && read < input.Length) output[write++] = 0;
        }
        Array.Resize(ref output, write);
        return output;
    }
}
