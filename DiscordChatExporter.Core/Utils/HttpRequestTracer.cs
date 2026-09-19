using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting;

namespace DiscordChatExporter.Core.Utils;

// Sits in the handler chain to count what an export actually costs over the wire.
//
// Two separate things happen here. The per-channel request and byte counters feed the progress
// display and are always live, but only while a channel export is in flight -- outside one,
// 'ExportStats.Current' is null and this is a no-op. The per-route breakdown is the older,
// heavier diagnostic and stays gated behind an environment variable.
public partial class HttpRequestTracer : DelegatingHandler
{
    private readonly ConcurrentDictionary<string, int> _countsByRoute = new(StringComparer.Ordinal);

    public HttpRequestTracer(HttpMessageHandler innerHandler)
        : base(innerHandler) { }

    public IReadOnlyList<KeyValuePair<string, int>> Counts =>
        _countsByRoute
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .ToArray();

    public int TotalCount => _countsByRoute.Values.Sum();

    // Counted separately, because the retry pipeline re-sends a throttled request and each
    // attempt shows up here. Without this, a rate-limited run looks like it made more calls.
    private int _rateLimitedCount;

    public int RateLimitedCount => Volatile.Read(ref _rateLimitedCount);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (IsEnabled && request.RequestUri is not null)
        {
            _countsByRoute.AddOrUpdate(
                NormalizeRoute(request.RequestUri),
                1,
                (_, count) => count + 1
            );
        }

        var stats = ExportStats.Current;
        stats?.ReportRequest();

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            Interlocked.Increment(ref _rateLimitedCount);

        // Both call sites request the response with 'ResponseHeadersRead', so the body hasn't
        // been consumed yet and can be wrapped to measure it as the caller reads it. Retries
        // count separately, which is correct: a retried request really is downloaded twice.
        if (stats is not null)
        {
            var content = response.Content;
            var counting = new StreamContent(
                new CountingStream(
                    await content.ReadAsStreamAsync(cancellationToken),
                    stats.ReportDownloadedBytes
                )
            );

            foreach (var (name, values) in content.Headers)
                counting.Headers.TryAddWithoutValidation(name, values);

            response.Content = counting;
        }

        return response;
    }
}

public partial class HttpRequestTracer
{
    // Gates only the per-route breakdown; the per-channel counters are always live
    public const string EnvironmentVariableName = "DISCORDCHATEXPORTER_HTTP_TRACE";

    public static bool IsEnabled { get; } =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariableName));

    // Collapses IDs into placeholders, so that (for example) every member lookup is counted
    // against a single 'guilds/{id}/members/{id}' row instead of one row per user.
    [GeneratedRegex(@"\d{17,20}")]
    private static partial Regex SnowflakeRegex { get; }

    // Reaction routes end in the emoji itself, which is neither an ID nor a fixed segment
    [GeneratedRegex(@"(?<=/reactions/).+$")]
    private static partial Regex ReactionEmojiRegex { get; }

    private static string NormalizeRoute(Uri uri)
    {
        // Query strings only carry pagination cursors and limits, which aren't part of the route
        var path = uri.AbsolutePath.TrimStart('/');

        // Strip the API version prefix to keep the output narrow
        if (path.StartsWith("api/", StringComparison.Ordinal))
            path = path["api/".Length..];
        if (path.StartsWith("v10/", StringComparison.Ordinal))
            path = path["v10/".Length..];

        return SnowflakeRegex.Replace(ReactionEmojiRegex.Replace(path, "{emoji}"), "{id}");
    }
}
