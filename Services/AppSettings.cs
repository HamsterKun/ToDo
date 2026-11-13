using System.IO;
using System.Text.Json;

namespace DesktopNote.Services;

public class AppSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 348;
    public double Height { get; set; } = 600;
    public bool AutoStart { get; set; }
    public bool ShowCompleted { get; set; }

    private static string PathFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToDo", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(PathFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFile));
                if (s != null) return s;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"settings.json 读取失败，使用默认设置：{ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        var tmp = PathFile + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, PathFile, true);
        }
        catch (Exception ex)
        {
            Log.Write($"settings.json 保存失败：{ex.Message}");
        }
    }
}
