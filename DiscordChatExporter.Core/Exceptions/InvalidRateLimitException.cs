using System;

namespace DiscordChatExporter.Core.Exceptions;

internal sealed class InvalidRateLimitException()
    : Exception("Discord returned an unusable rate-limit duration.");
