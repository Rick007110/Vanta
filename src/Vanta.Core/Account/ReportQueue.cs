using System.Text.Json;

namespace Vanta.Core.Account;

/// <summary>A report or withdrawal waiting to be sent (offline, server down). One per (game, cheat, fingerprint): the newest wins.</summary>
public sealed class QueuedReport
{
    public string Kind { get; set; } = "report";   // report | withdraw
    public ReportItem Item { get; set; } = null!;
    public DateTimeOffset Queued { get; set; }
    public int Attempts { get; set; }
    public string Key => $"{Item.GameId}|{Item.CheatId}|{Item.Fingerprint}";
}

/// <summary>Persistent queue in %LOCALAPPDATA%\Vanta\report-queue.json. Thread-safe.</summary>
public sealed class ReportQueue
{
    private readonly object _gate = new();
    private readonly List<QueuedReport> _items = new();
    public string? File { get; }
    public const int MaxItems = 500;

    public ReportQueue(string? file)
    {
        File = file;
        try
        {
            if (file != null && System.IO.File.Exists(file))
                _items.AddRange(JsonSerializer.Deserialize<List<QueuedReport>>(System.IO.File.ReadAllText(file), Json.Options) ?? new());
        }
        catch { /* corrupt: start empty */ }
        _items.RemoveAll(i => i.Item == null);
    }

    public int Count { get { lock (_gate) return _items.Count; } }
    public List<QueuedReport> Snapshot() { lock (_gate) return _items.ToList(); }

    public void Put(QueuedReport q)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.Key == q.Key);
            _items.Add(q);
            if (_items.Count > MaxItems) _items.RemoveRange(0, _items.Count - MaxItems);
            Save();
        }
    }

    /// <summary>Removes the entry if it is still this exact one (a newer vote for the same cheat stays queued).</summary>
    public void Done(QueuedReport q) { lock (_gate) { if (_items.Remove(q)) Save(); } }
    public void Clear() { lock (_gate) { _items.Clear(); Save(); } }
    public void Touch() { lock (_gate) Save(); }

    private void Save()
    {
        if (File == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json.Options));
            System.IO.File.Move(tmp, File, true);
        }
        catch { /* disk full / locked: stays in memory */ }
    }
}
