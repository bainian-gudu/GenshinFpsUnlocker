using System.Drawing.Drawing2D;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 托盘右键菜单窗口的四角圆边。
///
/// Win11：交给 DWM（<c>DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND</c>），
/// 抗锯齿、与系统菜单观感一致，不用自己裁。
/// Win10：没有这个属性，退化成用 <see cref="Control.Region"/> 把四角裁掉；Region 是
/// 硬裁剪（无抗锯齿），所以半径取 8 这种小值，并让渲染器把 1px 边框也画成同样的
/// 圆角矩形（见 <see cref="RegionActive"/>），否则边框会在被裁掉的角上断开。
///
/// 菜单是 AutoSize：每次打开、每次改帧率重建子项都可能变尺寸，而 Region 是按像素
/// 固定的，所以尺寸一变就要重算（DWM 那条路径不需要，但重设一次也无害）。
/// </summary>
internal static class TrayMenuCorners
{
    /// <summary>96 DPI 下的圆角半径；与 Win11 <c>DWMWCP_ROUND</c> 的观感对齐。</summary>
    private const int RadiusLogical = 8;

    /// <summary>已经挂过事件的下拉窗口（主菜单 + 帧率子菜单，一共就两个）。</summary>
    private static readonly HashSet<ToolStripDropDown> Hooked = [];

    /// <summary>
    /// 当前是否走「Region 裁角」这条路。渲染器据此决定边框画圆角还是直角：
    /// DWM 圆角在窗口边框之外，1px 边框保持矩形即可；Region 会把它裁断。
    /// </summary>
    internal static bool RegionActive { get; private set; }

    /// <summary>给一个下拉窗口（ContextMenuStrip 或某项的 DropDown）加上圆角。可重复调用。</summary>
    internal static void Apply(ToolStripDropDown? drop)
    {
        if (drop is null) return;
        try
        {
            if (Hooked.Add(drop))
            {
                drop.HandleCreated += (_, _) => Refresh(drop);
                drop.SizeChanged += (_, _) => Refresh(drop);
            }

            Refresh(drop);
        }
        catch (Exception ex)
        {
            // 圆角只是外观，任何异常都不该影响托盘菜单能用
            AppLog.Error(ex, "tray menu corners");
        }
    }

    private static void Refresh(ToolStripDropDown drop)
    {
        try
        {
            // 取 Handle 会顺带创建窗口句柄：DWM 属性必须有句柄才设得上
            var hwnd = drop.Handle;
            if (UiStyle.TryApplyRoundedCorners(hwnd))
            {
                RegionActive = false;
                drop.Region = null;   // 交给 DWM，别再叠一层 Region 把抗锯齿边裁成锯齿
                return;
            }

            var radius = RadiusFor(drop);
            if (drop.Width <= radius * 2 || drop.Height <= radius * 2)
            {
                // 太小就别裁，免得把内容切掉
                RegionActive = false;
                drop.Region = null;
                return;
            }

            using var path = RoundRect(new Rectangle(0, 0, drop.Width, drop.Height), radius);
            drop.Region = new Region(path);
            RegionActive = true;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "tray menu corners refresh");
        }
    }

    /// <summary>按控件 DPI 缩放半径（150% 缩放下 8 → 12）。</summary>
    internal static int RadiusFor(Control c) =>
        c is null ? RadiusLogical : Math.Max(4, (int)Math.Round(RadiusLogical * Math.Max(1, c.DeviceDpi) / 96.0));

    /// <summary>圆角矩形路径：窗口 Region、圆角边框、选中条共用同一份实现。</summary>
    internal static GraphicsPath RoundRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1, radius) * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
