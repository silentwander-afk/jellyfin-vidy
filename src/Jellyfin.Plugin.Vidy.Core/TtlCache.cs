using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Vidy.Core;

/// <summary>
/// Small in-memory cache with per-call TTL, a size cap and in-flight de-duplication, so two identical
/// requests that arrive together produce a single upstream call. A zero TTL disables storing (but
/// still de-duplicates concurrent calls). The first caller's cancellation token drives the shared call.
/// </summary>
public sealed class TtlCache
{
    private sealed record Entry(object Value, DateTimeOffset Expires, DateTimeOffset Created);

    private readonly ConcurrentDictionary<string, Entry> _items = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inflight = new();

    public int MaxEntries { get; set; } = 500;

    public int Count => _items.Count;

    public async Task<T> GetOrAddAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
        where T : class
    {
        if (ttl > TimeSpan.Zero && _items.TryGetValue(key, out var hit) && hit.Expires > DateTimeOffset.UtcNow)
        {
            return (T)hit.Value;
        }

        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => (object)await factory()));
        try
        {
            var value = (T)await lazy.Value;
            if (ttl > TimeSpan.Zero)
            {
                var now = DateTimeOffset.UtcNow;
                _items[key] = new Entry(value, now + ttl, now);
                Trim();
            }

            return value;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public void Clear() => _items.Clear();

    private void Trim()
    {
        var max = Math.Max(1, MaxEntries);
        if (_items.Count <= max)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _items.Where(k => k.Value.Expires <= now).ToList())
        {
            _items.TryRemove(kv.Key, out _);
        }

        if (_items.Count <= max)
        {
            return;
        }

        foreach (var kv in _items.OrderBy(k => k.Value.Created).Take(_items.Count - max).ToList())
        {
            _items.TryRemove(kv.Key, out _);
        }
    }
}
