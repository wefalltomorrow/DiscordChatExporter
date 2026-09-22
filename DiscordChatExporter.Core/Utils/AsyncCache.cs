using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Utils;

// Collapses concurrent lookups for the same key into one asynchronous operation.
// Failed/cancelled lookups are evicted so a transient error cannot poison a key for the run.
internal sealed class AsyncCache<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Lazy<Task<TValue>>> _entries = new();

    public bool TryGetCompleted(TKey key, out TValue? value)
    {
        if (
            _entries.TryGetValue(key, out var entry)
            && entry.IsValueCreated
            && entry.Value.IsCompletedSuccessfully
        )
        {
            value = entry.Value.Result;
            return true;
        }

        value = default;
        return false;
    }

    public async ValueTask<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> factory)
    {
        var entry = _entries.GetOrAdd(
            key,
            k => new Lazy<Task<TValue>>(
                () => factory(k),
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication
            )
        );

        try
        {
            return await entry.Value;
        }
        catch
        {
            _entries.TryRemove(new KeyValuePair<TKey, Lazy<Task<TValue>>>(key, entry));
            throw;
        }
    }

    public void Set(TKey key, TValue value) =>
        _entries[key] = new Lazy<Task<TValue>>(() => Task.FromResult(value));
}
