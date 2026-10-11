using System;
using System.Collections.Generic;

namespace Handy.Services;

// The file logger remains complete; the settings display retains a bounded tail.
public sealed class DisplayLogBuffer
{
    private const int Capacity = 64 * 1024;
    private readonly object _sync = new();
    private readonly Queue<string> _lines = new();
    private int _characters;
    private bool _dirty;

    public void Append(string line)
    {
        if (line.Length > Capacity - Environment.NewLine.Length)
            line = line.Substring(line.Length - Capacity + Environment.NewLine.Length);
        lock (_sync)
        {
            _lines.Enqueue(line);
            _characters += line.Length + Environment.NewLine.Length;
            while (_characters > Capacity)
                _characters -= _lines.Dequeue().Length + Environment.NewLine.Length;
            _dirty = true;
        }
    }

    public string? TakeChangedText()
    {
        lock (_sync)
        {
            if (!_dirty) return null;
            _dirty = false;
            return string.Join(Environment.NewLine, _lines);
        }
    }
}
