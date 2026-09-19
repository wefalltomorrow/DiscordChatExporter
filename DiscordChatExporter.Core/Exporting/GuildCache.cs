using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Utils;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

// Holds the channel, role and member data of a single guild for the duration of an export run.
// This used to live in ExportContext, which is constructed per channel, so every channel of the
// same guild re-fetched the guild's channels and roles and re-resolved every member it referenced.
internal class GuildCache(DiscordClient discord, Snowflake guildId, MemberCache? memberCache)
{
    private readonly AsyncCache<Snowflake, Member?> _members = new();
    private readonly AsyncCache<Snowflake, Channel?> _channels = new();
    private readonly ConcurrentDictionary<Snowflake, Role> _rolesById = new();

    private Lazy<Task>? _initialization;
    private readonly object _initializationLock = new();

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await foreach (var channel in discord.GetGuildChannelsAsync(guildId, cancellationToken))
            _channels.Set(channel.Id, channel);

        await foreach (var role in discord.GetGuildRolesAsync(guildId, cancellationToken))
            _rolesById[role.Id] = role;
    }

    // Idempotent: the bulk fetch runs once per guild per run, no matter how many channels ask.
    public async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        Lazy<Task> initialization;
        lock (_initializationLock)
        {
            initialization = _initialization ??= new Lazy<Task>(() =>
                InitializeAsync(cancellationToken)
            );
        }

        try
        {
            await initialization.Value;
        }
        catch
        {
            // Without this, a single failed request would leave every channel of this guild unable
            // to resolve anything, whereas previously each channel retried independently.
            lock (_initializationLock)
            {
                if (_initialization == initialization)
                    _initialization = null;
            }

            throw;
        }
    }

    // Threads are not preloaded, so we resolve them on demand
    public async ValueTask PopulateChannelAsync(
        Snowflake id,
        CancellationToken cancellationToken = default
    ) =>
        // Store the result even if it's null, to avoid re-fetching non-existing channels
        await _channels.GetOrAddAsync(
            id,
            async k => await discord.TryGetChannelAsync(k, cancellationToken)
        );

    // Because members cannot be pulled in bulk, we need to populate them on demand
    public async ValueTask PopulateMemberAsync(
        Snowflake id,
        User? fallbackUser,
        CancellationToken cancellationToken = default
    ) =>
        // Store the result even if it's null, to avoid re-fetching non-existing members
        await _members.GetOrAddAsync(
            id,
            async k =>
            {
                // The disk cache stores the raw payload of the member lookup, including the fact
                // that it found nothing. Either way it saves the request, which is the expensive
                // part; the fallback below then runs exactly as it would have.
                JsonElement? memberJson;
                if (memberCache is not null && memberCache.TryGet(guildId, k, out var cachedJson))
                {
                    memberJson = cachedJson;
                }
                else
                {
                    memberJson = await discord.TryGetGuildMemberJsonAsync(
                        guildId,
                        k,
                        cancellationToken
                    );

                    memberCache?.Set(guildId, k, memberJson);
                }

                var member = memberJson?.Pipe(j => Member.Parse(j, guildId));

                // User may have left the guild since they were mentioned.
                // Create a dummy member object based on the user info.
                if (member is null)
                {
                    var user = fallbackUser ?? await discord.TryGetUserAsync(k, cancellationToken);

                    // User may have been deleted since they were mentioned
                    if (user is not null)
                        member = Member.CreateFallback(user);
                }

                return member;
            }
        );

    public Member? TryGetMember(Snowflake id) =>
        _members.TryGetCompleted(id, out var member) ? member : null;

    public Channel? TryGetChannel(Snowflake id) =>
        _channels.TryGetCompleted(id, out var channel) ? channel : null;

    public Role? TryGetRole(Snowflake id) => _rolesById.TryGetValue(id, out var role) ? role : null;
}
