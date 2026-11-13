using Microsoft.Win32;

namespace DesktopNote.Services;

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ToDo";

    public static string ExePath =>
        Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    private static string Command => $"\"{ExePath}\"";

    /// <summary>注册表里已经指向当前 exe，就不必再写。</summary>
    public static bool IsCurrent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var v = key?.GetValue(ValueName) as string;
            return string.Equals(v, Command, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Write($"读取开机自启失败：{ex.Message}");
            return false;
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, Command);
            else if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName, false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"设置开机自启失败（enabled={enabled}）：{ex.Message}");
            return false;
        }
    }
}
