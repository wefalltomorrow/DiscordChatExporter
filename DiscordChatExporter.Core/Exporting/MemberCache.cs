using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using DiscordChatExporter.Core.Discord;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

// Persists the outcome of guild member lookups between runs of the tool, so that splitting an
// export into several invocations (by date range or by channel) doesn't re-resolve the same
// members every time.
//
// Only the member lookup is cached. Guild, channel and role data is a handful of requests per run
// and is exactly the data that changes wholesale, so it is always fetched fresh.
//
// This is advisory: any failure to read or write the cache is swallowed, because a cache must
// never be able to fail an export.
public partial class MemberCache(string filePath, TimeSpan ttl, string token)
{
    private static readonly AsyncKeyedLocker<string> Locker = new();

    // guildId -> (memberId -> entry)
    private readonly ConcurrentDictionary<
        Snowflake,
        ConcurrentDictionary<Snowflake, Entry>
    > _entriesByGuild = new();

    private readonly string _tokenHash = HashToken(token);

    private int _hitCount;

    // Recorded in the export so that an archive says whether any of it came from a cache
    public bool WasUsed => Volatile.Read(ref _hitCount) > 0;

    private ConcurrentDictionary<Snowflake, Entry> GetGuildEntries(Snowflake guildId) =>
        _entriesByGuild.GetOrAdd(guildId, _ => new ConcurrentDictionary<Snowflake, Entry>());

    internal bool TryGet(Snowflake guildId, Snowflake memberId, out JsonElement? payload)
    {
        payload = null;

        if (
            !GetGuildEntries(guildId).TryGetValue(memberId, out var entry)
            || DateTimeOffset.UtcNow - entry.FetchedAt >= ttl
        )
        {
            return false;
        }

        payload = entry.Payload;
        Interlocked.Increment(ref _hitCount);
        return true;
    }

    // A null payload records that the member lookup found nothing, which is worth remembering
    // just as much as a hit: it's the same request either way.
    internal void Set(Snowflake guildId, Snowflake memberId, JsonElement? payload) =>
        GetGuildEntries(guildId)[memberId] = new Entry(DateTimeOffset.UtcNow, payload);

    public async ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await Locker.LockAsync(filePath, cancellationToken);

            if (!File.Exists(filePath))
                return;

            await using var stream = File.OpenRead(filePath);
            using var document = await JsonDocument.ParseAsync(stream, default, cancellationToken);

            Read(document.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing, corrupt, or outdated cache is simply an empty one
            _entriesByGuild.Clear();
        }
    }

    public async ValueTask SaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await Locker.LockAsync(filePath, cancellationToken);

            // Merge with whatever another run may have written since we loaded, so that
            // concurrent invocations don't simply discard each other's work
            var merged = new Dictionary<Snowflake, Dictionary<Snowflake, Entry>>();

            try
            {
                if (File.Exists(filePath))
                {
                    await using var existingStream = File.OpenRead(filePath);
                    using var existingDocument = await JsonDocument.ParseAsync(
                        existingStream,
                        default,
                        cancellationToken
                    );

                    foreach (var (guildId, entries) in ReadEntries(existingDocument.RootElement))
                        merged[guildId] = entries;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }

            foreach (var (guildId, entries) in _entriesByGuild)
            {
                if (!merged.TryGetValue(guildId, out var mergedEntries))
                    merged[guildId] = mergedEntries = new Dictionary<Snowflake, Entry>();

                foreach (var (memberId, entry) in entries)
                {
                    // Newer wins, whichever run produced it
                    if (
                        !mergedEntries.TryGetValue(memberId, out var existing)
                        || existing.FetchedAt < entry.FetchedAt
                    )
                    {
                        mergedEntries[memberId] = entry;
                    }
                }
            }

            var directoryPath = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
                Directory.CreateDirectory(directoryPath);

            // Write to a temporary file first and move it into place, so that a reader never
            // observes a half-written cache
            var tempFilePath = $"{filePath}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";

            try
            {
                await using (var stream = File.Create(tempFilePath))
                {
                    await using var writer = new Utf8JsonWriter(
                        stream,
                        new JsonWriterOptions
                        {
                            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                            Indented = false,
                            SkipValidation = true,
                        }
                    );

                    Write(writer, merged);
                    await writer.FlushAsync(cancellationToken);
                }

                File.Move(tempFilePath, filePath, true);
            }
            catch
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }

                throw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Failing to persist the cache must not fail the export
        }
    }
}

public partial class MemberCache
{
    private const int FormatVersion = 1;

    // How long an entry is kept on disk, as opposed to how long it is trusted. These are
    // deliberately separate: the TTL is a per-run judgement about staleness, so pruning by it
    // would let a run with a short TTL throw away entries that a later run would have accepted
    // (and '--cache-ttl 0', which exists to force a refresh, would empty the file entirely).
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);

    private readonly record struct Entry(DateTimeOffset FetchedAt, JsonElement? Payload);

    // Entries reflect what a particular token can see: a member that is invisible to one account
    // may be perfectly visible to another, so they must not be shared between tokens.
    private static string HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token)).Pipe(Convert.ToHexStringLower).Truncate(16);

    private void Read(JsonElement root)
    {
        foreach (var (guildId, entries) in ReadEntries(root))
        {
            var guildEntries = GetGuildEntries(guildId);
            foreach (var (memberId, entry) in entries)
                guildEntries[memberId] = entry;
        }
    }

    private IEnumerable<KeyValuePair<Snowflake, Dictionary<Snowflake, Entry>>> ReadEntries(
        JsonElement root
    )
    {
        if (root.GetPropertyOrNull("version")?.GetInt32OrNull() != FormatVersion)
            yield break;

        if (root.GetPropertyOrNull("guilds") is not { } guilds)
            yield break;

        foreach (var guildProperty in guilds.EnumerateObject())
        {
            if (Snowflake.TryParse(guildProperty.Name) is not { } guildId)
                continue;

            // A guild section written by a different account describes a different view of that
            // guild, so it is dropped rather than trusted
            if (guildProperty.Value.GetPropertyOrNull("tokenHash")?.GetStringOrNull() != _tokenHash)
            {
                continue;
            }

            if (guildProperty.Value.GetPropertyOrNull("members") is not { } members)
                continue;

            var entries = new Dictionary<Snowflake, Entry>();

            foreach (var memberProperty in members.EnumerateObject())
            {
                if (Snowflake.TryParse(memberProperty.Name) is not { } memberId)
                    continue;

                if (
                    memberProperty.Value.GetPropertyOrNull("fetchedAt")?.GetDateTimeOffsetOrNull()
                    is not { } fetchedAt
                )
                {
                    continue;
                }

                entries[memberId] = new Entry(
                    fetchedAt,
                    memberProperty.Value.GetPropertyOrNull("payload")?.Clone()
                );
            }

            yield return new KeyValuePair<Snowflake, Dictionary<Snowflake, Entry>>(
                guildId,
                entries
            );
        }
    }

    private void Write(
        Utf8JsonWriter writer,
        Dictionary<Snowflake, Dictionary<Snowflake, Entry>> entriesByGuild
    )
    {
        var threshold = DateTimeOffset.UtcNow - RetentionPeriod;

        writer.WriteStartObject();
        writer.WriteNumber("version", FormatVersion);
        writer.WriteStartObject("guilds");

        foreach (var (guildId, entries) in entriesByGuild)
        {
            writer.WriteStartObject(guildId.ToString());
            writer.WriteString("tokenHash", _tokenHash);
            writer.WriteStartObject("members");

            foreach (var (memberId, entry) in entries)
            {
                // Long-stale entries are dropped rather than carried forward forever
                if (entry.FetchedAt < threshold)
                    continue;

                writer.WriteStartObject(memberId.ToString());
                writer.WriteString("fetchedAt", entry.FetchedAt);

                if (entry.Payload is { } payload)
                {
                    writer.WritePropertyName("payload");
                    payload.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
