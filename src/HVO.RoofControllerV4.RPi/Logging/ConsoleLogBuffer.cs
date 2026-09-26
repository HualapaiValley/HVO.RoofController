using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HVO.RoofControllerV4.RPi.Logging;

public sealed class ConsoleLogBuffer
{
    private readonly object _sync = new();
    private readonly Queue<ConsoleLogEntry> _entries;
    private readonly IOptionsMonitor<ConsoleLogBufferOptions> _optionsMonitor;

    public ConsoleLogBuffer(IOptionsMonitor<ConsoleLogBufferOptions> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
        var capacity = Math.Max(1, optionsMonitor.CurrentValue.Capacity);
        _entries = new Queue<ConsoleLogEntry>(capacity);
    }

    public ConsoleLogBufferOptions Options => _optionsMonitor.CurrentValue;

    public event EventHandler<ConsoleLogEntry>? EntryAdded;

    internal void Append(ConsoleLogEntry entry)
    {
        lock (_sync)
        {
            var capacity = Math.Max(1, Options.Capacity);

            while (_entries.Count >= capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }

        EntryAdded?.Invoke(this, entry);
    }

    public IReadOnlyList<ConsoleLogEntry> GetSnapshot()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }
}
