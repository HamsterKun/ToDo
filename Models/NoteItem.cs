namespace DesktopNote.Models;

public class NoteItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    /// <summary>截止/开始时间。null 表示长期任务（没写时间），在列表顶部单独成组。</summary>
    public DateTime? Due { get; set; }
    /// <summary>true 表示只有日期没有具体时刻。</summary>
    public bool AllDay { get; set; }
    public bool Done { get; set; }
    public DateTime? DoneAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
