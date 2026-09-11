namespace GenshinFpsUnlocker.Setup;

/// <summary>
/// 图形安装向导：欢迎 → 选择目录/选项 → 进度（滚动文件列表）→ 完成。
/// 底部固定：上一步 / 下一步 / 取消。
/// </summary>
internal sealed class SetupForm : Form
{
    private readonly string? _payloadDir;

    private Panel _pageHost = null!;
    private Panel _welcomePage = null!;
    private Panel _optionsPage = null!;
    private Panel _progressPage = null!;
    private Panel _donePage = null!;

    private TextBox _pathBox = null!;
    private CheckBox _desktopBox = null!;
    private CheckBox _defenderBox = null!;
    private CheckBox _autostartBox = null!;
    private CheckBox _launchBox = null!;
    private Label _payloadLabel = null!;
    private Label _runtimeLabel = null!;
    private Label _fileCountLabel = null!;

    private Label _statusLabel = null!;
    private Label _currentFileLabel = null!;
    private Label _percentLabel = null!;
    private ProgressBar _progress = null!;
    private ListBox _fileList = null!;

    private Label _donePathLabel = null!;

    private Button _backBtn = null!;
    private Button _nextBtn = null!;
    private Button _cancelBtn = null!;

    private int _page; // 0 welcome 1 options 2 progress 3 done
    private bool _busy;
    private CancellationTokenSource? _installCts;
    private string _lastInstallDir = "";

    private const int WinW = 560;
    private const int WinH = 460;

    public SetupForm(string? payloadDir)
    {
        _payloadDir = payloadDir;

        Text = SetupConstants.DisplayName + " — 安装程序";
        ClientSize = new Size(WinW, WinH);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5F);
        ShowInTaskbar = true;

        BuildUi();
        ShowPage(0);
        RefreshPayloadUi();

        FormClosing += (_, e) =>
        {
            if (!_busy) return;
            var r = MessageBox.Show(
                this,
                "安装正在进行。确定要取消并退出吗？",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (r == DialogResult.Yes)
            {
                try { _installCts?.Cancel(); } catch { /* ignore */ }
            }
            else
            {
                e.Cancel = true;
            }
        };
    }

    private void BuildUi()
    {
        // ---- 底部按钮栏（固定高度，不随内容撑大）----
        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = SystemColors.Control,
        };
        var sep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(200, 200, 200) };
        bottom.Controls.Add(sep);

        _cancelBtn = new Button
        {
            Text = "取消",
            Width = 88,
            Height = 28,
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
        };
        _backBtn = new Button
        {
            Text = "上一步",
            Width = 88,
            Height = 28,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _nextBtn = new Button
        {
            Text = "下一步",
            Width = 88,
            Height = 28,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };

        void LayoutButtons()
        {
            _cancelBtn.Location = new Point(12, 12);
            _nextBtn.Location = new Point(bottom.ClientSize.Width - _nextBtn.Width - 12, 12);
            _backBtn.Location = new Point(_nextBtn.Left - _backBtn.Width - 8, 12);
        }

        bottom.Resize += (_, _) => LayoutButtons();
        _cancelBtn.Click += OnCancelClick;
        _backBtn.Click += OnBackClick;
        _nextBtn.Click += async (_, _) => await OnNextClickAsync();

        bottom.Controls.Add(_cancelBtn);
        bottom.Controls.Add(_backBtn);
        bottom.Controls.Add(_nextBtn);

        // ---- 页面宿主 ----
        _pageHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
        _welcomePage = BuildWelcomePage();
        _optionsPage = BuildOptionsPage();
        _progressPage = BuildProgressPage();
        _donePage = BuildDonePage();

        foreach (var p in new[] { _welcomePage, _optionsPage, _progressPage, _donePage })
        {
            p.Dock = DockStyle.Fill;
            p.Visible = false;
            _pageHost.Controls.Add(p);
        }

        Controls.Add(_pageHost);
        Controls.Add(bottom);
        Load += (_, _) => LayoutButtons();
    }

    private Panel BuildWelcomePage()
    {
        var p = new Panel();
        var title = new Label
        {
            Text = "欢迎安装 " + SetupConstants.DisplayName,
            Font = new Font(Font.FontFamily, 13F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(8, 4),
        };
        var body = new Label
        {
            Location = new Point(8, 40),
            Size = new Size(500, 200),
            Text =
                "本向导将引导您完成安装。\n\n" +
                "• 可自定义安装目录（默认 Program Files\\GenshinFpsUnlocker）\n" +
                "• 创建开始菜单与可选桌面快捷方式\n" +
                "• 配置与日志：%LocalAppData%\\GenshinFpsUnlocker\\\n\n" +
                "支持 Windows 10 与 Windows 11（64 位）。\n" +
                "安装需要管理员权限（写入所选目录）。\n" +
                "安装完成后，日常使用与开机自启不会再弹出系统授权框。\n\n" +
                "请点击「下一步」选择安装位置。",
        };
        _payloadLabel = new Label
        {
            Location = new Point(8, 260),
            Size = new Size(500, 48),
            ForeColor = Color.DimGray,
            Text = "正在检测安装包…",
        };
        p.Controls.Add(title);
        p.Controls.Add(body);
        p.Controls.Add(_payloadLabel);
        return p;
    }

    private Panel BuildOptionsPage()
    {
        var p = new Panel();

        var title = new Label
        {
            Text = "安装选项",
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(8, 4),
        };

        var pathLbl = new Label { Text = "安装目录：", AutoSize = true, Location = new Point(8, 36) };
        _pathBox = new TextBox
        {
            Location = new Point(8, 56),
            Size = new Size(400, 25),
            Text = SetupConstants.DefaultInstallDir,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        var browse = new Button
        {
            Text = "浏览…",
            Location = new Point(416, 54),
            Size = new Size(80, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        browse.Click += (_, _) => BrowseInstallDir();

        var tip = new Label
        {
            Location = new Point(8, 86),
            Size = new Size(500, 32),
            ForeColor = Color.DimGray,
            Text = "目录名须为 GenshinFpsUnlocker。若选择其它文件夹，将在其下自动创建该子目录。",
        };

        _desktopBox = new CheckBox
        {
            Text = "创建桌面快捷方式",
            Checked = true,
            AutoSize = true,
            Location = new Point(8, 124),
        };
        _defenderBox = new CheckBox
        {
            Text = "添加 Windows Defender 排除（推荐）",
            Checked = true,
            AutoSize = true,
            Location = new Point(8, 150),
        };
        _autostartBox = new CheckBox
        {
            Text = "安装后启用开机自启（无授权弹窗）",
            Checked = false,
            AutoSize = true,
            Location = new Point(8, 176),
        };
        _launchBox = new CheckBox
        {
            Text = "安装完成后运行程序",
            Checked = true,
            AutoSize = true,
            Location = new Point(8, 202),
        };

        _fileCountLabel = new Label
        {
            Location = new Point(8, 234),
            Size = new Size(500, 20),
            ForeColor = Color.DimGray,
            Text = "",
        };

        _runtimeLabel = new Label
        {
            Location = new Point(8, 258),
            Size = new Size(400, 60),
            ForeColor = Color.DarkSlateGray,
            Text = "",
        };

        var dlBtn = new Button
        {
            Text = "下载 .NET 8 运行库",
            Location = new Point(8, 322),
            Size = new Size(160, 28),
        };
        dlBtn.Click += (_, _) => RuntimeCheck.OpenDownload();

        p.Controls.Add(title);
        p.Controls.Add(pathLbl);
        p.Controls.Add(_pathBox);
        p.Controls.Add(browse);
        p.Controls.Add(tip);
        p.Controls.Add(_desktopBox);
        p.Controls.Add(_defenderBox);
        p.Controls.Add(_autostartBox);
        p.Controls.Add(_launchBox);
        p.Controls.Add(_fileCountLabel);
        p.Controls.Add(_runtimeLabel);
        p.Controls.Add(dlBtn);

        p.Resize += (_, _) =>
        {
            _pathBox.Width = Math.Max(200, p.ClientSize.Width - browse.Width - 24);
            browse.Left = _pathBox.Right + 8;
        };

        return p;
    }

    private Panel BuildProgressPage()
    {
        var p = new Panel();

        _statusLabel = new Label
        {
            Text = "准备安装…",
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(8, 4),
        };

        _percentLabel = new Label
        {
            Text = "0%",
            AutoSize = true,
            Location = new Point(460, 8),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };

        _progress = new ProgressBar
        {
            Location = new Point(8, 32),
            Size = new Size(500, 22),
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Style = ProgressBarStyle.Continuous,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _currentFileLabel = new Label
        {
            Location = new Point(8, 60),
            Size = new Size(500, 36),
            ForeColor = Color.DimGray,
            Text = "",
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        var listLbl = new Label
        {
            Text = "已处理文件 / 步骤（自动滚动）：",
            AutoSize = true,
            Location = new Point(8, 100),
        };

        // 固定高度 ListBox，内容再多也不撑大窗口，仅内部滚动
        _fileList = new ListBox
        {
            Location = new Point(8, 122),
            Size = new Size(500, 230),
            IntegralHeight = false,
            HorizontalScrollbar = true,
            Font = new Font("Consolas", 8.5F),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };

        p.Controls.Add(_statusLabel);
        p.Controls.Add(_percentLabel);
        p.Controls.Add(_progress);
        p.Controls.Add(_currentFileLabel);
        p.Controls.Add(listLbl);
        p.Controls.Add(_fileList);

        p.Resize += (_, _) =>
        {
            var w = Math.Max(200, p.ClientSize.Width - 16);
            _progress.Width = w;
            _currentFileLabel.Width = w;
            _fileList.Width = w;
            _fileList.Height = Math.Max(120, p.ClientSize.Height - _fileList.Top - 8);
            _percentLabel.Left = p.ClientSize.Width - 50;
        };

        return p;
    }

    private Panel BuildDonePage()
    {
        var p = new Panel();
        var title = new Label
        {
            Text = "安装完成",
            Font = new Font(Font.FontFamily, 13F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(8, 4),
        };
        var body = new Label
        {
            Location = new Point(8, 40),
            Size = new Size(500, 140),
            Text =
                "感谢安装 " + SetupConstants.DisplayName + "。\n\n" +
                "• 开始菜单或桌面快捷方式可启动\n" +
                "• 卸载：开始菜单 → 卸载 " + SetupConstants.DisplayName + "\n" +
                "• 配置/日志：%LocalAppData%\\GenshinFpsUnlocker\\\n\n" +
                "开机自启不会弹出 Windows 授权框。\n" +
                "点击「完成」关闭本向导。",
        };
        _donePathLabel = new Label
        {
            Location = new Point(8, 200),
            Size = new Size(500, 60),
            ForeColor = Color.DimGray,
            Text = "",
        };
        p.Controls.Add(title);
        p.Controls.Add(body);
        p.Controls.Add(_donePathLabel);
        return p;
    }

    private void BrowseInstallDir()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择安装位置（将使用或创建 GenshinFpsUnlocker 子目录）",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_pathBox.Text)
                ? _pathBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var chosen = dlg.SelectedPath.TrimEnd('\\', '/');
        var leaf = Path.GetFileName(chosen);
        _pathBox.Text = leaf.Equals(SetupConstants.ProductName, StringComparison.OrdinalIgnoreCase)
            ? chosen
            : Path.Combine(chosen, SetupConstants.ProductName);
    }

    private void ShowPage(int page)
    {
        _page = page;
        _welcomePage.Visible = page == 0;
        _optionsPage.Visible = page == 1;
        _progressPage.Visible = page == 2;
        _donePage.Visible = page == 3;

        _backBtn.Enabled = !_busy && page is 1; // 进度中不可上一步；完成页无上一步
        _backBtn.Visible = page is not 3;
        _cancelBtn.Visible = page is not 3;
        _cancelBtn.Enabled = true; // 安装中也可点取消 → 请求中止
        _cancelBtn.Text = _busy ? "取消安装" : "取消";

        if (page == 0) { _nextBtn.Text = "下一步"; _nextBtn.Enabled = _payloadDir is not null && !_busy; }
        else if (page == 1) { _nextBtn.Text = "安装"; _nextBtn.Enabled = !_busy; UpdateOptionsHints(); }
        else if (page == 2) { _nextBtn.Text = "安装中…"; _nextBtn.Enabled = false; }
        else
        {
            _nextBtn.Text = "完成";
            _nextBtn.Enabled = true;
            _backBtn.Visible = false;
            _cancelBtn.Visible = false;
            _donePathLabel.Text = "安装位置：\n" + _lastInstallDir;
        }
    }

    private void RefreshPayloadUi()
    {
        if (_payloadDir is null)
        {
            _payloadLabel.Text = "未找到安装包（需要 Payload 中的 GenshinFpsUnlocker.exe）。\n请先运行 build.ps1。";
            _payloadLabel.ForeColor = Color.DarkRed;
            return;
        }

        var stub = File.Exists(Path.Combine(_payloadDir, SetupConstants.StubName));
        var n = InstallEngine.CollectPayloadFiles(_payloadDir).Count;
        _payloadLabel.Text = $"安装包：{_payloadDir}\n文件数：{n}　Stub：{(stub ? "已找到" : "缺失")}";
        _payloadLabel.ForeColor = stub ? Color.DimGray : Color.DarkOrange;
    }

    private void UpdateOptionsHints()
    {
        if (_payloadDir is null)
        {
            _fileCountLabel.Text = "";
            _runtimeLabel.Text = "无安装包。";
            return;
        }

        var n = InstallEngine.CollectPayloadFiles(_payloadDir).Count;
        _fileCountLabel.Text = $"将复制约 {n} 个文件到所选目录。";

        if (PayloadLocator.IsSelfContained(_payloadDir))
        {
            _runtimeLabel.Text = "自包含构建：一般无需单独安装 .NET。";
            _runtimeLabel.ForeColor = Color.DarkGreen;
        }
        else if (RuntimeCheck.HasDotNetDesktop8(out var detail))
        {
            _runtimeLabel.Text = "已检测到 .NET 8 桌面运行时。\n" + detail;
            _runtimeLabel.ForeColor = Color.DarkGreen;
        }
        else
        {
            _runtimeLabel.Text = "未检测到 .NET 8 桌面运行时，主程序可能无法启动。\n可先下载运行库，或仍继续安装。";
            _runtimeLabel.ForeColor = Color.DarkOrange;
        }
    }

    private void OnBackClick(object? sender, EventArgs e)
    {
        if (_busy || _page != 1) return;
        ShowPage(0);
    }

    private void OnCancelClick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            var r = MessageBox.Show(
                this,
                "确定取消当前安装？已复制的文件可能需要手动清理。",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (r == DialogResult.Yes)
            {
                try { _installCts?.Cancel(); } catch { /* ignore */ }
                AppendList("—— 用户请求取消 ——");
                _statusLabel.Text = "正在取消…";
            }
            return;
        }

        if (MessageBox.Show(this, "确定退出安装程序？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            Close();
    }

    private async Task OnNextClickAsync()
    {
        if (_busy) return;

        if (_page == 0)
        {
            if (_payloadDir is null)
            {
                MessageBox.Show(this, "未找到安装包。", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            ShowPage(1);
            return;
        }

        if (_page == 1)
        {
            if (!TryResolveInstallDir(out var installDir))
                return;

            if (!PayloadLocator.IsSelfContained(_payloadDir!) &&
                !RuntimeCheck.HasDotNetDesktop8(out _))
            {
                var r = MessageBox.Show(
                    this,
                    "未检测到 .NET 8 桌面运行时。\n\n是 = 打开下载\n否 = 仍然安装\n取消 = 返回",
                    "运行库",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning);
                if (r == DialogResult.Yes) { RuntimeCheck.OpenDownload(); return; }
                if (r == DialogResult.Cancel) return;
            }

            await RunInstallAsync(installDir);
            return;
        }

        if (_page == 3)
            Close();
    }

    private bool TryResolveInstallDir(out string installDir)
    {
        installDir = (_pathBox.Text ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(installDir))
        {
            MessageBox.Show(this, "请填写安装目录。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        try { installDir = Path.GetFullPath(installDir); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "路径无效: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var leaf = Path.GetFileName(installDir.TrimEnd('\\', '/'));
        if (!leaf.Equals(SetupConstants.ProductName, StringComparison.OrdinalIgnoreCase))
        {
            var fix = Path.Combine(installDir, SetupConstants.ProductName);
            var r = MessageBox.Show(
                this,
                $"最终目录名必须为 {SetupConstants.ProductName}。\n\n将安装到：\n{fix}\n\n是否继续？",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return false;
            installDir = fix;
            _pathBox.Text = fix;
        }

        return true;
    }

    private async Task RunInstallAsync(string installDir)
    {
        _lastInstallDir = installDir;
        _busy = true;
        _installCts = new CancellationTokenSource();
        _fileList.Items.Clear();
        _progress.Value = 0;
        _percentLabel.Text = "0%";
        _currentFileLabel.Text = "";
        _statusLabel.Text = "正在安装…";
        ShowPage(2);

        var engine = new InstallEngine
        {
            PayloadDir = _payloadDir!,
            InstallDir = installDir,
            CreateDesktopShortcut = _desktopBox.Checked,
            AddDefenderExclusion = _defenderBox.Checked,
            EnableAutostart = _autostartBox.Checked,
            StartAfterInstall = _launchBox.Checked,
            CancellationToken = _installCts.Token,
        };

        engine.Progress += ev =>
        {
            try { BeginInvoke(() => ApplyProgress(ev)); }
            catch { /* ignore */ }
        };

        try
        {
            await Task.Run(() => engine.Run(), _installCts.Token);
            _statusLabel.Text = "安装完成";
            _progress.Value = 100;
            _percentLabel.Text = "100%";
            AppendList("—— 全部完成 ——");
            _busy = false;
            ShowPage(3);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "已取消";
            AppendList("—— 安装已取消 ——");
            MessageBox.Show(this, "安装已取消。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            _busy = false;
            ShowPage(1);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "安装失败";
            AppendList("错误: " + ex.Message);
            MessageBox.Show(this, "安装失败：\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            _busy = false;
            ShowPage(1);
        }
        finally
        {
            _installCts?.Dispose();
            _installCts = null;
            _busy = false;
            if (_page != 3) ShowPage(_page);
        }
    }

    private void ApplyProgress(InstallProgressEvent ev)
    {
        if (ev.Total > 0)
        {
            var pct = (int)Math.Clamp(100.0 * ev.Current / ev.Total, 0, 100);
            if (pct >= _progress.Value)
                _progress.Value = pct;
            _percentLabel.Text = _progress.Value + "%";
        }

        _statusLabel.Text = ev.IsFileCopy ? "正在复制文件…" : (ev.Message.Length > 40 ? ev.Message[..40] + "…" : ev.Message);

        if (ev.IsFileCopy && ev.RelativePath is not null)
        {
            var name = Path.GetFileName(ev.RelativePath);
            var line = $"{ev.Current}/{ev.Total}  {name}";
            if (ev.TargetPath is not null)
                line += $"  →  {ev.TargetPath}";
            // 列表过长时裁剪旧项，避免内存与绘制压力，保持滚动区域紧凑
            if (_fileList.Items.Count > 500)
                _fileList.Items.RemoveAt(0);
            _fileList.Items.Add(line);
            _fileList.TopIndex = Math.Max(0, _fileList.Items.Count - 1);

            _currentFileLabel.Text =
                $"当前：{name}\n目录：{(ev.TargetPath is null ? "" : Path.GetDirectoryName(ev.TargetPath))}";
        }
        else
        {
            AppendList($"{ev.Current}/{ev.Total}  {ev.Message}");
            if (!string.IsNullOrEmpty(ev.TargetPath))
                _currentFileLabel.Text = ev.TargetPath;
        }
    }

    private void AppendList(string line)
    {
        if (_fileList.Items.Count > 500)
            _fileList.Items.RemoveAt(0);
        _fileList.Items.Add(line);
        _fileList.TopIndex = Math.Max(0, _fileList.Items.Count - 1);
    }
}
