using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Utils;

namespace DiscordChatExporter.Core.Exporting;

// Metadata shared by every channel exported through the same ChannelExporter.
// This avoids re-fetching identical guild/member/channel/role data for each channel.
internal sealed class ExportCache(DiscordClient discord)
{
    private readonly AsyncCache<Snowflake, IReadOnlyList<Channel>> _guildChannels = new();
    private readonly AsyncCache<Snowflake, IReadOnlyList<Role>> _guildRoles = new();
    private readonly AsyncCache<Snowflake, Channel?> _channels = new();
    private readonly AsyncCache<(Snowflake GuildId, Snowflake UserId), Member?> _members = new();
    private long _hitCount;

    public long HitCount => Interlocked.Read(ref _hitCount);

    private async ValueTask<TValue> GetOrAddAsync<TKey, TValue>(
        AsyncCache<TKey, TValue> cache,
        TKey key,
        System.Func<TKey, Task<TValue>> factory
    )
        where TKey : notnull
    {
        if (cache.TryGetCompleted(key, out var cached))
        {
            Interlocked.Increment(ref _hitCount);
            return cached!;
        }

        return await cache.GetOrAddAsync(key, factory);
    }

    public async ValueTask<IReadOnlyList<Channel>> GetGuildChannelsAsync(
        Snowflake guildId,
        CancellationToken cancellationToken = default
    ) =>
        await GetOrAddAsync(
            _guildChannels,
            guildId,
            async id =>
            {
                var channels = new List<Channel>();
                await foreach (var channel in discord.GetGuildChannelsAsync(id, cancellationToken))
                {
                    channels.Add(channel);
                    _channels.Set(channel.Id, channel);
                }

                return channels;
            }
        );

    public async ValueTask<IReadOnlyList<Role>> GetGuildRolesAsync(
        Snowflake guildId,
        CancellationToken cancellationToken = default
    ) =>
        await GetOrAddAsync(
            _guildRoles,
            guildId,
            async id =>
            {
                var roles = new List<Role>();
                await foreach (var role in discord.GetGuildRolesAsync(id, cancellationToken))
                    roles.Add(role);

                return roles;
            }
        );

    public async ValueTask<Channel?> GetChannelAsync(
        Snowflake channelId,
        CancellationToken cancellationToken = default
    ) =>
        await GetOrAddAsync(
            _channels,
            channelId,
            async id => await discord.TryGetChannelAsync(id, cancellationToken)
        );

    public async ValueTask<Member?> GetMemberAsync(
        Snowflake guildId,
        Snowflake userId,
        User? fallbackUser,
        CancellationToken cancellationToken = default
    ) =>
        await GetOrAddAsync(
            _members,
            (guildId, userId),
            async key =>
            {
                var member = await discord.TryGetGuildMemberAsync(
                    key.GuildId,
                    key.UserId,
                    cancellationToken
                );

                if (member is not null)
                    return member;

                var user = fallbackUser
                    ?? await discord.TryGetUserAsync(key.UserId, cancellationToken);

                return user is not null ? Member.CreateFallback(user) : null;
            }
        );
}
