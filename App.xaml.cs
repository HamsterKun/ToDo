using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopNote.Services;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace DesktopNote;

public partial class App : Application
{
    private const string ShowSignalName = @"Local\ToDo.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;
    private NoteStore _store = null!;
    private AppSettings _settings = null!;
    private MainWindow? _win;
    private WF.NotifyIcon _tray = null!;
    private DispatcherTimer _health = null!;
    private Icon? _icon;
    private int _restarts;
    private DateTime _restartWindowStart = DateTime.MinValue;
    private DateTime _lastRebuild = DateTime.MinValue;
    private bool _rebuilding;
    private bool _syncingAutoStart;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Local\ToDo.SingleInstance", out var created);
        if (!created)
        {
            // 已经有一个在跑了：叫它把便签显示出来，自己退出
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var ev))
                    using (ev) ev.Set();
            }
            catch { }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        Log.Write($"==== 启动 pid={Environment.ProcessId} exe={AutoStart.ExePath}");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("未处理异常: " + args.Exception);
            args.Handled = true;
        };

        _store = new NoteStore();
        _settings = AppSettings.Load();

        // 只有路径和注册表里不一致时才写，exe 换位置后自启也不会失效
        if (_settings.AutoStart && !AutoStart.IsCurrent())
            AutoStart.Set(true);

        CreateWindow();
        CreateTray();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromSignal)), null, Timeout.Infinite, false);

        // 关机 / 注销时 OnExit 不一定来得及跑，先把数据落盘
        SessionEnding += (_, _) =>
        {
            _store.Flush();
            _settings.Save();
        };

        // 换壁纸、改显示器布局后重新取壁纸
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _health = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(4) };
        _health.Tick += (_, _) => EnsureAlive();
        _health.Start();
    }

    private void ShowFromSignal()
    {
        Log.Write("收到第二个实例的唤起请求");
        if (_win == null) CreateWindow();
        _win?.ForceShow();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.Desktop) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Backdrop.Reload();
            _win?.RefreshBackdrop();
        }));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Backdrop.Reload();
            _win?.RefreshBackdrop();
        }));
    }

    private void CreateWindow()
    {
        try
        {
            var win = new MainWindow(_store, _settings);
            _win = win;
            // 只清自己：重建时旧窗口的 Closed 晚于新窗口创建，不能把新窗口的引用清掉
            win.Closed += (s, _) => { if (ReferenceEquals(_win, s)) _win = null; };
            win.RequestRebuild += RebuildWindow;
            win.Show();
            var h = new WindowInteropHelper(win).Handle;
            Log.Write($"窗口已创建 hwnd=0x{h:X} vis={NativeMethods.IsWindowVisible(h)}");
        }
        catch (Exception ex)
        {
            Log.Write("窗口创建失败: " + ex);
            _win = null;
        }
    }

    /// <summary>窗口被 Win+D 之类弄成隐身又救不回来时，换一个新窗口。5 秒内最多一次。</summary>
    private void RebuildWindow()
    {
        if (_rebuilding) return;
        if ((DateTime.Now - _lastRebuild).TotalSeconds < 5) return;
        _lastRebuild = DateTime.Now;
        Log.Write("窗口仍被隐身，重建窗口");

        _rebuilding = true;
        try
        {
            var old = _win;
            _win = null;
            try { old?.Close(); } catch (Exception ex) { Log.Write("关闭旧窗口失败: " + ex.Message); }
            CreateWindow();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    /// <summary>Explorer 重启会连带销毁桌面层的子窗口，这里检测到就重建。</summary>
    private void EnsureAlive()
    {
        if (_win != null)
        {
            var h = new WindowInteropHelper(_win).Handle;
            if (h != IntPtr.Zero && NativeMethods.IsWindow(h)) return;

            Log.Write("探测到窗口句柄失效");
            var old = _win;
            _win = null;
            try { old.Close(); } catch { }
        }

        // 10 分钟内最多重建 30 次，过了窗口期重新计数，不会一次失控就永久放弃
        if ((DateTime.Now - _restartWindowStart).TotalMinutes > 10)
        {
            _restartWindowStart = DateTime.Now;
            _restarts = 0;
        }
        if (_restarts >= 30) return;
        _restarts++;

        Log.Write("窗口已丢失，准备重建");
        CreateWindow();
    }

    private void CreateTray()
    {
        _icon = BuildIcon();

        var menu = new WF.ContextMenuStrip();
        var showItem = new WF.ToolStripMenuItem("显示便签");
        showItem.Click += (_, _) => ToggleWindow();

        var startItem = new WF.ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = _settings.AutoStart };
        startItem.CheckedChanged += (_, _) =>
        {
            // 失败回滚时会再次触发 CheckedChanged，用标志挡住，避免反复写注册表、无限递归
            if (_syncingAutoStart) return;
            var want = startItem.Checked;
            if (!AutoStart.Set(want))
            {
                _syncingAutoStart = true;
                try { startItem.Checked = !want; }
                finally { _syncingAutoStart = false; }
                return;
            }
            _settings.AutoStart = want;
            _settings.Save();
        };

        var exitItem = new WF.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApp();

        menu.Items.Add(showItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(startItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray = new WF.NotifyIcon
        {
            Icon = _icon,
            Text = "时笺 · 未来日程",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ToggleWindow();
    }

    private void ToggleWindow()
    {
        if (_win == null)
        {
            CreateWindow();
            return;
        }

        if (_win.IsVisible)
        {
            _win.UserHidden = true;
            _win.Hide();
        }
        else
        {
            _win.ForceShow();
        }
    }

    private static Icon BuildIcon()
    {
        // 从内嵌资源加载 app.ico（包含 16/24/32/48/64/128/256 多尺寸）
        var asm = Assembly.GetExecutingAssembly();
        // 资源名称：AssemblyName + 路径，点替换分隔符
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
        if (resourceName != null)
        {
            using var stream = asm.GetManifestResourceStream(resourceName)!;
            return new Icon(stream, new System.Drawing.Size(32, 32));
        }

        // 回退：从 exe 同目录的 app.ico 文件加载
        var exeDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName) ?? ".";
        var icoPath = Path.Combine(exeDir, "app.ico");
        if (File.Exists(icoPath))
            return new Icon(icoPath, 32, 32);

        // 最终回退：单色蓝方块
        using var bmp = new Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 106, 166, 255));
        }
        var h = bmp.GetHicon();
        try   { using var tmp = Icon.FromHandle(h); return (Icon)tmp.Clone(); }
        finally { NativeMethods.DestroyIcon(h); }
    }

    private void ExitApp()
    {
        _store.Flush();
        _settings.Save();
        _health?.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _icon?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 走到这里的不一定是托盘"退出"（例如 Shutdown 被别处调用），再落一次盘；没改动时 Flush 是空操作
        try { _store?.Flush(); } catch { }
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
