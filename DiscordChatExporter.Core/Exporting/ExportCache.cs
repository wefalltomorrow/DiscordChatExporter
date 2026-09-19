using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Utils;

namespace DiscordChatExporter.Core.Exporting;

// Discord data shared by every channel exported in the same run. Scoped to a ChannelExporter,
// which both hosts already create once per export operation.
public class ExportCache(DiscordClient discord, MemberCache? memberCache = null)
{
    private readonly AsyncCache<Snowflake, Guild> _guilds = new();

    // Everything below the guild level is keyed by guild, because a user's nickname, avatar and
    // roles are per-guild. A flat member cache would corrupt exports that span several guilds.
    private readonly ConcurrentDictionary<Snowflake, GuildCache> _guildCaches = new();

    public async ValueTask<Guild> GetGuildAsync(
        Snowflake guildId,
        CancellationToken cancellationToken = default
    ) =>
        await _guilds.GetOrAddAsync(
            guildId,
            async id => await discord.GetGuildAsync(id, cancellationToken)
        );

    internal GuildCache GetGuildCache(Snowflake guildId) =>
        _guildCaches.GetOrAdd(guildId, id => new GuildCache(discord, id, memberCache));

    // True once anything in this run was served from the persisted cache
    public bool UsedPersistedData => memberCache?.WasUsed == true;

    public async ValueTask LoadPersistedDataAsync(CancellationToken cancellationToken = default)
    {
        if (memberCache is not null)
            await memberCache.LoadAsync(cancellationToken);
    }

    public async ValueTask SavePersistedDataAsync(CancellationToken cancellationToken = default)
    {
        if (memberCache is not null)
            await memberCache.SaveAsync(cancellationToken);
    }
}
