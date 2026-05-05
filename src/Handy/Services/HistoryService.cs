using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Handy.Services;

public sealed class HistoryEntry
{
    public string Text { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Simple bounded transcription history backed by JSON on disk.
/// </summary>
public sealed class HistoryService
{
    public event Action? Changed;

    private readonly string _path;
    private readonly List<HistoryEntry> _entries = new();
    private int _limit;
    private readonly object _lock = new();

    public HistoryService(string dataDir, int limit)
    {
        _path = Path.Combine(dataDir, "history.json");
        _limit = Math.Max(1, limit);
        Load();
    }

    public void SetLimit(int limit)
    {
        lock (_lock)
        {
            _limit = Math.Max(1, limit);
            TrimUnlocked();
            SaveUnlocked();
        }
    }

    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_lock)
        {
            _entries.Add(new HistoryEntry { Text = text, TimestampUtc = DateTime.UtcNow });
            TrimUnlocked();
            SaveUnlocked();
        }
        try { Changed?.Invoke(); } catch { }
    }

    public void Remove(HistoryEntry entry)
    {
        lock (_lock)
        {
            _entries.Remove(entry);
            SaveUnlocked();
        }
        try { Changed?.Invoke(); } catch { }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            SaveUnlocked();
        }
        try { Changed?.Invoke(); } catch { }
    }

    public string? Last()
    {
        lock (_lock)
            return _entries.Count == 0 ? null : _entries[^1].Text;
    }

    public HistoryEntry? LastEntry()
    {
        lock (_lock)
            return _entries.Count == 0 ? null : _entries[^1];
    }

    public IReadOnlyList<HistoryEntry> Snapshot()
    {
        lock (_lock) return _entries.ToArray();
    }

    private void TrimUnlocked()
    {
        if (_entries.Count > _limit)
            _entries.RemoveRange(0, _entries.Count - _limit);
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            var items = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
            if (items is not null) _entries.AddRange(items);
        }
        catch (Exception ex)
        {
            Log.Warn($"History load failed: {ex.Message}");
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"History save failed: {ex.Message}");
        }
    }
}
