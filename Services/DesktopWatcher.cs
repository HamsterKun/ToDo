namespace DesktopNote.Services;

/// <summary>
/// 判断便签此刻该不该露出来。
/// 核心是"有没有别的应用窗口压在我这块面积上"——这个判断与便签自己是否可见无关，
/// 不会出现"自己挡住自己 → 永远显示"或者来回闪烁。
/// </summary>
public static class DesktopWatcher
{
    // 只放桌面、任务栏和系统外壳的窗口类。
    // ApplicationFrameWindow 是 UWP 应用（设置、计算器等）的外框，属于真正的应用窗口，不能排除。
    private static readonly string[] DesktopClasses =
    {
        "Progman", "WorkerW", "SHELLDLL_DefView", "SysListView32", "SysShadow",
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow"
    };

    public static bool IsDesktopInFront(IntPtr self)
    {
        var fg = NativeMethods.GetForegroundWindow();

        if (fg == IntPtr.Zero) return true;
        if (fg == self) return true;

        var cls = NativeMethods.ClassOf(fg);
        if (cls is "Progman" or "WorkerW") return true;

        // 前台窗口被最小化 / 被 DWM 隐身（Win+D 预览桌面就是 cloaked）
        if (NativeMethods.IsIconic(fg)) return true;
        if (NativeMethods.IsCloaked(fg)) return true;

        return false;
    }

    /// <summary>有没有别的应用窗口盖住了便签所占的区域（超过 35% 就算盖住）。</summary>
    public static bool IsCoveredByOther(IntPtr self)
    {
        if (!NativeMethods.GetWindowRect(self, out var selfRect)) return false;
        if (selfRect.Width < 40 || selfRect.Height < 40) return false;

        long selfArea = (long)selfRect.Width * selfRect.Height;
        var covered = false;

        NativeMethods.EnumWindows((h, _) =>
        {
            if (covered) return false;
            // EnumWindows 按 Z 序从上往下枚举，走到自己就说明上面没有遮挡物了，
            // 再往下的窗口都在便签下面，不能算遮挡
            if (h == self) return false;
            if (!NativeMethods.IsWindowVisible(h)) return true;
            if (NativeMethods.IsIconic(h)) return true;
            if (NativeMethods.IsCloaked(h)) return true;

            var cls = NativeMethods.ClassOf(h);
            if (Array.IndexOf(DesktopClasses, cls) >= 0) return true;

            var ex = NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

            if (!NativeMethods.GetWindowRect(h, out var w)) return true;
            if (w.Width < 60 || w.Height < 60) return true;

            long ix = Math.Max(0, Math.Min(selfRect.Right, w.Right) - Math.Max(selfRect.Left, w.Left));
            long iy = Math.Max(0, Math.Min(selfRect.Bottom, w.Bottom) - Math.Max(selfRect.Top, w.Top));
            long inter = ix * iy;

            if (inter * 100 >= selfArea * 35)
            {
                covered = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return covered;
    }

    /// <summary>
    /// 指定显示器上是否有全屏应用在前台（视频、游戏、演示）。
    /// 带标题栏的最大化窗口不算全屏。
    /// </summary>
    public static bool IsFullscreenOn(IntPtr monitor)
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (Array.IndexOf(DesktopClasses, NativeMethods.ClassOf(fg)) >= 0) return false;
        if (NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST) != monitor) return false;

        var style = NativeMethods.GetWindowLongPtr(fg, NativeMethods.GWL_STYLE).ToInt64();
        if ((style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION) return false;

        if (!NativeMethods.GetWindowRect(fg, out var r)) return false;
        if (!NativeMethods.TryGetMonitorInfo(monitor, out var mi)) return false;
        var m = mi.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }
}
