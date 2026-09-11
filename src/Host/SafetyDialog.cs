namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 安全声明对话框（从主窗体拆分，避免 MainForm 过大）。
/// </summary>
internal static class SafetyDialog
{
    /// <summary>
    /// 显示安全声明对话框。force=true 时强制显示。
    /// 用户勾选“不再每次启动提示”后写入 ShowSafetyNoticeOnStartup=false。
    /// </summary>
    public static void Show(IWin32Window? owner, AppConfig config, bool force)
    {
        if (!force && config.SafetyNoticeAcknowledged && !config.ShowSafetyNoticeOnStartup)
            return;

        using var dlg = new Form
        {
            Text = SafetyNotice.Title,
            Width = 560,
            Height = 420,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Font = new Font("Segoe UI", 9.75F),
        };

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Text = SafetyNotice.FullText,
            BackColor = Color.White,
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 72, Padding = new Padding(12) };
        var dontShow = new CheckBox
        {
            Text = "我已阅读，不再每次启动提示",
            Checked = config.SafetyNoticeAcknowledged,
            AutoSize = true,
            Left = 12,
            Top = 8,
        };
        var ok = new Button
        {
            Text = "我知道了",
            Width = 100,
            Height = 30,
            Left = 420,
            Top = 28,
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        bottom.Controls.Add(dontShow);
        bottom.Controls.Add(ok);
        dlg.Controls.Add(box);
        dlg.Controls.Add(bottom);
        dlg.AcceptButton = ok;

        var result = dlg.ShowDialog(owner);
        // 点 X 关闭也视为本会话已确认；“不再提示”仅在勾选时生效
        config.SafetyNoticeAcknowledged = true;
        if (result == DialogResult.OK && dontShow.Checked)
        {
            config.ShowSafetyNoticeOnStartup = false;
        }
        config.Save();
    }


}
