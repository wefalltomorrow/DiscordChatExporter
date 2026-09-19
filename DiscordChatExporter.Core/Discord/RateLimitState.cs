using System;

namespace DiscordChatExporter.Core.Discord;

public readonly record struct RateLimitState(bool IsPaused, TimeSpan Remaining);
