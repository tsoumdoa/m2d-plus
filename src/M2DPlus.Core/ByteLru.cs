namespace M2DPlus.Core;

/// <summary>Byte-weighted retention policy. Retirement callbacks drop cache owners, never live leases.</summary>
public sealed class ByteLru<TKey> : IDisposable where TKey : notnull
{
    private sealed record Entry(TKey Key, long Bytes, Action Retire);
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _age = new();
    public long BudgetBytes { get; }
    public long RetainedBytes { get; private set; }
    public int Count => _entries.Count;
    // The containing cache serializes this policy with acquisition and its dictionaries.
    public ByteLru(long budgetBytes) => BudgetBytes = budgetBytes > 0 ? budgetBytes : throw new ArgumentOutOfRangeException(nameof(budgetBytes));
    public void Touch(TKey key)
    {
        if (!_entries.TryGetValue(key, out var node)) return;
        _age.Remove(node); _age.AddLast(node);
    }
    public void Add(TKey key, long bytes, Action retire)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (_entries.ContainsKey(key)) throw new ArgumentException("Duplicate cache key", nameof(key));
        if (bytes > BudgetBytes) { retire(); return; }
        while (RetainedBytes > BudgetBytes - bytes) RemoveOldest();
        var node = _age.AddLast(new Entry(key, bytes, retire));
        _entries.Add(key, node); RetainedBytes += bytes;
    }
    private void RemoveOldest()
    {
        var entry = _age.First!.Value; _age.RemoveFirst(); _entries.Remove(entry.Key);
        RetainedBytes -= entry.Bytes; entry.Retire();
    }
    public void Dispose() { while (_age.Count != 0) RemoveOldest(); }
}
