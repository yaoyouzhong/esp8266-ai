using System.Drawing;

namespace AIClockBridge;

sealed class StockSettingsForm : Form
{
    readonly StockMonitor _stocks;
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
    };
    readonly TextBox _symbol = new() { Dock = DockStyle.Fill, PlaceholderText = "例如 sh600519、hk00700、usAAPL" };
    readonly Label _summary = new() { AutoSize = true, ForeColor = Color.FromArgb(80, 97, 112) };
    readonly Label _status = new() { AutoSize = true, ForeColor = Color.FromArgb(80, 97, 112) };

    public StockSettingsForm(StockMonitor stocks)
    {
        _stocks = stocks;
        Text = "设置自选股";
        ClientSize = new Size(700, 540);
        MinimumSize = MaximumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(244, 248, 251);
        Font = new Font("Microsoft YaHei UI", 9F);

        AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(16, 47, 70),
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(28, 10, 24, 8),
        };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label
        {
            Text = "自选股与分页",
            Font = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold),
            ForeColor = Color.White,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = "最多 20 只  ·  每屏 4 只  ·  每 5 秒自动翻页",
            ForeColor = Color.FromArgb(166, 221, 239),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 1);
        root.Controls.Add(header, 0, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 18, 24, 18),
            RowCount = 5,
            ColumnCount = 1,
        };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.Controls.Add(body, 0, 1);

        var addRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 226));
        var add = new Button { Text = "添加", Dock = DockStyle.Fill, Margin = new Padding(8, 0, 8, 8) };
        addRow.Controls.Add(_symbol, 0, 0);
        addRow.Controls.Add(add, 1, 0);
        addRow.Controls.Add(new Label
        {
            Text = "6 位 A 股代码可自动识别市场",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = Color.FromArgb(98, 115, 130),
        }, 2, 0);
        body.Controls.Add(addRow, 0, 0);

        _list.Columns.Add("顺序", 58, HorizontalAlignment.Center);
        _list.Columns.Add("页码", 96, HorizontalAlignment.Center);
        _list.Columns.Add("股票代码", 135);
        _list.Columns.Add("股票名称", 175);
        _list.Columns.Add("屏幕位置", 120);
        body.Controls.Add(_list, 0, 1);

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var remove = new Button { Text = "删除", AutoSize = true };
        var up = new Button { Text = "上移", AutoSize = true };
        var down = new Button { Text = "下移", AutoSize = true };
        controls.Controls.Add(remove);
        controls.Controls.Add(up);
        controls.Controls.Add(down);
        controls.Controls.Add(_summary);
        _summary.Margin = new Padding(18, 7, 0, 0);
        body.Controls.Add(controls, 0, 2);
        body.Controls.Add(_status, 0, 3);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var save = new Button { Text = "保存并刷新", AutoSize = true };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.BackColor = Color.FromArgb(45, 127, 249);
        save.ForeColor = Color.White;
        save.FlatStyle = FlatStyle.Flat;
        save.FlatAppearance.BorderSize = 0;
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        body.Controls.Add(actions, 0, 4);

        foreach (var saved in StockMonitor.Symbols)
        {
            if (!StockMonitor.TryNormalizeSymbol(saved, out var normalized, out _)) continue;
            AddItem(normalized);
            if (!saved.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                _status.Text = $"已自动修正市场前缀：{saved} → {normalized}";
        }
        RefreshRows();

        add.Click += (_, _) => AddSymbol();
        _symbol.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            AddSymbol();
        };
        remove.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count == 0) return;
            _list.Items.Remove(_list.SelectedItems[0]);
            RefreshRows();
        };
        up.Click += (_, _) => MoveSelected(-1);
        down.Click += (_, _) => MoveSelected(1);
        save.Click += (_, _) => Save();
        CancelButton = cancel;
    }

    void AddSymbol()
    {
        if (_list.Items.Count >= StockMonitor.MaxSymbols)
        {
            _status.Text = $"最多只能配置 {StockMonitor.MaxSymbols} 只。";
            return;
        }
        if (!StockMonitor.TryNormalizeSymbol(_symbol.Text, out var normalized, out var error))
        {
            _status.Text = error;
            return;
        }
        if (_list.Items.Cast<ListViewItem>().Any(x =>
            x.Tag?.ToString()?.Equals(normalized, StringComparison.OrdinalIgnoreCase) == true))
        {
            _status.Text = $"{normalized} 已在列表中。";
            return;
        }
        var original = _symbol.Text.Trim();
        AddItem(normalized);
        _symbol.Clear();
        _status.Text = original.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            ? $"已添加 {normalized}。"
            : $"已自动修正并添加：{original} → {normalized}";
        RefreshRows();
    }

    void AddItem(string symbol)
    {
        var item = new ListViewItem { Tag = symbol };
        item.SubItems.Add("");
        item.SubItems.Add(symbol);
        item.SubItems.Add(_stocks.NameForSymbol(symbol) is { Length: > 0 } name ? name : "保存后获取");
        item.SubItems.Add("");
        _list.Items.Add(item);
    }

    void MoveSelected(int delta)
    {
        if (_list.SelectedItems.Count == 0) return;
        var item = _list.SelectedItems[0];
        var target = item.Index + delta;
        if (target < 0 || target >= _list.Items.Count) return;
        _list.Items.Remove(item);
        _list.Items.Insert(target, item);
        item.Selected = true;
        item.EnsureVisible();
        RefreshRows();
    }

    void RefreshRows()
    {
        for (var i = 0; i < _list.Items.Count; i++)
        {
            var page = i / StockMonitor.RowsPerPage + 1;
            var row = i % StockMonitor.RowsPerPage + 1;
            _list.Items[i].Text = (i + 1).ToString();
            _list.Items[i].SubItems[1].Text = $"第 {page} 页";
            _list.Items[i].SubItems[4].Text = $"第 {row} 行";
        }
        var pages = Math.Max(1, (_list.Items.Count + StockMonitor.RowsPerPage - 1) / StockMonitor.RowsPerPage);
        _summary.Text = _list.Items.Count == 0 ? "尚未添加股票" : $"共 {_list.Items.Count} 只 · {pages} 页";
    }

    void Save()
    {
        if (_list.Items.Count == 0)
        {
            _status.Text = "至少保留一只股票。";
            return;
        }
        StockMonitor.Symbols = _list.Items.Cast<ListViewItem>().Select(x => x.Tag!.ToString()!).ToArray();
        _stocks.Refresh();
        DialogResult = DialogResult.OK;
        Close();
    }
}
