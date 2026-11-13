using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopNote.Models;
using DesktopNote.Services;

namespace DesktopNote;

/// <summary>
/// 主便签被其它窗口挡住时，缩成右下角的一条小悬浮窗，只显示时间上最近的一条待办。
/// 置顶、不抢焦点、不进任务栏和 Alt+Tab；点一下就把主便签叫回来。
/// </summary>
public partial class MiniWindow : Window
{
    private static readonly Brush Normal = Freeze("#8B96A8");
    private static readonly Brush Late = Freeze("#FF7B72");
    private static readonly Brush DotNormal = Freeze("#6AA6FF");
    private static readonly Brush DotIdle = Freeze("#4A5668");
    private static readonly Brush TitleNormal = Freeze("#E9EEF6");
    private static readonly Brush TitleIdle = Freeze("#8B96A8");

    private const int MarginDip = 12;

    private IntPtr _hwnd;

    /// <summary>用户点了迷你窗。</summary>
    public event Action? Clicked;

    public MiniWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // 工具窗口：不进 Alt+Tab，也会被主窗的遮挡判断忽略；NOACTIVATE：点它不会抢走前台焦点
            var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        };
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Clicked?.Invoke();
        };
    }

    private static Brush Freeze(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public void SetItem(NoteItem? item, DateTime now)
    {
        if (item == null)
        {
            TitleText.Text = "暂无带时间的待办";
            TitleText.Foreground = TitleIdle;
            ChipText.Text = "";
            Dot.Fill = DotIdle;
            ToolTip = null;
            return;
        }

        var (chip, late) = MainWindow.Describe(item, now);
        TitleText.Text = item.Title;
        TitleText.Foreground = late ? Late : TitleNormal;
        ChipText.Text = late ? "逾期 " + chip : chip;
        ChipText.Foreground = late ? Late : Normal;
        Dot.Fill = late ? Late : DotNormal;
        ToolTip = item.Title;
    }

    /// <summary>显示在 owner 所在显示器工作区的右下角（任务栏上方）。</summary>
    public void ShowAt(IntPtr ownerHwnd)
    {
        // 先建句柄、摆好位置再显示，避免第一次出现时在默认位置闪一下
        if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();

        var mon = NativeMethods.MonitorFromWindow(ownerHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (NativeMethods.TryGetMonitorInfo(mon, out var mi))
        {
            var work = mi.rcWork;
            double s = NativeMethods.MonitorScale(mon);

            int w = (int)Math.Round(Width * s);
            int h = (int)Math.Round(Height * s);
            int m = (int)Math.Round(MarginDip * s);
            int x = work.Right - w - m;
            int y = work.Bottom - h - m;

            if (!NativeMethods.GetWindowRect(_hwnd, out var cur) || cur.Left != x || cur.Top != y)
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        if (!IsVisible) Show();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        Card.Opacity = 1;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Card.Opacity = 0.92;
    }
}
