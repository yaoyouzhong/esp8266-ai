using System.Drawing;

namespace AIClockBridge;

sealed class WeatherSettingsForm : Form
{
    readonly WeatherMonitor _weather;
    readonly TextBox _host = new() { Dock = DockStyle.Fill };
    readonly TextBox _city = new() { Dock = DockStyle.Fill };
    readonly TextBox _apiKey = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    readonly CheckBox _autoLocation = new() { Text = "使用 Windows 自动定位", AutoSize = true };
    readonly Label _location = new() { AutoSize = true, ForeColor = Color.FromArgb(80, 97, 112) };
    readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly Button _locate = new() { Text = "获取当前位置", AutoSize = true };
    readonly Button _test = new() { Text = "测试连接", AutoSize = true };
    readonly Button _save = new() { Text = "保存并刷新", AutoSize = true };
    double _latitude;
    double _longitude;

    public WeatherSettingsForm(WeatherMonitor weather)
    {
        _weather = weather;
        Text = "天气设置";
        ClientSize = new Size(560, 500);
        MinimumSize = MaximumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(244, 248, 251);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(16, 47, 70) };
        header.Controls.Add(new Label
        {
            Text = "天气数据源",
            Font = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(26, 18),
        });
        header.Controls.Add(new Label
        {
            Text = "和风天气实况优先  ·  Open-Meteo 自动回退",
            ForeColor = Color.FromArgb(166, 221, 239),
            AutoSize = true,
            Location = new Point(29, 57),
        });
        root.Controls.Add(header, 0, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(26, 20, 26, 18),
            ColumnCount = 2,
            RowCount = 8,
            BackColor = BackColor,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.Controls.Add(body, 0, 1);

        AddField(body, 0, "API Host", _host, "控制台 → 设置，例如 abc.def.qweatherapi.com");
        AddField(body, 1, "地区", _city, "建议填写到区县，例如 南京市建邺区");
        body.Controls.Add(new Label { Text = "定位", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        var autoPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        autoPanel.Controls.Add(_autoLocation);
        autoPanel.Controls.Add(_locate);
        body.Controls.Add(autoPanel, 1, 2);
        body.Controls.Add(new Label { Text = "当前位置", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        body.Controls.Add(_location, 1, 3);
        AddField(body, 4, "API KEY", _apiKey, "保存在 Windows 凭据管理器，不写入配置和日志");

        body.Controls.Add(new Label { Text = "连接状态", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
        var statusPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(12, 10, 12, 8) };
        statusPanel.Controls.Add(_status);
        body.Controls.Add(statusPanel, 1, 5);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.Add(_save);
        actions.Controls.Add(cancel);
        actions.Controls.Add(_test);
        body.Controls.Add(actions, 0, 7);
        body.SetColumnSpan(actions, 2);

        _save.BackColor = Color.FromArgb(45, 127, 249);
        _save.ForeColor = Color.White;
        _save.FlatStyle = FlatStyle.Flat;
        _save.FlatAppearance.BorderSize = 0;
        _host.Text = WeatherMonitor.QWeatherApiHost;
        _city.Text = WeatherMonitor.City;
        _autoLocation.Checked = WeatherMonitor.AutoLocation;
        _latitude = WeatherMonitor.Latitude;
        _longitude = WeatherMonitor.Longitude;
        _apiKey.PlaceholderText = WeatherMonitor.HasQWeatherApiKey ? "已安全保存；留空保持不变" : "粘贴新建的 API KEY";
        _status.Text = WeatherMonitor.HasQWeatherApiKey ? "凭据已保存，等待测试连接。" : "尚未保存 API KEY。";
        UpdateLocationState();

        _autoLocation.CheckedChanged += (_, _) => UpdateLocationState();
        _locate.Click += async (_, _) => await Locate();
        _test.Click += async (_, _) => await TestConnection();
        _save.Click += (_, _) => SaveSettings();
        AcceptButton = _save;
        CancelButton = cancel;
    }

    static void AddField(TableLayoutPanel body, int row, string label, Control input, string hint)
    {
        body.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.Controls.Add(input, 0, 0);
        panel.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Color.FromArgb(98, 115, 130) }, 0, 1);
        body.Controls.Add(panel, 1, row);
    }

    void UpdateLocationState()
    {
        _city.Enabled = !_autoLocation.Checked;
        _locate.Enabled = _autoLocation.Checked;
        _location.Text = _latitude != 0 || _longitude != 0
            ? $"{_latitude:F4}, {_longitude:F4}（仅保存在本机）"
            : _autoLocation.Checked ? "尚未获取位置" : "使用手动地区";
    }

    async Task Locate()
    {
        SetBusy(true, "正在请求 Windows 定位…");
        try
        {
            var result = await WindowsLocation.Locate();
            _latitude = result.Latitude;
            _longitude = result.Longitude;
            UpdateLocationState();
            _status.Text = "定位成功。点击“测试连接”确认和风天气解析的区县。";
        }
        catch (Exception ex) { _status.Text = ex.Message.Trim(); }
        finally { SetBusy(false); }
    }

    async Task TestConnection()
    {
        SetBusy(true, "正在连接和风天气…");
        try
        {
            var snapshot = await _weather.TestQWeather(_host.Text, _apiKey.Text, _city.Text,
                _autoLocation.Checked, _latitude, _longitude);
            _status.Text = $"连接成功：{snapshot.City} · {snapshot.Condition} · {snapshot.Temperature:F0}℃ · 空气{snapshot.AirQuality}";
        }
        catch (Exception ex) { _status.Text = ex.Message.Trim(); }
        finally { SetBusy(false); }
    }

    void SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(_host.Text)) { _status.Text = "请输入 API Host。"; return; }
        if (!_autoLocation.Checked && string.IsNullOrWhiteSpace(_city.Text)) { _status.Text = "请输入地区。"; return; }
        if (_autoLocation.Checked && _latitude == 0 && _longitude == 0) { _status.Text = "请先获取当前位置。"; return; }
        if (string.IsNullOrWhiteSpace(_apiKey.Text) && !WeatherMonitor.HasQWeatherApiKey)
        { _status.Text = "请输入 API KEY。"; return; }
        try
        {
            _weather.ApplySettings(_host.Text, _city.Text, _autoLocation.Checked,
                _latitude, _longitude, _apiKey.Text);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { _status.Text = ex.Message.Trim(); }
    }

    void SetBusy(bool busy, string message = null)
    {
        _locate.Enabled = !busy && _autoLocation.Checked;
        _test.Enabled = !busy;
        _save.Enabled = !busy;
        if (message != null) _status.Text = message;
        UseWaitCursor = busy;
    }
}
