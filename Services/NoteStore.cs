using System.IO;
using System.Text.Json;
using DesktopNote.Models;

namespace DesktopNote.Services;

public class NoteStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer;
    private bool _dirty;

    public List<NoteItem> Items { get; private set; } = new();
    public event Action? Changed;

    public NoteStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToDo");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "notes.json");

        _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Flush(); };

        Load();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;

        try
        {
            var text = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<NoteItem>>(text, _json);
            if (list != null) Items = list;
        }
        catch (Exception ex)
        {
            // 文件坏了不能直接用空列表覆盖：先把原文件挪到一边留证，再从空列表开始
            var backup = $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(_path, backup);
                Log.Write($"notes.json 解析失败，已备份到 {Path.GetFileName(backup)}：{ex.GetType().Name} {ex.Message}");
            }
            catch (Exception moveEx)
            {
                // 连备份都做不了，就不再写这个文件，避免覆盖
                Log.Write($"notes.json 解析失败且无法备份，本次运行不写入：{moveEx.Message}");
                ReadOnly = true;
            }
            Items = new List<NoteItem>();
        }
    }

    /// <summary>数据文件损坏且无法备份时为 true，此时不写盘。</summary>
    public bool ReadOnly { get; private set; }

    public void Add(NoteItem item)
    {
        Items.Add(item);
        Save();
        Changed?.Invoke();
    }

    public void Remove(NoteItem item)
    {
        Items.Remove(item);
        Save();
        Changed?.Invoke();
    }

    /// <summary>原地改过某一项后调用：保存并通知界面刷新。</summary>
    public void Touch()
    {
        Save();
        Changed?.Invoke();
    }

    public void Save()
    {
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>立即写盘。退出、注销前也要调用，防止防抖期间的修改丢失。</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        if (!_dirty || ReadOnly) return;

        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items, _json));
            File.Move(tmp, _path, true);
            _dirty = false;
        }
        catch (Exception ex)
        {
            Log.Write($"保存 notes.json 失败：{ex.GetType().Name} {ex.Message}");
        }
    }
}
