namespace ScreenTranslator.App.Core;

internal sealed class BoundedLruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _entries = [];
    private readonly LinkedList<CacheEntry> _recency = [];
    private readonly object _gate = new();

    public BoundedLruCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                value = default!;
                return false;
            }

            _recency.Remove(node);
            _recency.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existingNode))
            {
                existingNode.Value = new CacheEntry(key, value);
                _recency.Remove(existingNode);
                _recency.AddFirst(existingNode);
                return;
            }

            var node = _recency.AddFirst(new CacheEntry(key, value));
            _entries[key] = node;

            if (_entries.Count <= _capacity)
            {
                return;
            }

            var leastRecent = _recency.Last!;
            _recency.RemoveLast();
            _entries.Remove(leastRecent.Value.Key);
        }
    }

    private sealed record CacheEntry(TKey Key, TValue Value);
}
