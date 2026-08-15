using System.Collections.Concurrent;

namespace MicroVMStudio.Core.Runtime;

/// <summary>
/// In-memory ring buffer of console output per VM, plus a live feed for the
/// Logs tab. Buffers are lost on app restart; the guest redials the collector
/// with backoff, so a running VM repopulates on its own.
/// </summary>
public sealed class VmLogHub(int capacity = 5000)
{
    private readonly ConcurrentDictionary<string, RingBuffer> _buffers = new();

    /// <summary>Raised for every line appended, so open log views can tail live.</summary>
    public event Action<string, string>? LineAppended;

    public void Append(string vmId, string line)
    {
        _buffers.GetOrAdd(vmId, _ => new RingBuffer(capacity)).Add(line);
        LineAppended?.Invoke(vmId, line);
    }

    public IReadOnlyList<string> Tail(string vmId, int lines) =>
        _buffers.TryGetValue(vmId, out var buffer)
            ? buffer.Tail(Math.Clamp(lines, 1, capacity))
            : [];

    public void Clear(string vmId) => _buffers.TryRemove(vmId, out _);

    private sealed class RingBuffer(int capacity)
    {
        private readonly Queue<string> _lines = new(capacity);
        private readonly Lock _gate = new();

        public void Add(string line)
        {
            lock (_gate)
            {
                if (_lines.Count >= capacity) _lines.Dequeue();
                _lines.Enqueue(line);
            }
        }

        public IReadOnlyList<string> Tail(int count)
        {
            lock (_gate)
            {
                return _lines.Count <= count
                    ? [.. _lines]
                    : [.. _lines.Skip(_lines.Count - count)];
            }
        }
    }
}
