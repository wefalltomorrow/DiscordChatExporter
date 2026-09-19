using System;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Cli.Commands.Shared;

public enum ThreadInclusionMode
{
    None,
    Active,
    Archived,
    All,

    // Threads and nothing else. The channels that hold them are still fetched, because that is
    // how threads are discovered in the first place, but they are dropped before the export.
    Only,
}

public static class ThreadInclusionModeExtensions
{
    extension(ThreadInclusionMode threadInclusionMode)
    {
        // Which threads to ask Discord for. 'Only' is about what ends up in the export rather
        // than about which threads to fetch, so it asks for all of them.
        public ThreadKinds ThreadKinds =>
            threadInclusionMode switch
            {
                ThreadInclusionMode.None => ThreadKinds.None,
                ThreadInclusionMode.Active => ThreadKinds.Active,
                ThreadInclusionMode.Archived => ThreadKinds.Archived,
                ThreadInclusionMode.All or ThreadInclusionMode.Only => ThreadKinds.All,
                _ => throw new ArgumentOutOfRangeException(nameof(threadInclusionMode)),
            };

        public bool IncludesParentChannels => threadInclusionMode != ThreadInclusionMode.Only;
    }
}
