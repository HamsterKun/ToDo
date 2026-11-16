using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopNote.Models;
using DesktopNote.Services;

namespace DesktopNote;

public partial class MainWindow : Window
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static readonly Brush TextPrimary = Freeze("#E9EEF6");
    private static readonly Brush TextSecondary = Freeze("#8B96A8");
    private static readonly Brush TextMuted = Freeze("#6C7787");
    private static readonly Brush Accent = Freeze("#6AA6FF");
    private static readonly Brush Danger = Freeze("#FF7B72");
    private static readonly Brush Hover = Freeze("#14FFFFFF");
    private static readonly Brush BoxBorder = Freeze("#4A5668");

    private readonly NoteStore _store;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _backdropTimer;
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _visTimer;
    private DateTime _forceShowUntil = DateTime.MinValue;
    private bool _wantVisible = true;
    private int _pendingHide;
    private readonly MiniWindow _mini;

    private IntPtr _hwnd;
    private string? _editingId;
    private FrameworkElement? _editor;
    private string? _editorFor;
    private bool _dragging;
    private int _dragOffX, _dragOffY;
    private bool _resizing;
    private int _rsX, _rsY, _rsW, _rsH;
    private bool _showDone;
    private NoteDetailWindow? _detailWin;

    public MainWindow(NoteStore store, AppSettings settings)
    {
        _store = store;
        _settings = settings;
        _showDone = settings.ShowCompleted;

        InitializeComponent();

        _backdropTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _backdropTimer.Tick += (_, _) => { _backdropTimer.Stop(); UpdateBackdrop(); };

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _tickTimer.Tick += (_, _) =>
        {
            if (_editingId == null) RefreshAll();
            UpdateMini();
        };

        _visTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _visTimer.Tick += (_, _) => TickVisibility();

        _mini = new MiniWindow();
        _mini.Clicked += () => ForceShow();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => ScheduleBackdrop();
        _store.Changed += OnStoreChanged;
        Closed += (_, _) =>
        {
            _store.Flush();
            SaveBounds();
            _store.Changed -= OnStoreChanged;
            _tickTimer.Stop();
            _visTimer.Stop();
            _backdropTimer.Stop();
            _mini.Close();
        };

        // 点击时把 Win32 焦点要过来，保证输入框能打字
        PreviewMouseDown += (_, _) =>
        {
            if (_hwnd != IntPtr.Zero) NativeMethods.SetFocus(_hwnd);
        };

        Deactivated += (_, _) => TickVisibility();
        Activated += (_, _) => TickVisibility();
        // 可见性变化时立即触发检查——用 BeginInvoke 推迟到当前消息处理完毕，
        // 避免在 WPF 窗口关闭流程中途直接调 Show() 引发 InvalidOperationException
        IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(TickVisibility);
    }

    private static Brush Freeze(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private static readonly Brush SectionLine = Freeze("#12FFFFFF");
    private static readonly Brush EditorBg = Freeze("#1AFFFFFF");
    private static readonly Brush EditorBorder = Freeze("#26FFFFFF");

    /// <summary>
    /// 数据变了就重建列表。正在编辑的那一行会复用同一个编辑框实例（见 BuildRow），
    /// 所以重建不会丢掉未保存的输入。
    /// </summary>
    private void OnStoreChanged()
    {
        // 正在编辑的条目被删了，编辑态也就没意义了
        if (_editingId != null && _store.Items.All(i => i.Id != _editingId)) _editingId = null;
        RefreshAll();
        UpdateMini();
    }

    private void EndEdit()
    {
        _editingId = null;
        RefreshAll();
        UpdateMini();
    }

    // ───────────────────────── 生命周期 ─────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Log.Write($"MainWindow 初始化 hwnd=0x{_hwnd:X}");
        ApplyStoredBounds();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _tickTimer.Start();
        _visTimer.Start();
        RefreshAll();
        Dispatcher.BeginInvoke(new Action(UpdateBackdrop), DispatcherPriority.Loaded);
    }

    private void ApplyStoredBounds()
    {
        bool fresh = double.IsNaN(_settings.Left) || double.IsNaN(_settings.Top)
                     || _settings.Width <= 0 || _settings.Height <= 0;

        // 按窗口将要落在的那块显示器算，而不是主屏 / 整个虚拟屏
        IntPtr mon;
        if (fresh)
        {
            mon = NativeMethods.PrimaryMonitor();
        }
        else
        {
            var stored = new NativeMethods.RECT(
                (int)_settings.Left, (int)_settings.Top,
                (int)(_settings.Left + _settings.Width), (int)(_settings.Top + _settings.Height));
            mon = NativeMethods.MonitorFromRect(ref stored, NativeMethods.MONITOR_DEFAULTTONEAREST);
        }

        double scale = NativeMethods.MonitorScale(mon);
        NativeMethods.RECT work;
        if (NativeMethods.TryGetMonitorInfo(mon, out var mi))
            work = mi.rcWork;
        else
            work = new NativeMethods.RECT(0, 0,
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

        int minW = (int)Math.Round(270 * scale);
        int minH = (int)Math.Round(240 * scale);

        int w = fresh ? (int)Math.Round(348 * scale) : (int)_settings.Width;
        int h = fresh ? (int)Math.Round(600 * scale) : (int)_settings.Height;
        w = Math.Clamp(w, minW, Math.Max(minW, work.Width));
        h = Math.Clamp(h, minH, Math.Max(minH, work.Height));

        int x = fresh ? work.Right - w - (int)Math.Round(44 * scale) : (int)_settings.Left;
        int y = fresh ? work.Top + (int)Math.Round(work.Height * 0.11) : (int)_settings.Top;

        // 整个窗口留在该显示器的工作区内，换了显示器布局也不会跑到看不见的地方
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));

        // SourceInitialized 时窗口还在默认显示器上。尺寸按目标显示器 DPI 换成逻辑单位交给 WPF，
        // 位置用 SetWindowPos 按物理像素放（只移不改尺寸）：跨 DPI 移动时 WPF 会保持逻辑尺寸，
        // 若这里同时设物理尺寸，WM_DPICHANGED 会再缩放一次
        Width = w / scale;
        Height = h / scale;
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>窗口当前所在显示器的工作区（物理像素）。</summary>
    private NativeMethods.RECT CurrentWorkArea()
    {
        var mon = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (NativeMethods.TryGetMonitorInfo(mon, out var mi)) return mi.rcWork;
        return new NativeMethods.RECT(0, 0, int.MaxValue / 2, int.MaxValue / 2);
    }

    /// <summary>当前窗口所在显示器的 DPI 缩放。</summary>
    private double CurrentScale()
    {
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return s > 0 ? s : 1;
    }
    private void ScheduleBackdrop()
    {
        _backdropTimer.Stop();
        _backdropTimer.Start();
    }

    /// <summary>壁纸或显示器布局变了，由 App 调用。</summary>
    public void RefreshBackdrop() => ScheduleBackdrop();

    private void UpdateBackdrop()
    {
        if (_hwnd == IntPtr.Zero) return;

        try
        {
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (dpi <= 0) dpi = 1;

            if (!NativeMethods.GetWindowRect(_hwnd, out var r)) return;

            // 面板比窗口内缩 15，壁纸按面板的实际屏幕矩形取，才能和背后的壁纸对上
            int inset = (int)Math.Round(15 * dpi);
            var rect = new Int32Rect(
                r.Left + inset,
                r.Top + inset,
                Math.Max(2, r.Width - inset * 2),
                Math.Max(2, r.Height - inset * 2));

            var src = Backdrop.BlurredCrop(rect, 13 * dpi);

            if (src != null)
            {
                var brush = new ImageBrush(src) { Stretch = Stretch.Fill };
                brush.Freeze();
                WallpaperLayer.Background = brush;
                TintLayer.Background = (Brush)FindResource("TintGlass");
            }
            else
            {
                WallpaperLayer.Background = null;
                TintLayer.Background = (Brush)FindResource("TintSolid");
            }
        }
        catch { }
    }

    // ───────────── 只在"桌面露出来"的时候显示自己 ─────────────

    private void TickVisibility()
    {
        if (_hwnd == IntPtr.Zero || !IsLoaded) return;
        if (UserHidden || DateTime.Now < _forceShowUntil)
        {
            SyncMini();
            return;
        }

        // 两个信号任一成立就显示：桌面在前台，或者便签这块区域根本没被别的窗口压住
        var want = DesktopWatcher.IsDesktopInFront(_hwnd) || !DesktopWatcher.IsCoveredByOther(_hwnd);

        if (want)
        {
            // 出现要快，立刻执行
            _pendingHide = 0;
            if (!_wantVisible)
            {
                _wantVisible = true;
                Log.Write("桌面在前 -> 显示便签");
            }
            EnsureShown();
        }
        else if (_wantVisible && ++_pendingHide >= 2)
        {
            // 消失要稳，连续两次判定才收，避免 Alt+Tab、切窗口时抖一下
            _pendingHide = 0;
            _wantVisible = false;
            if (IsVisible) Hide();
            Log.Write("其它窗口在前 -> 收成迷你窗");
        }

        SyncMini();
    }

    /// <summary>主窗收起且用户没手动隐藏时，在右下角显示迷你窗；全屏应用在前台时也不打扰。</summary>
    private void SyncMini()
    {
        bool show = !UserHidden && !_wantVisible && !IsVisible;
        if (show)
        {
            var mon = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (DesktopWatcher.IsFullscreenOn(mon)) show = false;
        }

        if (show)
        {
            if (!_mini.IsVisible) UpdateMini();
            _mini.ShowAt(_hwnd);
        }
        else if (_mini.IsVisible)
        {
            _mini.Hide();
        }
    }

    private void UpdateMini() => _mini.SetItem(NextDue(), DateTime.Now);

    /// <summary>有时间的未完成条目里最早的一条（已逾期的也算，排在最前）。</summary>
    public NoteItem? NextDue() =>
        _store.Items.Where(i => !i.Done && i.Due.HasValue)
            .OrderBy(i => i.Due!.Value)
            .ThenBy(i => i.CreatedAt)
            .FirstOrDefault();

    /// <summary>迷你窗用的标签文字和是否逾期。</summary>
    internal static (string Chip, bool Late) Describe(NoteItem item, DateTime now)
        => (ChipText(item, now), IsLate(item, now));

    private void EnsureShown()
    {
        // 窗口正在关闭时不能调 Show()
        if (!IsLoaded || _hwnd == IntPtr.Zero) return;

        var changed = false;

        if (!IsVisible)
        {
            Show();
            changed = true;
        }

        // 只在真的没显示出来时才补一次，避免每 400ms 都调 ShowWindow
        if (!NativeMethods.IsWindowVisible(_hwnd))
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            changed = true;
        }

        // Win+D 之后窗口会被 DWM 隐身，Show() 解不开。
        // DWMWA_CLOAK 系统不允许外部写，只能切 topmost 试试；还不行就重建窗口（节流在 App 里做）。
        if (NativeMethods.IsCloaked(_hwnd))
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

            if (NativeMethods.IsCloaked(_hwnd))
            {
                RequestRebuild?.Invoke();
            }
            else
            {
                Log.Write("cloak 已被 topmost 切换解除");
            }

            changed = true;
        }

        if (changed) ScheduleBackdrop();
    }

    /// <summary>窗口被系统隐身且救不回来时，交给 App 重建一个。</summary>
    public event Action? RequestRebuild;

    /// <summary>从托盘或迷你窗手动叫出来：直接激活，方便立刻打字；同时给 8 秒宽限期。</summary>
    public void ForceShow()
    {
        UserHidden = false;
        _forceShowUntil = DateTime.Now.AddSeconds(8);
        _wantVisible = true;
        _pendingHide = 0;
        if (!IsVisible) Show();
        if (_mini.IsVisible) _mini.Hide();
        ScheduleBackdrop();
        if (_hwnd != IntPtr.Zero) NativeMethods.SetForegroundWindow(_hwnd);
    }

    private void SaveBounds()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!NativeMethods.GetWindowRect(_hwnd, out var r)) return;

        // 拖拽/缩放走的是 Win32，这里把 WPF 侧的 Left/Top/尺寸同步回来，避免下次 Show 时跳位
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi <= 0) dpi = 1;
        Left = r.Left / dpi;
        Top = r.Top / dpi;
        Width = r.Width / dpi;
        Height = r.Height / dpi;

        _settings.Left = r.Left;
        _settings.Top = r.Top;
        _settings.Width = r.Width;
        _settings.Height = r.Height;
        _settings.ShowCompleted = _showDone;
        _settings.Save();
    }

    // ───────────────────────── 列表构建 ─────────────────────────

    private void RefreshAll()
    {
        var now = DateTime.Now;
        MonthText.Text = now.Month + "月";
        DayText.Text = now.Day.ToString();

        var offset = Scroller.VerticalOffset;
        ListHost.Children.Clear();

        // 退出或切换编辑后丢掉旧编辑框，下次进入编辑重新取值
        if (_editorFor != _editingId)
        {
            _editor = null;
            _editorFor = null;
        }

        var active = _store.Items.Where(i => !i.Done).ToList();
        var hasDone = _store.Items.Any(i => i.Done);

        if (active.Count == 0 && !hasDone)
        {
            EmptyState.Visibility = Visibility.Visible;
            UpdateStats(now, 0, hasDone);
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;

        // 没写时间的条目单独成组、置顶，按添加顺序排
        AddSection("长期任务", active.Where(i => !i.Due.HasValue).OrderBy(i => i.CreatedAt), now);
        AddSection("已逾期", active.Where(i => i.Due.HasValue && i.Due.Value.Date < now.Date), now, true);
        AddSection("今天", active.Where(i => i.Due.HasValue && i.Due.Value.Date == now.Date), now);
        AddSection("明天", active.Where(i => i.Due.HasValue && i.Due.Value.Date == now.Date.AddDays(1)), now);
        AddSection("接下来", active.Where(i => i.Due.HasValue && i.Due.Value.Date > now.Date.AddDays(1)
                                            && i.Due.Value.Date <= now.Date.AddDays(7)), now);
        AddSection("更远", active.Where(i => i.Due.HasValue && i.Due.Value.Date > now.Date.AddDays(7)), now);

        if (_showDone)
        {
            var done = _store.Items.Where(i => i.Done)
                .OrderByDescending(i => i.DoneAt ?? DateTime.MinValue).Take(60).ToList();
            if (done.Count > 0)
            {
                ListHost.Children.Add(BuildSectionHeader("已完成", done.Count, false));
                foreach (var it in done) ListHost.Children.Add(BuildRow(it, now));
            }
        }

        UpdateStats(now, active.Count, hasDone);

        Dispatcher.BeginInvoke(new Action(() => Scroller.ScrollToVerticalOffset(offset)), DispatcherPriority.Loaded);
    }

    private void AddSection(string title, IEnumerable<NoteItem> items, DateTime now, bool danger = false)
    {
        var list = items.OrderBy(i => i.Due ?? DateTime.MaxValue).ThenBy(i => i.CreatedAt).ToList();
        if (list.Count == 0) return;

        ListHost.Children.Add(BuildSectionHeader(title, list.Count, danger));
        foreach (var it in list) ListHost.Children.Add(BuildRow(it, now));
    }

    private FrameworkElement BuildSectionHeader(string title, int count, bool danger)
    {
        var g = new Grid { Margin = new Thickness(7, 13, 7, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = title,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = danger ? Danger : TextSecondary
        };
        var num = new TextBlock
        {
            Text = count.ToString(),
            FontSize = 10,
            Foreground = TextMuted,
            Margin = new Thickness(6, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var line = new Border
        {
            Height = 1,
            Background = SectionLine,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0)
        };

        Grid.SetColumn(name, 0);
        Grid.SetColumn(line, 1);
        Grid.SetColumn(num, 2);
        g.Children.Add(name);
        g.Children.Add(line);
        g.Children.Add(num);
        return g;
    }

    private FrameworkElement BuildRow(NoteItem item, DateTime now)
    {
        if (_editingId == item.Id)
        {
            // 复用已有编辑框，保留用户正在输入的内容
            if (_editor != null && _editorFor == item.Id) return _editor;
            _editorFor = item.Id;
            return _editor = BuildEditor(item);
        }

        var isLate = IsLate(item, now);

        var row = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 7, 5, 7),
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Arrow
        };

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = BuildCheck(item);
        check.Margin = new Thickness(1, 1, 9, 0);

        var title = new TextBlock
        {
            Text = item.Title,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = item.Done ? TextMuted : isLate ? Danger : TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 18
        };
        if (item.Done) title.TextDecorations = TextDecorations.Strikethrough;

        var chip = new TextBlock
        {
            Text = ChipText(item, now),
            FontSize = 10,
            Foreground = item.Done ? TextMuted : isLate ? Danger : TextSecondary,
            Margin = new Thickness(8, 2, 2, 0),
            VerticalAlignment = VerticalAlignment.Top
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            Margin = new Thickness(2, 0, 0, 0)
        };

        var editBtn = new Button
        {
            Style = (Style)FindResource("IconBtn"),
            Content = "\uE70F",
            Width = 20,
            Height = 20,
            FontSize = 10,
            ToolTip = "编辑"
        };
        editBtn.Click += (_, _) => { _editingId = item.Id; RefreshAll(); };
        // 阻止按钮点击事件冒泡到 row，避免触发详情窗口
        editBtn.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
        editBtn.MouseLeftButtonDown += (_, e) => e.Handled = true;

        var delBtn = new Button
        {
            Style = (Style)FindResource("IconBtn"),
            Content = "\uE74D",
            Width = 20,
            Height = 20,
            FontSize = 10,
            ToolTip = "删除"
        };
        delBtn.Click += (_, _) =>
        {
            if (_editingId == item.Id) _editingId = null;
            _store.Remove(item);
        };
        // 阻止按钮点击事件冒泡到 row
        delBtn.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
        delBtn.MouseLeftButtonDown += (_, e) => e.Handled = true;

        actions.Children.Add(editBtn);
        actions.Children.Add(delBtn);

        Grid.SetColumn(check, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(chip, 2);
        Grid.SetColumn(actions, 3);
        g.Children.Add(check);
        g.Children.Add(title);
        g.Children.Add(chip);
        g.Children.Add(actions);

        row.Child = g;

        row.MouseEnter += (_, _) => { row.Background = Hover; actions.Opacity = 1; };
        row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; actions.Opacity = 0; };
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                // 双击 → 内联编辑
                _detailWin?.Close();
                _detailWin = null;
                _editingId = item.Id;
                RefreshAll();
                return;
            }
            // 单击 → 打开详情备注窗口（避免重复打开同一条目）
            if (_detailWin != null && _detailWin.ItemId == item.Id) return;
            _detailWin?.Close();
            _detailWin = new NoteDetailWindow(item, _store, this);
            _detailWin.Closed += (_, _) => _detailWin = null;
            _detailWin.Show();
        };

        return row;
    }

    private Border BuildCheck(NoteItem item)
    {
        var box = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1.3),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderBrush = item.Done ? Accent : BoxBorder
        };

        var mark = new TextBlock
        {
            Text = "\uE73E",
            FontFamily = IconFont,
            FontSize = 8,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = item.Done ? Visibility.Visible : Visibility.Collapsed
        };
        box.Child = mark;

        if (item.Done) box.Background = Accent;

        box.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            item.Done = !item.Done;
            item.DoneAt = item.Done ? DateTime.Now : null;
            _store.Touch();
        };

        return box;
    }

    private FrameworkElement BuildEditor(NoteItem item)
    {
        var wrap = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = EditorBg,
            BorderBrush = EditorBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 8, 9, 7),
            Margin = new Thickness(0, 3, 0, 3)
        };

        var sp = new StackPanel();

        var titleBox = new TextBox
        {
            Style = (Style)FindResource("FlatBox"),
            Text = item.Title,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };

        var timeRow = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var clock = new TextBlock
        {
            Text = "\uE823",
            FontFamily = IconFont,
            FontSize = 10,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        var timeBox = new TextBox
        {
            Style = (Style)FindResource("FlatBox"),
            Text = TimeExpr(item),
            FontSize = 11.5,
            Foreground = TextSecondary
        };

        Grid.SetColumn(clock, 0);
        Grid.SetColumn(timeBox, 1);
        timeRow.Children.Add(clock);
        timeRow.Children.Add(timeBox);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        Button MakeBtn(string text, Brush fg)
        {
            var b = new Button
            {
                Style = (Style)FindResource("GhostBtn"),
                Content = text,
                Foreground = fg,
                Margin = new Thickness(4, 0, 0, 0)
            };
            return b;
        }

        var delB = MakeBtn("删除", Danger);
        var cancelB = MakeBtn("取消", TextSecondary);
        var saveB = MakeBtn("保存", Accent);

        delB.Click += (_, _) => { _editingId = null; _store.Remove(item); };
        cancelB.Click += (_, _) => EndEdit();

        // 用户没动时间框就原样保留，不重新解析，避免逾期 / 跨年的日期被"顺延"
        var originalExpr = timeBox.Text;

        void Commit()
        {
            var title = titleBox.Text.Trim();
            if (title.Length == 0) return;

            var expr = timeBox.Text.Trim();
            if (expr == originalExpr.Trim())
            {
                // 时间没改
            }
            else if (expr.Length == 0)
            {
                // 清空时间 = 转为长期任务
                item.Due = null;
                item.AllDay = false;
            }
            else
            {
                var (_, due, allDay) = QuickParser.Parse(expr, DateTime.Now);
                if (due.HasValue)
                {
                    item.Due = due;
                    item.AllDay = allDay;
                }
                // 解析不出时间就保留原值，不悄悄改成长期任务
            }

            item.Title = title;
            _editingId = null;
            _store.Touch();
        }

        saveB.Click += (_, _) => Commit();
        titleBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; EndEdit(); }
        };
        timeBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; EndEdit(); }
        };

        btnRow.Children.Add(delB);
        btnRow.Children.Add(cancelB);
        btnRow.Children.Add(saveB);

        sp.Children.Add(titleBox);
        sp.Children.Add(timeRow);
        sp.Children.Add(btnRow);
        wrap.Child = sp;

        // 只在第一次出现时把焦点放到标题框；列表重建时编辑框会被重新挂上来，不能再抢焦点
        var focused = false;
        wrap.Loaded += (_, _) =>
        {
            if (focused) return;
            focused = true;
            titleBox.Focus();
            titleBox.CaretIndex = titleBox.Text.Length;
        };

        return wrap;
    }

    private void UpdateStats(DateTime now, int activeCount, bool hasDone)
    {
        var todayCount = _store.Items.Count(i => !i.Done && i.Due.HasValue && i.Due.Value.Date == now.Date);
        var lateCount = _store.Items.Count(i => IsLate(i, now));

        if (activeCount == 0)
        {
            SubTitle.Text = "空闲中";
            SubTitle.Foreground = TextSecondary;
        }
        else if (lateCount > 0)
        {
            SubTitle.Text = $"共 {activeCount} 项 · {lateCount} 项已过期";
            SubTitle.Foreground = Danger;
        }
        else
        {
            SubTitle.Text = todayCount > 0 ? $"共 {activeCount} 项 · 今天 {todayCount} 项" : $"共 {activeCount} 项待办";
            SubTitle.Foreground = TextSecondary;
        }

        var doneCount = _store.Items.Count(i => i.Done);
        DoneToggle.Content = _showDone ? "收起已完成" : $"已完成 {doneCount}";
        DoneToggle.Visibility = hasDone ? Visibility.Visible : Visibility.Collapsed;
        FooterStat.Text = activeCount > 0 ? $"{activeCount} 项待办" : "";
    }

    private static bool IsLate(NoteItem item, DateTime now)
    {
        if (item.Done || !item.Due.HasValue) return false;
        // 只有日期的条目，当天不算过期
        return item.AllDay ? item.Due.Value.Date < now.Date : item.Due.Value < now;
    }

    private static string ChipText(NoteItem item, DateTime now)
    {
        if (!item.Due.HasValue) return "";

        var d = item.Due.Value;
        var today = now.Date;

        if (item.AllDay)
        {
            if (d.Date == today) return "今天";
            if (d.Date == today.AddDays(1)) return "明天";
            if (d.Date == today.AddDays(2)) return "后天";
            if (d.Date == today.AddDays(-1)) return "昨天";
            return d.ToString("M月d日");
        }

        string prefix;
        if (d.Date == today) prefix = "";
        else if (d.Date == today.AddDays(1)) prefix = "明天 ";
        else if (d.Date == today.AddDays(2)) prefix = "后天 ";
        else if (d.Date == today.AddDays(-1)) prefix = "昨天 ";
        else if (d.Date < today) prefix = d.ToString("M月d日") + " ";
        else if ((d.Date - today).TotalDays < 7) prefix = "周" + "一二三四五六日"[(int)d.DayOfWeek == 0 ? 6 : (int)d.DayOfWeek - 1] + " ";
        else prefix = d.ToString("M月d日") + " ";

        return prefix + d.ToString("HH:mm");
    }

    private static string TimeExpr(NoteItem item)
    {
        if (!item.Due.HasValue) return "";
        var d = item.Due.Value;
        var today = DateTime.Now.Date;

        // 过去的日期和非今年的日期都带上年份，用户只改时刻时解析器不会把它顺延到明年
        var fmt = d.Date < today || d.Year != today.Year ? "yyyy年M月d日" : "M月d日";

        if (item.AllDay)
        {
            if (d.Date == today) return "今天";
            if (d.Date == today.AddDays(1)) return "明天";
            return d.ToString(fmt);
        }
        return d.ToString(fmt + " HH:mm");
    }

    // ───────────────────────── 交互 ─────────────────────────

    private void AddBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AddHint.Visibility = AddBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            QuickAdd();
        }
        else if (e.Key == Key.Escape)
        {
            AddBox.Text = "";
            Keyboard.ClearFocus();
        }
    }

    private void QuickAdd()
    {
        var raw = AddBox.Text.Trim();
        if (raw.Length == 0) return;

        var (title, due, allDay) = QuickParser.Parse(raw, DateTime.Now);
        if (title.Length == 0) return;

        // 不把用户原文写进日志
        Log.Write($"快速添加: due={(due.HasValue ? due.Value.ToString("yyyy-MM-dd HH:mm") : "长期")} allDay={allDay}");
        _store.Add(new NoteItem { Title = title, Due = due, AllDay = allDay });
        AddBox.Text = "";
        AddBox.Focus();
    }

    private void DoneToggle_Click(object sender, RoutedEventArgs e)
    {
        _showDone = !_showDone;
        _settings.ShowCompleted = _showDone;
        _settings.Save();
        RefreshAll();
    }

    private void HideBtn_Click(object sender, RoutedEventArgs e)
    {
        UserHidden = true;
        Hide();
    }

    /// <summary>用户主动收起到托盘时为 true，避免健康检查把它又弹出来；迷你窗也一起收掉。</summary>
    public bool UserHidden
    {
        get => _userHidden;
        set
        {
            _userHidden = value;
            if (value && _mini.IsVisible) _mini.Hide();
        }
    }
    private bool _userHidden;

    // ── 拖动 ──────────────────────────────────────────────

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        NativeMethods.GetWindowRect(_hwnd, out var r);
        NativeMethods.GetCursorPos(out var p);
        _dragOffX = p.X - r.Left;
        _dragOffY = p.Y - r.Top;
        _dragging = true;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        NativeMethods.GetCursorPos(out var p);
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, p.X - _dragOffX, p.Y - _dragOffY, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        ((UIElement)sender).ReleaseMouseCapture(); // 会触发 LostMouseCapture，收尾在那里做
        EndDrag();
    }

    /// <summary>捕获被系统夺走（Alt+Tab、弹窗、锁屏）时也要结束拖动，否则鼠标松开后窗口还跟着走。</summary>
    private void Header_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        SaveBounds();
        ScheduleBackdrop();
    }

    // ── 缩放 ──────────────────────────────────────────────

    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        NativeMethods.GetWindowRect(_hwnd, out var r);
        NativeMethods.GetCursorPos(out var p);
        _rsX = p.X;
        _rsY = p.Y;
        _rsW = r.Width;
        _rsH = r.Height;
        _rsWork = CurrentWorkArea();
        _resizing = true;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private NativeMethods.RECT _rsWork;

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_resizing) return;
        NativeMethods.GetCursorPos(out var p);

        double dpi = CurrentScale();
        int minW = (int)Math.Round(270 * dpi);
        int minH = (int)Math.Round(240 * dpi);
        // 最大不超过所在显示器的工作区
        int maxW = Math.Max(minW, _rsWork.Width);
        int maxH = Math.Max(minH, _rsWork.Height);

        int w = Math.Clamp(_rsW + (p.X - _rsX), minW, maxW);
        int h = Math.Clamp(_rsH + (p.Y - _rsY), minH, maxH);

        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, w, h,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private void Grip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        ((UIElement)sender).ReleaseMouseCapture();
        EndResize();
    }

    private void Grip_LostMouseCapture(object sender, MouseEventArgs e) => EndResize();

    private void EndResize()
    {
        if (!_resizing) return;
        _resizing = false;
        SaveBounds();
        UpdateBackdrop();
    }
}
