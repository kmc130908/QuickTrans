using System.IO;
using System.Text.Json;

namespace QuickTrans.Services;

public sealed class HistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public string Source { get; set; } = "";
    public string Result { get; set; } = "";
    public string Target { get; set; } = "";
    public string Mode { get; set; } = "translate";
    public string Engine { get; set; } = "";
}

/// <summary>
/// 轻量本地翻译历史存储（history.json，最多保留 100 条）。
/// 按需读写磁盘，平时不常驻内存；查看历史在独立子进程中打开。
/// </summary>
public static class HistoryService
{
    private const int MaxItems = 100;
    private static readonly object Gate = new();

    public static string HistoryPath => Path.Combine(AppContext.BaseDirectory, "history.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static List<HistoryItem> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(HistoryPath)) return new List<HistoryItem>();
                var list = JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(HistoryPath), JsonOpts);
                return list ?? new List<HistoryItem>();
            }
            catch
            {
                return new List<HistoryItem>();
            }
        }
    }

    public static void Add(string source, string result, string target, string mode, string engineLabel)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(result)) return;

        lock (Gate)
        {
            try
            {
                var list = Load();

                // 避免连续保存完全相同的一条
                if (list.Count > 0
                    && string.Equals(list[0].Source, source, StringComparison.Ordinal)
                    && string.Equals(list[0].Target, target, StringComparison.Ordinal)
                    && string.Equals(list[0].Mode, mode, StringComparison.Ordinal))
                {
                    list[0].Result = result;
                    list[0].Time = DateTime.Now;
                }
                else
                {
                    list.Insert(0, new HistoryItem
                    {
                        Time = DateTime.Now,
                        Source = source.Length > 2000 ? source[..2000] + "…" : source,
                        Result = result.Length > 4000 ? result[..4000] + "…" : result,
                        Target = target,
                        Mode = mode,
                        Engine = engineLabel,
                    });
                }

                if (list.Count > MaxItems)
                    list.RemoveRange(MaxItems, list.Count - MaxItems);

                SaveCore(list);
            }
            catch { /* 写盘失败不影响翻译主流程 */ }
        }
    }

    public static void Delete(string id)
    {
        lock (Gate)
        {
            var list = Load();
            if (list.RemoveAll(x => string.Equals(x.Id, id, StringComparison.Ordinal)) > 0)
                SaveCore(list);
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try { if (File.Exists(HistoryPath)) File.Delete(HistoryPath); } catch { }
        }
    }

    private static void SaveCore(List<HistoryItem> list)
    {
        try
        {
            var tmp = HistoryPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOpts));
            File.Move(tmp, HistoryPath, overwrite: true);
        }
        catch { }
    }
}
