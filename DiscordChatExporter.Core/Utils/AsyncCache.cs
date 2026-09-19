using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Utils;

// Memoizes the result of an asynchronous lookup, collapsing concurrent requests for the same key
// into a single execution of the factory. Channels are exported in parallel, so without this two
// channels reaching the same user at the same moment would each issue their own request.
internal class AsyncCache<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Lazy<Task<TValue>>> _entries = new();

    // A plain ConcurrentDictionary<TKey, Task<TValue>> is not enough here: GetOrAdd may invoke its
    // value factory more than once under contention, and the losing invocation would still have
    // started a request whose result is then discarded. Lazy<T> defaults to
    // LazyThreadSafetyMode.ExecutionAndPublication, which guarantees a single execution.
    public async ValueTask<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> factory)
    {
        var entry = _entries.GetOrAdd(key, k => new Lazy<Task<TValue>>(() => factory(k)));

        try
        {
            return await entry.Value;
        }
        catch
        {
            // Don't let a transient failure (or a cancellation) poison the key for the rest of the
            // run. The pair-based overload ensures we only remove the entry we actually observed,
            // in case another caller has already replaced it.
            _entries.TryRemove(new KeyValuePair<TKey, Lazy<Task<TValue>>>(key, entry));
            throw;
        }
    }

    // Non-blocking read, for the synchronous call sites (the Razor templates and the JSON writer
    // both reach for already-populated data). An entry that is still in flight, or that faulted,
    // is reported as absent rather than awaited, preserving the previous semantics where a lookup
    // for something not yet populated simply returned null.
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

    public void Set(TKey key, TValue value) =>
        _entries[key] = new Lazy<Task<TValue>>(Task.FromResult(value));
}
