using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using WinDefendOFF.Core;

namespace WinDefendOFF.App;

public sealed class MainForm : Form
{
    private readonly DefenderController _controller;
    private readonly TextBox _logBox = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9F),
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
    };
    private Label _statusLabel = new();
    private Label _engineLabel = new();
    private Label _rtpLabel = new();
    private Label _tamperLabel = new();
    private Label _ifeoLabel = new();
    private Label _updatedLabel = new();
    private Label _compatLabel = new();
    private DataGridView _serviceGrid = new();
    private Button _toggleButton = new();
    private Button _refreshButton = new();
    private ProgressBar _progress = new();
    private Button _openLogFolder = new();
    private Button _compatButton = new();

    private DefenderState _state = new();

    private static readonly Color OkColor = Color.FromArgb(22, 101, 52);
    private static readonly Color WarnColor = Color.FromArgb(176, 58, 46);
    private static readonly Color MutedColor = Color.FromArgb(90, 90, 90);
    private static readonly Color PanelBg = Color.FromArgb(247, 247, 247);
    private static readonly Color Border = Color.FromArgb(220, 220, 220);

    public MainForm()
    {
        var workDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinDefendOFF");
        Directory.CreateDirectory(workDir);
        _controller = new DefenderController(workDir, AppendLog);

        BuildLayout();
        RefreshState();
    }

    private void BuildLayout()
    {
        Text = "WinDefendOFF — Microsoft Defender 開關";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(840, 780);
        MinimumSize = new Size(760, 720);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));   // 標題列
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 168));  // 狀態卡
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));   // 開關列
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 服務表
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));  // 記錄
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildStatusCards(), 0, 1);
        root.Controls.Add(BuildToggleRow(), 0, 2);
        root.Controls.Add(BuildServiceTable(), 0, 3);
        root.Controls.Add(BuildLogPanel(), 0, 4);
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var title = new Label
        {
            Text = "Microsoft Defender 開關",
            Font = new Font("Segoe UI Semibold", 18F),
            AutoSize = true,
            Location = new Point(0, 4),
        };
        var subtitle = new Label
        {
            Text = "自主控制 Defender 的停用與恢復。所有變更都會記錄於下方。",
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(2, 38),
        };
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        return panel;
    }

    private Control BuildStatusCards()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
        };
        for (var i = 0; i < 3; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        _statusLabel = MakeCardLabel();
        _engineLabel = MakeCardLabel();
        _rtpLabel = MakeCardLabel();
        _tamperLabel = MakeCardLabel();
        _ifeoLabel = MakeCardLabel();
        _updatedLabel = MakeCardLabel();

        table.Controls.Add(BuildCard("整體狀態", _statusLabel), 0, 0);
        table.Controls.Add(BuildCard("引擎行程 (MsMpEng)", _engineLabel), 1, 0);
        table.Controls.Add(BuildCard("即時保護", _rtpLabel), 2, 0);
        table.Controls.Add(BuildCard("篡改保護", _tamperLabel), 0, 1);
        table.Controls.Add(BuildCard("IFEO 引擎封鎖", _ifeoLabel), 1, 1);
        table.Controls.Add(BuildCard("相容性檢查", _compatLabel), 2, 1);
        return table;
    }

    private static Label MakeCardLabel() => new()
    {
        Text = "—",
        Font = new Font("Segoe UI Semibold", 13F),
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private Control BuildCard(string title, Label valueLabel)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = PanelBg,
            Padding = new Padding(12),
            Margin = new Padding(4),
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };

        var titleLabel = new Label
        {
            Text = title,
            ForeColor = MutedColor,
            Font = new Font("Segoe UI", 9F),
            Dock = DockStyle.Top,
            Height = 20,
        };
        valueLabel.Dock = DockStyle.Fill;
        card.Controls.Add(valueLabel);
        card.Controls.Add(titleLabel);
        return card;
    }

    private Control BuildToggleRow()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 8, 4, 8) };

        _toggleButton = new Button
        {
            Text = "停用 Defender",
            Font = new Font("Segoe UI Semibold", 12F),
            Size = new Size(230, 46),
            Location = new Point(4, 4),
            BackColor = WarnColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
        };
        _toggleButton.FlatAppearance.BorderSize = 0;
        _toggleButton.Click += ToggleButton_Click;

        _refreshButton = new Button
        {
            Text = "重新整理",
            Size = new Size(110, 46),
            Location = new Point(246, 4),
            Cursor = Cursors.Hand,
        };
        _refreshButton.Click += (_, _) => RefreshState();

        _openLogFolder = new Button
        {
            Text = "開啟記錄資料夾",
            Size = new Size(150, 46),
            Location = new Point(366, 4),
            Cursor = Cursors.Hand,
        };
        _openLogFolder.Click += (_, _) =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinDefendOFF");
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLog("無法開啟資料夾: " + ex.Message);
            }
        };

        _compatButton = new Button
        {
            Text = "環境檢查",
            Size = new Size(110, 46),
            Location = new Point(526, 4),
            Cursor = Cursors.Hand,
        };
        _compatButton.Click += (_, _) => ShowCompatibilityReport();

        _progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 0,
            Visible = false,
            Location = new Point(646, 19),
            Size = new Size(150, 16),
        };

        panel.Controls.Add(_toggleButton);
        panel.Controls.Add(_refreshButton);
        panel.Controls.Add(_openLogFolder);
        panel.Controls.Add(_compatButton);
        panel.Controls.Add(_progress);
        return panel;
    }

    private Control BuildServiceTable()
    {
        var group = new GroupBox
        {
            Text = "服務狀態",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 8),
        };

        _serviceGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EnableHeadersVisualStyles = false,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(235, 235, 235),
        };
        _serviceGrid.ClearSelection();
        _serviceGrid.ColumnHeadersDefaultCellStyle.BackColor = PanelBg;
        _serviceGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9F);
        _serviceGrid.ColumnHeadersHeight = 30;
        _serviceGrid.RowTemplate.Height = 26;

        // 唯讀展示用表格：讓選取高亮不可見
        _serviceGrid.DefaultCellStyle.SelectionBackColor = Color.White;
        _serviceGrid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _serviceGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = PanelBg;
        _serviceGrid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.Black;

        // 服務名稱欄需要固定寬度（其餘兩欄平均分配）
        _serviceGrid.Columns.Add("svc", "服務");
        _serviceGrid.Columns.Add("status", "狀態");
        _serviceGrid.Columns.Add("start", "開機啟動");
        _serviceGrid.Columns["svc"].FillWeight = 150;
        _serviceGrid.Columns["status"].FillWeight = 90;
        _serviceGrid.Columns["start"].FillWeight = 90;

        group.Controls.Add(_serviceGrid);
        return group;
    }

    private Control BuildLogPanel()
    {
        var group = new GroupBox
        {
            Text = "執行記錄",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 8),
        };

        group.Controls.Add(_logBox);
        return group;
    }

    private void RefreshState()
    {
        try
        {
            _state = _controller.ReadState();
        }
        catch (Exception ex)
        {
            AppendLog("讀取狀態失敗: " + ex.Message);
            return;
        }

        var enabled = !_state.IsDisabled;
        _statusLabel.Text = enabled ? "運作中" : "已停用";
        _statusLabel.ForeColor = enabled ? OkColor : WarnColor;

        _engineLabel.Text = _state.EngineRunning ? "執行中" : "未執行";
        _engineLabel.ForeColor = _state.EngineRunning ? OkColor : MutedColor;

        _rtpLabel.Text = _state.RealtimeProtectionEnabled ? "啟用" : "停用";
        _rtpLabel.ForeColor = _state.RealtimeProtectionEnabled ? OkColor : WarnColor;

        _tamperLabel.Text = _state.TamperProtectionEnabled ? "啟用" : "停用";
        _tamperLabel.ForeColor = _state.TamperProtectionEnabled ? OkColor : MutedColor;

        _ifeoLabel.Text = _state.IfeoBlockPresent ? "有 (需重開機移除)" : "無";
        _ifeoLabel.ForeColor = _state.IfeoBlockPresent ? WarnColor : MutedColor;

        // 相容性狀態
        try
        {
            var compat = new CompatibilityChecker().Check();
            _compatLabel.Text = compat.CanDisableWithoutReboot ? "可即時切換" : "有阻斷問題";
            _compatLabel.ForeColor = compat.CanDisableWithoutReboot ? OkColor : WarnColor;
        }
        catch
        {
            _compatLabel.Text = "無法檢查";
            _compatLabel.ForeColor = MutedColor;
        }

        _updatedLabel.Text = _state.CapturedAt.ToString("HH:mm:ss");
        _updatedLabel.ForeColor = MutedColor;

        _toggleButton.Text = _state.IsDisabled ? "啟用 Defender" : "停用 Defender";
        _toggleButton.BackColor = _state.IsDisabled ? OkColor : WarnColor;

        _serviceGrid.SuspendLayout();
        _serviceGrid.Rows.Clear();
        foreach (var svc in _state.Services)
        {
            var idx = _serviceGrid.Rows.Add(svc.Name, svc.Status, svc.StartType);
            _serviceGrid.Rows[idx].Cells["status"].Style.ForeColor =
                svc.IsRunning ? OkColor : MutedColor;
            _serviceGrid.Rows[idx].Cells["start"].Style.ForeColor =
                svc.IsDisabled ? WarnColor : Color.Black;
        }
        _serviceGrid.ClearSelection();
        _serviceGrid.ResumeLayout();
    }

    private async void ToggleButton_Click(object? sender, EventArgs e)
    {
        if (_state.IsDisabled)
        {
            var confirm = MessageBox.Show(
                "將要重新啟用 Microsoft Defender 與其所有服務。\n\n" +
                "確定要繼續嗎？",
                "確認啟用",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;
        }
        else
        {
            var confirm = MessageBox.Show(
                "將要停用 Microsoft Defender，包含：\n" +
                "  • 關閉 WinDefend / WdNisSvc / WdFilter / SecurityHealthService 服務\n" +
                "  • 封鎖 Defender 引擎載入 (IFEO)\n" +
                "  • 結束 MsMpEng.exe 行程\n\n" +
                "此裝置將失去惡意軟體防護。確定要繼續嗎？",
                "確認停用",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
        }

        SetBusy(true);
        try
        {
            var action = _state.IsDisabled ? DefenderAction.Enable : DefenderAction.Disable;
            var result = await Task.Run(() => action == DefenderAction.Disable
                ? _controller.Disable(_state)
                : _controller.Enable(_state));

            AppendLog("");
            foreach (var step in result.Steps)
            {
                AppendLog($"--- {step.Title}: {(step.Success ? "成功" : "失敗")} ---");
                if (step.Error is not null) AppendLog("  錯誤: " + step.Error);
            }

            RefreshState();

            MessageBox.Show(
                result.Success
                    ? (action == DefenderAction.Disable
                        ? "Defender 已停用，無需重新啟動電腦。\n\n" +
                          "引擎行程已終結，且已封鎖載入以確保不會自動重啟。"
                        : "Defender 已重新啟用，無需重新啟動電腦。\n\n" +
                          "引擎已恢復運作，即時保護已開啟。")
                    : "操作未完全成功，請檢視執行記錄。",
                result.Success ? "完成" : "部分失敗",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AppendLog("操作失敗: " + ex);
            MessageBox.Show("操作失敗:\n" + ex.Message, "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _toggleButton.Enabled = !busy;
        _refreshButton.Enabled = !busy;
        _progress.Visible = busy;
        _progress.MarqueeAnimationSpeed = busy ? 30 : 0;
        UseWaitCursor = busy;
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }

        var stamp = DateTime.Now.ToString("HH:mm:ss");
        _logBox.AppendText($"[{stamp}] {message}{Environment.NewLine}");

        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinDefendOFF");
            Directory.CreateDirectory(logDir);
            // UTF-8 with BOM：確保記事本等工具能正確解碼中文
            File.AppendAllText(
                Path.Combine(logDir, "windefendoff.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                new System.Text.UTF8Encoding(true));
        }
        catch
        {
            // 記錄檔寫入失敗不影響主流程
        }
    }

    /// <summary>顯示環境相容性檢查報告。</summary>
    private void ShowCompatibilityReport()
    {
        var checker = new CompatibilityChecker();
        var result = checker.Check();

        var sb = new StringBuilder();
        sb.AppendLine("WinDefendOFF 環境相容性檢查");
        sb.AppendLine(new string('=', 56));
        foreach (var item in result.Items)
        {
            var mark = item.Passed ? "[ OK ]" : (item.Critical ? "[!!  ]" : "[警告]");
            sb.AppendLine($"{mark} {item.Name}");
            sb.AppendLine($"        {item.Detail}");
            if (!item.Passed && item.Remedy is not null)
                sb.AppendLine($"        → {item.Remedy}");
            sb.AppendLine();
        }
        sb.AppendLine(new string('=', 56));
        sb.AppendLine(result.CanDisableWithoutReboot
            ? "結論：可即時停用 / 啟用，無需重新啟動電腦。"
            : "結論：存在阻斷性問題，無法保證即時切換。請依上方建議處理。");

        MessageBox.Show(
            sb.ToString(),
            "環境相容性檢查",
            MessageBoxButtons.OK,
            result.CanDisableWithoutReboot ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }
}
