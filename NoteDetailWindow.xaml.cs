using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopNote.Models;
using DesktopNote.Services;

namespace DesktopNote;

/// <summary>
/// 单击待办条目后弹出的详情窗口。
/// 显示标题、时间，并提供一个多行备注输入框，失焦或关闭时自动保存。
/// </summary>
public partial class NoteDetailWindow : Window
{
    private readonly NoteItem _item;
    private readonly NoteStore _store;
    private readonly DispatcherTimer _saveTimer;
    private bool _dirty;

    /// <summary>当前展示的条目 Id，供主窗口判断是否重复打开。</summary>
    public string ItemId => _item.Id;

    // 窗口打开后的宽限期，期间不响应 Deactivated，避免主窗 PreviewMouseDown 抢焦点导致立刻关闭
    private DateTime _openedAt;

    public NoteDetailWindow(NoteItem item, NoteStore store, Window owner)
    {
        InitializeComponent();

        _item  = item;
        _store = store;
        _openedAt = DateTime.Now;

        // 保存防抖：用户停止输入 800ms 后自动写盘
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Flush();
        };

        // 填充界面
        TitleText.Text = item.Title;
        TimeText.Text  = FormatTime(item);
        NotesBox.Text  = item.Notes ?? "";

        // 把窗口放在主窗口附近
        PositionNear(owner);

        // 窗口出现后把焦点移到备注框末尾
        Loaded += (_, _) =>
        {
            NotesBox.Focus();
            NotesBox.CaretIndex = NotesBox.Text.Length;
        };
    }

    // ── 事件 ──────────────────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Flush();
        Close();
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // 窗口刚打开时有短暂宽限期，忽略此期间的 Deactivated
        // 否则主窗口的 PreviewMouseDown → SetFocus 会在详情窗刚出现时把它立刻关掉
        if ((DateTime.Now - _openedAt).TotalMilliseconds < 400) return;

        // 窗口失去焦点（用户点了其他地方）时立即保存并关闭
        Flush();
        Close();
    }

    private void NotesBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _dirty = true;
        // 重置防抖计时器
        _saveTimer.Stop();
        _saveTimer.Start();
        // 显示"编辑中"提示
        SavedHint.Text    = "编辑中…";
        SavedHint.Opacity = 0.7;
    }

    // ── 内部方法 ──────────────────────────────────────────────────────────

    private void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        _saveTimer.Stop();

        _item.Notes = NotesBox.Text;
        _store.Touch();

        // 短暂显示"已保存"提示
        SavedHint.Text    = "已保存";
        SavedHint.Opacity = 1.0;
        var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        fadeTimer.Tick += (_, _) => { fadeTimer.Stop(); SavedHint.Opacity = 0; };
        fadeTimer.Start();
    }

    private void PositionNear(Window owner)
    {
        // 先显示在屏幕中央，再尝试紧贴主窗口右侧
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        try
        {
            var src = PresentationSource.FromVisual(owner);
            double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            double ownerLeft = owner.Left;
            double ownerTop  = owner.Top;
            double ownerW    = owner.ActualWidth;

            // 目标：紧贴主窗口右侧，垂直与主窗口顶部对齐
            double left = ownerLeft + ownerW + 10;
            double top  = ownerTop;

            // 防止超出屏幕右边缘
            var screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point((int)(ownerLeft * dpiX), (int)(ownerTop * dpiY)));
            double screenRightDip = screen.WorkingArea.Right / dpiX;
            double screenBottomDip = screen.WorkingArea.Bottom / dpiY;

            if (left + Width > screenRightDip)
                left = ownerLeft - Width - 10;   // 放到左侧
            if (top + Height > screenBottomDip)
                top = screenBottomDip - Height;

            Left = left;
            Top  = top;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        catch
        {
            // 定位失败时保持居中
        }
    }

    private static string FormatTime(NoteItem item)
    {
        if (item.Due == null) return "长期任务";

        var due = item.Due.Value;
        var now = DateTime.Now;
        var diff = (due.Date - now.Date).Days;

        string datePart = diff switch
        {
            0  => "今天",
            1  => "明天",
            2  => "后天",
            -1 => "昨天",
            < 0 => $"已逾期 {-diff} 天",
            <= 7 => $"周{ChineseWeekday(due.DayOfWeek)}",
            _ => $"{due.Month}月{due.Day}日"
        };

        if (item.AllDay)
            return datePart;

        return $"{datePart}  {due:HH:mm}";
    }

    private static string ChineseWeekday(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday    => "一",
        DayOfWeek.Tuesday   => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday  => "四",
        DayOfWeek.Friday    => "五",
        DayOfWeek.Saturday  => "六",
        _                   => "日"
    };
}
