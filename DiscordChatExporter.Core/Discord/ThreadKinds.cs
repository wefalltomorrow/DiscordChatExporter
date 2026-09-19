using System;

namespace DiscordChatExporter.Core.Discord;

// Which threads to pull. Active and archived threads come from two different endpoints, so this
// is a bit field rather than a scale: asking for only one of them skips the other's requests
// entirely, instead of fetching both and filtering afterwards.
[Flags]
public enum ThreadKinds
{
    None = 0,
    Active = 0b1,
    Archived = 0b10,
    All = Active | Archived,
}

public static class ThreadKindsExtensions
{
    extension(ThreadKinds threadKinds)
    {
        internal bool Includes(ThreadKinds other) => (threadKinds & other) != 0;

        public string GetDisplayName() =>
            threadKinds switch
            {
                ThreadKinds.None => "None",
                ThreadKinds.Active => "Active",
                ThreadKinds.Archived => "Archived",
                ThreadKinds.All => "Active and archived",
                _ => throw new ArgumentOutOfRangeException(nameof(threadKinds)),
            };
    }
}
