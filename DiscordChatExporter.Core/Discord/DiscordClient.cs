using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting.Progress;
using DiscordChatExporter.Core.Utils;
using Gress;
using HttpCloak;
using JsonExtensions.Http;
using JsonExtensions.Reading;
using Polly;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord;

public class DiscordClient(
    string token,
    RateLimitPreference rateLimitPreference = RateLimitPreference.RespectAll
) : IDisposable
{
    private const int JsonParseRetryAttempts = 5;

    internal const int InvalidRequestCircuitBreakerThreshold = 100;
    internal const int UserRequestConcurrencyLimit = 2;
    internal const int BotRequestConcurrencyLimit = 16;
    internal static readonly TimeSpan UserRequestStartInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan InvalidRequestCircuitBreakerWindow = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan AdaptiveRateLimitWindow = TimeSpan.FromMinutes(10);

    private const string DisableBrowserTransportEnvironmentVariable =
        "DISCORDCHATEXPORTER_DISABLE_BROWSER_TRANSPORT";

    // HttpCloak's in-process .NET native binding currently has an unresolved
    // host-process crash on Linux. Keep the browser-fingerprint transport on
    // Windows, where that issue is not reproduced, and retain HttpClient
    // elsewhere for process stability. The environment override gives users
    // a recovery switch if the native transport causes trouble on a machine.
    internal static bool ShouldUseBrowserTransport(string? disabledValue, bool isWindows) =>
        isWindows
        && !string.Equals(disabledValue, "1", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(disabledValue, "true", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(disabledValue, "yes", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(disabledValue, "on", StringComparison.OrdinalIgnoreCase);

    internal static bool IsBrowserTransportSupported =>
        ShouldUseBrowserTransport(
            Environment.GetEnvironmentVariable(DisableBrowserTransportEnvironmentVariable),
            OperatingSystem.IsWindows()
        );

    private static Session? TryCreateBrowserSession()
    {
        if (!IsBrowserTransportSupported)
            return null;

        try
        {
            return new Session(preset: Presets.Chrome150Windows, retry: 0);
        }
        catch
        {
            // Browser impersonation is an enhancement, not a reason to make the
            // exporter unusable. Fall back to managed HttpClient if the native
            // transport cannot initialize on this system.
            return null;
        }
    }

    private readonly Uri _baseUri = new("https://discord.com/api/v10/", UriKind.Absolute);
    private readonly HttpClient _httpClient = Http.Client;
    private readonly Session? _session = TryCreateBrowserSession();
    private readonly DiscordUserClientProfile _userClientProfile = new();
    private readonly Func<TimeSpan, CancellationToken, ValueTask> _delayAsync = static (
        delay,
        cancellationToken
    ) => new ValueTask(Task.Delay(delay, cancellationToken));
    private readonly bool _useBrowserTransport = IsBrowserTransportSupported;
    private readonly bool _refreshUserClientBuildNumber = true;
    private readonly object _rateLimitSync = new();
    private readonly object _invalidRequestSync = new();
    private readonly object _hardRateLimitSync = new();
    private readonly Queue<DateTimeOffset> _invalidRequestTimes = new();
    private readonly Queue<DateTimeOffset> _hardRateLimitTimes = new();
    private readonly ConcurrentDictionary<
        (TokenKind Kind, string Url),
        HttpStatusCode
    > _knownUnavailableRequests = new();
    private readonly SemaphoreSlim _userRequestGate = new(
        UserRequestConcurrencyLimit,
        UserRequestConcurrencyLimit
    );
    private readonly SemaphoreSlim _botRequestGate = new(
        BotRequestConcurrencyLimit,
        BotRequestConcurrencyLimit
    );
    private readonly SemaphoreSlim _userRequestStartGate = new(1, 1);
    private TimeSpan _userRequestStartInterval = UserRequestStartInterval;
    private DateTimeOffset _lastUserRequestStartedUtc;
    private DateTimeOffset _sharedRateLimitUntilUtc;
    private long _apiRequestCount;
    private long _avoidedUnavailableRequestCount;
    private long _hardRateLimitCount;
    private long _advisoryPauseCount;
    private TokenKind? _resolvedTokenKind;

    public event EventHandler<RateLimitState>? RateLimitChanged;

    internal DiscordClient(
        string tokenOverride,
        RateLimitPreference rateLimitPreferenceOverride,
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, ValueTask>? delayAsync = null
    )
        : this(tokenOverride, rateLimitPreferenceOverride)
    {
        _httpClient = httpClient;
        _session?.Dispose();
        _session = null;
        _useBrowserTransport = false;
        _refreshUserClientBuildNumber = false;
        _userRequestStartInterval = TimeSpan.Zero;

        if (delayAsync is not null)
            _delayAsync = delayAsync;
    }

    private static HttpResponseMessage ToHttpResponseMessage(Response source, Uri requestUri)
    {
        var response = new HttpResponseMessage((HttpStatusCode)source.StatusCode)
        {
            Content = new ByteArrayContent(source.Content),
            ReasonPhrase = source.Reason,
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri),
        };

        foreach (var (name, values) in source.Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, values))
                response.Content.Headers.TryAddWithoutValidation(name, values);
        }

        return response;
    }

    private (DateTimeOffset PauseUntil, TimeSpan Delay) ExtendSharedRateLimit(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
            return (default, TimeSpan.Zero);

        var now = DateTimeOffset.UtcNow;
        var requestedPauseUntil = now + delay;

        lock (_rateLimitSync)
        {
            if (requestedPauseUntil >= _sharedRateLimitUntilUtc)
            {
                _sharedRateLimitUntilUtc = requestedPauseUntil;
                return (_sharedRateLimitUntilUtc, delay);
            }

            return (_sharedRateLimitUntilUtc, _sharedRateLimitUntilUtc - now);
        }
    }

    private void ClearSharedRateLimit(DateTimeOffset observedPauseUntil)
    {
        if (observedPauseUntil == default)
            return;

        lock (_rateLimitSync)
        {
            if (_sharedRateLimitUntilUtc == observedPauseUntil)
                _sharedRateLimitUntilUtc = default;
        }
    }

    private async ValueTask WaitForSharedRateLimitAsync(
        CancellationToken cancellationToken = default
    )
    {
        DateTimeOffset observedPauseUntil;
        TimeSpan delay;

        lock (_rateLimitSync)
        {
            observedPauseUntil = _sharedRateLimitUntilUtc;
            delay = observedPauseUntil - DateTimeOffset.UtcNow;
        }

        if (delay <= TimeSpan.Zero)
            return;

        var completed = false;
        RateLimitChanged?.Invoke(this, new RateLimitState(true, delay));
        try
        {
            await _delayAsync(delay, cancellationToken);
            completed = true;
        }
        finally
        {
            if (completed)
                ClearSharedRateLimit(observedPauseUntil);

            RateLimitChanged?.Invoke(this, new RateLimitState(false, TimeSpan.Zero));
        }
    }

    private async ValueTask WaitForRateLimitAsync(
        TimeSpan delay,
        CancellationToken cancellationToken
    )
    {
        var (pauseUntil, effectiveDelay) = ExtendSharedRateLimit(delay);
        if (effectiveDelay <= TimeSpan.Zero)
            return;

        var completed = false;
        RateLimitChanged?.Invoke(this, new RateLimitState(true, effectiveDelay));
        try
        {
            await _delayAsync(effectiveDelay, cancellationToken);
            completed = true;
        }
        finally
        {
            if (completed)
                ClearSharedRateLimit(pauseUntil);

            RateLimitChanged?.Invoke(this, new RateLimitState(false, TimeSpan.Zero));
        }
    }

    internal static TimeSpan GetAdaptiveHardRateLimitCushion(int recentRateLimitCount) =>
        recentRateLimitCount switch
        {
            <= 1 => TimeSpan.Zero,
            2 => TimeSpan.FromSeconds(2),
            3 => TimeSpan.FromSeconds(5),
            _ => TimeSpan.FromSeconds(15),
        };

    private TimeSpan RecordHardRateLimit()
    {
        var now = DateTimeOffset.UtcNow;
        int recentCount;

        lock (_hardRateLimitSync)
        {
            while (
                _hardRateLimitTimes.TryPeek(out var oldest)
                && now - oldest > AdaptiveRateLimitWindow
            )
            {
                _hardRateLimitTimes.Dequeue();
            }

            _hardRateLimitTimes.Enqueue(now);
            recentCount = _hardRateLimitTimes.Count;
        }

        Interlocked.Increment(ref _hardRateLimitCount);
        return GetAdaptiveHardRateLimitCushion(recentCount);
    }

    private async ValueTask WaitForHardRateLimitAsync(
        TimeSpan delay,
        CancellationToken cancellationToken
    )
    {
        var cushion = RecordHardRateLimit();
        await WaitForRateLimitAsync(delay + cushion, cancellationToken);
    }

    private async ValueTask WaitForUserRequestStartAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (_userRequestStartInterval <= TimeSpan.Zero)
            return;

        await _userRequestStartGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var remaining = _lastUserRequestStartedUtc + _userRequestStartInterval - now;

            if (remaining > TimeSpan.Zero)
                await _delayAsync(remaining, cancellationToken);

            _lastUserRequestStartedUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _userRequestStartGate.Release();
        }
    }

    private HttpResponseMessage CreateCachedUnavailableResponse(
        string url,
        HttpStatusCode statusCode
    ) =>
        new(statusCode)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, url)),
            ReasonPhrase = "Cached unavailable response",
        };

    public DiscordRequestStats GetRequestStats() =>
        new(
            Interlocked.Read(ref _apiRequestCount),
            Interlocked.Read(ref _avoidedUnavailableRequestCount),
            Interlocked.Read(ref _hardRateLimitCount),
            Interlocked.Read(ref _advisoryPauseCount)
        );

    private void RecordInvalidRequestOrThrow(HttpResponseMessage response)
    {
        if (
            response.StatusCode
            is not (
                HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.TooManyRequests
            )
        )
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        int recentInvalidRequestCount;

        lock (_invalidRequestSync)
        {
            while (
                _invalidRequestTimes.TryPeek(out var oldest)
                && now - oldest > InvalidRequestCircuitBreakerWindow
            )
            {
                _invalidRequestTimes.Dequeue();
            }

            _invalidRequestTimes.Enqueue(now);
            recentInvalidRequestCount = _invalidRequestTimes.Count;
        }

        if (recentInvalidRequestCount < InvalidRequestCircuitBreakerThreshold)
            return;

        response.Dispose();
        throw new DiscordChatExporterException(
            $"Safety circuit breaker stopped Discord requests after {recentInvalidRequestCount} "
                + $"HTTP 401/403/429 responses within {InvalidRequestCircuitBreakerWindow.TotalMinutes:0} minutes. "
                + "Check the token, channel permissions, and rate-limit state before resuming.",
            true
        );
    }

    private async ValueTask<HttpResponseMessage> GetResponseAsync(
        string url,
        TokenKind tokenKind,
        CancellationToken cancellationToken = default
    )
    {
        if (_knownUnavailableRequests.TryGetValue((tokenKind, url), out var cachedStatusCode))
        {
            Interlocked.Increment(ref _avoidedUnavailableRequestCount);
            return CreateCachedUnavailableResponse(url, cachedStatusCode);
        }

        var resilienceContext = ResilienceContextPool.Shared.Get(cancellationToken);
        resilienceContext.Properties.Set(Http.RateLimitDelayHandlerKey, WaitForHardRateLimitAsync);

        try
        {
            return await Http.ResponseResiliencePipeline.ExecuteAsync(
                async innerContext =>
                {
                    await WaitForSharedRateLimitAsync(innerContext.CancellationToken);

                    var requestUri = new Uri(_baseUri, url);
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Authorization"] = tokenKind == TokenKind.Bot ? $"Bot {token}" : token,
                    };

                    if (tokenKind == TokenKind.User)
                    {
                        await _userClientProfile.AddHeadersAsync(
                            headers,
                            innerContext.CancellationToken,
                            _refreshUserClientBuildNumber
                        );
                    }

                    var requestGate =
                        tokenKind == TokenKind.User ? _userRequestGate : _botRequestGate;

                    await requestGate.WaitAsync(innerContext.CancellationToken);
                    HttpResponseMessage response;
                    try
                    {
                        if (tokenKind == TokenKind.User)
                            await WaitForUserRequestStartAsync(innerContext.CancellationToken);

                        Interlocked.Increment(ref _apiRequestCount);

                        if (
                            tokenKind == TokenKind.User
                            && _useBrowserTransport
                            && _session is not null
                        )
                        {
                            var cloakResponse = await _session.GetAsync(
                                requestUri.ToString(),
                                headers: headers,
                                cancellationToken: innerContext.CancellationToken
                            );

                            response = ToHttpResponseMessage(cloakResponse, requestUri);
                        }
                        else
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                            foreach (var (name, value) in headers)
                                request.Headers.TryAddWithoutValidation(name, value);

                            response = await _httpClient.SendAsync(
                                request,
                                HttpCompletionOption.ResponseHeadersRead,
                                innerContext.CancellationToken
                            );
                        }
                    }
                    finally
                    {
                        requestGate.Release();
                    }

                    RecordInvalidRequestOrThrow(response);

                    if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                    {
                        _knownUnavailableRequests.TryAdd((tokenKind, url), response.StatusCode);
                    }

                    // Discord has advisory rate limits (communicated via response headers), but
                    // they are typically stricter than the actual server-enforced limits.
                    if (
                        response.StatusCode != HttpStatusCode.TooManyRequests
                        && rateLimitPreference.IsRespectedFor(tokenKind)
                    )
                    {
                        var remainingRequestCount = response
                            .Headers.TryGetValue("X-RateLimit-Remaining")
                            ?.Pipe(v => int.ParseOrNull(v, CultureInfo.InvariantCulture));

                        var resetAfterDelay = response
                            .Headers.TryGetValue("X-RateLimit-Reset-After")
                            ?.Pipe(v => double.ParseOrNull(v, CultureInfo.InvariantCulture))
                            ?.Pipe(TimeSpan.FromSeconds);

                        if (remainingRequestCount <= 0 && resetAfterDelay is not null)
                        {
                            Interlocked.Increment(ref _advisoryPauseCount);

                            var delay = (resetAfterDelay.Value + TimeSpan.FromSeconds(1)).Clamp(
                                TimeSpan.Zero,
                                TimeSpan.FromSeconds(60)
                            );

                            try
                            {
                                await response.Content.LoadIntoBufferAsync(
                                    innerContext.CancellationToken
                                );
                                await WaitForRateLimitAsync(delay, innerContext.CancellationToken);
                            }
                            catch
                            {
                                response.Dispose();
                                throw;
                            }
                        }
                    }

                    return response;
                },
                resilienceContext
            );
        }
        finally
        {
            ResilienceContextPool.Shared.Return(resilienceContext);
        }
    }

    private async ValueTask<TokenKind> ResolveTokenKindAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (_resolvedTokenKind is not null)
            return _resolvedTokenKind.Value;

        // Try authenticating as a user
        using var userResponse = await GetResponseAsync(
            "users/@me",
            TokenKind.User,
            cancellationToken
        );

        if (userResponse.IsSuccessStatusCode)
            return (_resolvedTokenKind = TokenKind.User).Value;

        if (userResponse.StatusCode != HttpStatusCode.Unauthorized)
            throw new DiscordChatExporterException(
                $"Token probe failed: {userResponse.StatusCode.ToString().SeparateWords(' ').ToLowerInvariant()}."
            );

        // Try authenticating as a bot
        using var botResponse = await GetResponseAsync(
            "users/@me",
            TokenKind.Bot,
            cancellationToken
        );

        if (botResponse.IsSuccessStatusCode)
            return (_resolvedTokenKind = TokenKind.Bot).Value;

        if (botResponse.StatusCode != HttpStatusCode.Unauthorized)
            throw new DiscordChatExporterException(
                $"Token probe failed: {botResponse.StatusCode.ToString().SeparateWords(' ').ToLowerInvariant()}."
            );

        throw new DiscordChatExporterException("Authentication token is invalid.", true);
    }

    private async ValueTask<HttpResponseMessage> GetResponseAsync(
        string url,
        CancellationToken cancellationToken = default
    ) =>
        await GetResponseAsync(
            url,
            await ResolveTokenKindAsync(cancellationToken),
            cancellationToken
        );

    private static bool IsJsonParseFailure(Exception exception) =>
        exception is JsonException || exception.GetType().Name == "JsonReaderException";

    private static TimeSpan GetJsonParseRetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, Math.Max(0, attempt - 1))));

    private async ValueTask<JsonElement> GetJsonResponseAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        Exception? lastJsonException = null;

        for (var attempt = 1; attempt <= JsonParseRetryAttempts; attempt++)
        {
            using var response = await GetResponseAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw await CreateFailedResponseExceptionAsync(url, response, cancellationToken);

            try
            {
                return await response.Content.ReadAsJsonAsync(cancellationToken);
            }
            catch (Exception ex) when (IsJsonParseFailure(ex))
            {
                lastJsonException = ex;

                if (attempt >= JsonParseRetryAttempts)
                    break;

                await _delayAsync(GetJsonParseRetryDelay(attempt), cancellationToken);
            }
        }

        throw new DiscordChatExporterException(
            $"Discord returned malformed or truncated JSON for '{url}' after {JsonParseRetryAttempts} attempts.",
            false,
            lastJsonException
        );
    }

    private static async ValueTask<DiscordChatExporterException> CreateFailedResponseExceptionAsync(
        string url,
        HttpResponseMessage response,
        CancellationToken cancellationToken = default
    ) =>
        response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new DiscordChatExporterException(
                "Authentication token is invalid.",
                true
            ),

            HttpStatusCode.Forbidden => new DiscordChatExporterException(
                $"Request to '{url}' failed: forbidden."
            ),

            HttpStatusCode.NotFound => new DiscordChatExporterException(
                $"Request to '{url}' failed: not found."
            ),

            _ => new DiscordChatExporterException(
                $"""
                Request to '{url}' failed: {response
                    .StatusCode.ToString()
                    .SeparateWords(' ')
                    .ToLowerInvariant()}.
                Response content: {await response.Content.ReadAsStringAsync(cancellationToken)}
                """,
                true
            ),
        };

    private async ValueTask<JsonElement?> TryGetJsonResponseAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        Exception? lastJsonException = null;

        for (var attempt = 1; attempt <= JsonParseRetryAttempts; attempt++)
        {
            using var response = await GetResponseAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                throw await CreateFailedResponseExceptionAsync(url, response, cancellationToken);
            }

            try
            {
                return await response.Content.ReadAsJsonAsync(cancellationToken);
            }
            catch (Exception ex) when (IsJsonParseFailure(ex))
            {
                lastJsonException = ex;

                if (attempt >= JsonParseRetryAttempts)
                    break;

                await _delayAsync(GetJsonParseRetryDelay(attempt), cancellationToken);
            }
        }

        throw new DiscordChatExporterException(
            $"Discord returned malformed or truncated JSON for '{url}' after {JsonParseRetryAttempts} attempts.",
            false,
            lastJsonException
        );
    }

    private async ValueTask<(
        HttpStatusCode StatusCode,
        JsonElement? Json
    )> TryGetJsonResponseWithStatusAsync(string url, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= JsonParseRetryAttempts; attempt++)
        {
            using var response = await GetResponseAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (response.StatusCode, null);

            try
            {
                return (
                    response.StatusCode,
                    await response.Content.ReadAsJsonAsync(cancellationToken)
                );
            }
            catch (Exception ex) when (IsJsonParseFailure(ex))
            {
                if (attempt >= JsonParseRetryAttempts)
                    return (response.StatusCode, null);

                await _delayAsync(GetJsonParseRetryDelay(attempt), cancellationToken);
            }
        }

        return (HttpStatusCode.OK, null);
    }

    internal static long? TryParseMessageSearchTotal(
        HttpStatusCode statusCode,
        JsonElement? response
    )
    {
        if (statusCode == HttpStatusCode.Accepted || response is null)
            return null;

        if ((int)statusCode is < 200 or >= 300)
            return null;

        try
        {
            return
                response.Value.TryGetProperty("total_results", out var totalResults)
                && totalResults.ValueKind == JsonValueKind.Number
                && totalResults.TryGetInt64(out var count)
                ? count
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask<long?> CountMessagesAsync(
        Channel channel,
        Snowflake? after = null,
        Snowflake? before = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (await ResolveTokenKindAsync(cancellationToken) != TokenKind.User)
                return null;

            var url = new UrlBuilder()
                .SetPath(
                    channel.IsDirect
                        ? $"channels/{channel.Id}/messages/search"
                        : $"guilds/{channel.GuildId}/messages/search"
                )
                .SetQueryParameter("channel_id", channel.IsDirect ? null : channel.Id.ToString())
                .SetQueryParameter("min_id", (after ?? Snowflake.Zero).ToString())
                .SetQueryParameter("max_id", before?.ToString())
                .SetQueryParameter("limit", "1")
                .Build();

            var (statusCode, response) = await TryGetJsonResponseWithStatusAsync(
                url,
                cancellationToken
            );

            return TryParseMessageSearchTotal(statusCode, response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask<Application> GetApplicationAsync(
        CancellationToken cancellationToken = default
    )
    {
        var response = await GetJsonResponseAsync("applications/@me", cancellationToken);
        return Application.Parse(response);
    }

    private async ValueTask EnsureMessageContentIntentAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (await ResolveTokenKindAsync(cancellationToken) != TokenKind.Bot)
            return;

        var application = await GetApplicationAsync(cancellationToken);
        if (application.IsMessageContentIntentEnabled)
            return;

        throw new DiscordChatExporterException(
            "Provided bot account is missing the MESSAGE_CONTENT privileged intent.",
            true
        );
    }

    public async ValueTask<User?> TryGetUserAsync(
        Snowflake userId,
        CancellationToken cancellationToken = default
    )
    {
        var response = await TryGetJsonResponseAsync($"users/{userId}", cancellationToken);
        return response?.Pipe(User.Parse);
    }

    public async IAsyncEnumerable<Guild> GetUserGuildsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        yield return Guild.DirectMessages;

        var currentAfter = Snowflake.Zero;
        while (true)
        {
            var url = new UrlBuilder()
                .SetPath("users/@me/guilds")
                .SetQueryParameter("limit", "100")
                .SetQueryParameter("after", currentAfter.ToString())
                .Build();

            var response = await GetJsonResponseAsync(url, cancellationToken);

            var count = 0;
            foreach (var guildJson in response.EnumerateArray())
            {
                var guild = Guild.Parse(guildJson);
                yield return guild;

                currentAfter = guild.Id;
                count++;
            }

            if (count <= 0)
                yield break;
        }
    }

    public async ValueTask<Guild> GetGuildAsync(
        Snowflake guildId,
        CancellationToken cancellationToken = default
    )
    {
        if (guildId == Guild.DirectMessages.Id)
            return Guild.DirectMessages;

        var response = await GetJsonResponseAsync($"guilds/{guildId}", cancellationToken);
        return Guild.Parse(response);
    }

    public async IAsyncEnumerable<Channel> GetGuildChannelsAsync(
        Snowflake guildId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (guildId == Guild.DirectMessages.Id)
        {
            var response = await GetJsonResponseAsync("users/@me/channels", cancellationToken);
            foreach (var channelJson in response.EnumerateArray())
                yield return Channel.Parse(channelJson);
        }
        else
        {
            var response = await GetJsonResponseAsync(
                $"guilds/{guildId}/channels",
                cancellationToken
            );

            var channelsJson = response
                .EnumerateArray()
                .OrderBy(j => j.GetProperty("position").GetInt32())
                .ThenBy(j => j.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse))
                .ToArray();

            var parentsById = channelsJson
                .Where(j => j.GetProperty("type").GetInt32() == (int)ChannelKind.GuildCategory)
                .Select((j, i) => Channel.Parse(j, null, i + 1))
                .ToDictionary(j => j.Id);

            // Discord channel positions are relative, so we need to normalize them
            // so that the user may refer to them more easily in file name templates.
            var position = 0;

            foreach (var channelJson in channelsJson)
            {
                var parent = channelJson
                    .GetPropertyOrNull("parent_id")
                    ?.GetNonWhiteSpaceStringOrNull()
                    ?.Pipe(Snowflake.Parse)
                    .Pipe(parentsById.GetValueOrDefault);

                yield return Channel.Parse(channelJson, parent, position);
                position++;
            }
        }
    }

    public async IAsyncEnumerable<Channel> GetGuildThreadsAsync(
        Snowflake guildId,
        bool includeArchived = false,
        Snowflake? before = null,
        Snowflake? after = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (guildId == Guild.DirectMessages.Id)
            yield break;

        var channels = await GetGuildChannelsAsync(guildId, cancellationToken);

        foreach (
            var channel in await GetChannelThreadsAsync(
                channels,
                includeArchived,
                before,
                after,
                cancellationToken
            )
        )
        {
            yield return channel;
        }
    }

    public async IAsyncEnumerable<Role> GetGuildRolesAsync(
        Snowflake guildId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (guildId == Guild.DirectMessages.Id)
            yield break;

        var response = await GetJsonResponseAsync($"guilds/{guildId}/roles", cancellationToken);
        foreach (var roleJson in response.EnumerateArray())
            yield return Role.Parse(roleJson);
    }

    public async ValueTask<Member?> TryGetGuildMemberAsync(
        Snowflake guildId,
        Snowflake memberId,
        CancellationToken cancellationToken = default
    )
    {
        if (guildId == Guild.DirectMessages.Id)
            return null;

        var response = await TryGetJsonResponseAsync(
            $"guilds/{guildId}/members/{memberId}",
            cancellationToken
        );
        return response?.Pipe(j => Member.Parse(j, guildId));
    }

    public async ValueTask<Invite?> TryGetInviteAsync(
        string code,
        CancellationToken cancellationToken = default
    )
    {
        var response = await TryGetJsonResponseAsync($"invites/{code}", cancellationToken);
        return response?.Pipe(Invite.Parse);
    }

    public async ValueTask<Channel> GetChannelAsync(
        Snowflake channelId,
        CancellationToken cancellationToken = default
    )
    {
        var response = await GetJsonResponseAsync($"channels/{channelId}", cancellationToken);

        var parentId = response
            .GetPropertyOrNull("parent_id")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        // It's possible for the parent channel to be inaccessible, despite the
        // child channel being accessible.
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/1108
        var parent = parentId is not null
            ? await TryGetChannelAsync(parentId.Value, cancellationToken)
            : null;

        return Channel.Parse(response, parent);
    }

    public async ValueTask<Channel?> TryGetChannelAsync(
        Snowflake channelId,
        CancellationToken cancellationToken = default
    )
    {
        var response = await TryGetJsonResponseAsync($"channels/{channelId}", cancellationToken);
        if (response is null)
            return null;

        var parentId = response
            .Value.GetPropertyOrNull("parent_id")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        Channel? parent = null;
        if (parentId is not null)
        {
            // It's possible for the parent channel to be inaccessible, despite the
            // child channel being accessible.
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/1108
            parent = await TryGetChannelAsync(parentId.Value, cancellationToken);
        }

        return Channel.Parse(response.Value, parent);
    }

    public async IAsyncEnumerable<Channel> GetChannelThreadsAsync(
        IReadOnlyList<Channel> channels,
        bool includeArchived = false,
        Snowflake? before = null,
        Snowflake? after = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var filteredChannels = channels
            // Categories cannot have threads
            .Where(c => !c.IsCategory)
            // Voice channels cannot have threads
            .Where(c => !c.IsVoice)
            // Empty channels cannot have threads
            .Where(c => !c.IsEmpty)
            // If the 'before' boundary is specified, skip channels that don't have messages
            // for that range, because thread-start event should always be accompanied by a message.
            // Note that we don't perform a similar check for the 'after' boundary, because
            // threads may have messages in range, even if the parent channel doesn't.
            .Where(c => before is null || c.MayHaveMessagesBefore(before.Value))
            .ToArray();

        // Track yielded thread IDs to avoid duplicates that can occur when a thread transitions
        // from active to archived between the two separate API calls used to fetch threads.
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/1433
        var seenThreadIds = new HashSet<Snowflake>();

        // User accounts can only fetch threads using the search endpoint
        if (await ResolveTokenKindAsync(cancellationToken) == TokenKind.User)
        {
            foreach (var channel in filteredChannels)
            {
                // Either include both active and archived threads, or only active threads
                foreach (
                    var isArchived in includeArchived ? new[] { false, true } : new[] { false }
                )
                {
                    // Offset is just the index of the last thread in the previous batch
                    var currentOffset = 0;
                    while (true)
                    {
                        var url = new UrlBuilder()
                            .SetPath($"channels/{channel.Id}/threads/search")
                            .SetQueryParameter("sort_by", "last_message_time")
                            .SetQueryParameter("sort_order", "desc")
                            .SetQueryParameter("archived", isArchived.ToString().ToLowerInvariant())
                            .SetQueryParameter("offset", currentOffset.ToString())
                            .Build();

                        // Can be null on channels that the user cannot access or channels without threads
                        var response = await TryGetJsonResponseAsync(url, cancellationToken);
                        if (response is null)
                            break;

                        var breakOuter = false;

                        foreach (
                            var threadJson in response.Value.GetProperty("threads").EnumerateArray()
                        )
                        {
                            var thread = Channel.Parse(threadJson, channel);

                            // If the 'after' boundary is specified, we can break early,
                            // because threads are sorted by last message timestamp.
                            if (after is not null && !thread.MayHaveMessagesAfter(after.Value))
                            {
                                breakOuter = true;
                                break;
                            }

                            if (seenThreadIds.Add(thread.Id))
                                yield return thread;

                            currentOffset++;
                        }

                        if (breakOuter)
                            break;

                        if (!response.Value.GetProperty("has_more").GetBoolean())
                            break;
                    }
                }
            }
        }
        // Bot accounts can only fetch threads using the threads endpoint
        else
        {
            var guilds = new HashSet<Snowflake>();
            foreach (var channel in filteredChannels)
                guilds.Add(channel.GuildId);

            // Active threads
            foreach (var guildId in guilds)
            {
                var parentsById = filteredChannels.ToDictionary(c => c.Id);

                var response = await GetJsonResponseAsync(
                    $"guilds/{guildId}/threads/active",
                    cancellationToken
                );

                foreach (var threadJson in response.GetProperty("threads").EnumerateArray())
                {
                    var parent = threadJson
                        .GetPropertyOrNull("parent_id")
                        ?.GetNonWhiteSpaceStringOrNull()
                        ?.Pipe(Snowflake.Parse)
                        .Pipe(parentsById.GetValueOrDefault);

                    if (filteredChannels.Contains(parent))
                    {
                        var thread = Channel.Parse(threadJson, parent);

                        if (seenThreadIds.Add(thread.Id))
                            yield return thread;
                    }
                }
            }

            // Archived threads
            if (includeArchived)
            {
                foreach (var channel in filteredChannels)
                {
                    foreach (var archiveType in new[] { "public", "private" })
                    {
                        string? currentBefore = null;

                        while (true)
                        {
                            // Threads are sorted by archive timestamp, not by last message timestamp
                            var url = new UrlBuilder()
                                .SetPath($"channels/{channel.Id}/threads/archived/{archiveType}")
                                .SetQueryParameter("before", currentBefore)
                                .Build();

                            // Can be null on certain channels
                            var response = await TryGetJsonResponseAsync(url, cancellationToken);
                            if (response is null)
                                break;

                            foreach (
                                var threadJson in response
                                    .Value.GetProperty("threads")
                                    .EnumerateArray()
                            )
                            {
                                var thread = Channel.Parse(threadJson, channel);

                                currentBefore = threadJson
                                    .GetProperty("thread_metadata")
                                    .GetProperty("archive_timestamp")
                                    .GetString();

                                if (seenThreadIds.Add(thread.Id))
                                    yield return thread;
                            }

                            if (!response.Value.GetProperty("has_more").GetBoolean())
                                break;
                        }
                    }
                }
            }
        }
    }

    private async ValueTask<Message?> TryGetFirstMessageAsync(
        Snowflake channelId,
        Snowflake? after = null,
        CancellationToken cancellationToken = default
    )
    {
        var url = new UrlBuilder()
            .SetPath($"channels/{channelId}/messages")
            .SetQueryParameter("limit", "1")
            .SetQueryParameter("after", (after ?? Snowflake.Zero).ToString())
            .Build();

        var response = await GetJsonResponseAsync(url, cancellationToken);
        var message = response.EnumerateArray().Select(Message.Parse).FirstOrDefault();

        return message;
    }

    private async ValueTask<Message?> TryGetLastMessageAsync(
        Snowflake channelId,
        Snowflake? before = null,
        CancellationToken cancellationToken = default
    )
    {
        var url = new UrlBuilder()
            .SetPath($"channels/{channelId}/messages")
            .SetQueryParameter("limit", "1")
            .SetQueryParameter("before", before?.ToString())
            .Build();

        var response = await GetJsonResponseAsync(url, cancellationToken);
        return response.EnumerateArray().Select(Message.Parse).LastOrDefault();
    }

    public async ValueTask<Message?> TryGetMessageAsync(
        Snowflake channelId,
        Snowflake messageId,
        CancellationToken cancellationToken = default
    )
    {
        // Use the listing endpoint with 'around' because the dedicated message endpoint
        // is not accessible to user tokens.
        var url = new UrlBuilder()
            .SetPath($"channels/{channelId}/messages")
            .SetQueryParameter("around", messageId.ToString())
            .SetQueryParameter("limit", "1")
            .Build();

        var response = await TryGetJsonResponseAsync(url, cancellationToken);
        if (response is null)
            return null;

        return response
            .Value.EnumerateArray()
            .Select(Message.Parse)
            .FirstOrDefault(message => message.Id == messageId);
    }

    private async ValueTask<Message?> ResolveThreadStarterMessageAsync(
        Message message,
        CancellationToken cancellationToken = default
    )
    {
        if (message.Kind != MessageKind.ThreadStarterMessage)
            return message;

        if (message.Reference?.ChannelId is not { } channelId)
            return null;
        if (message.Reference?.MessageId is not { } messageId)
            return null;

        return await TryGetMessageAsync(channelId, messageId, cancellationToken);
    }

    private async ValueTask<IReadOnlyList<Message>> GetMessageProbePageAfterAsync(
        Snowflake channelId,
        Snowflake after,
        Snowflake? before = null,
        CancellationToken cancellationToken = default
    )
    {
        var url = new UrlBuilder()
            .SetPath($"channels/{channelId}/messages")
            .SetQueryParameter("limit", MessageCountEstimator.PageSize.ToString())
            .SetQueryParameter("after", after.ToString())
            .SetQueryParameter("before", before?.ToString())
            .Build();

        var response = await GetJsonResponseAsync(url, cancellationToken);
        return response.EnumerateArray().Select(Message.Parse).Reverse().ToArray();
    }

    private static MessageDensitySample? TryCreateDensitySample(IReadOnlyList<Message> page)
    {
        if (page.Count < 2)
            return null;

        var span = (page[^1].Timestamp - page[0].Timestamp).Duration().TotalSeconds;
        return span > 0 ? new MessageDensitySample(page[0].Timestamp, page.Count / span) : null;
    }

    public async ValueTask<long?> EstimateMessageCountByDensityAsync(
        Channel channel,
        Snowflake? after = null,
        Snowflake? before = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var firstPage = await GetMessageProbePageAfterAsync(
                channel.Id,
                after ?? Snowflake.Zero,
                before,
                cancellationToken
            );

            if (firstPage.Count == 0)
                return 0;

            var lastMessage = await TryGetLastMessageAsync(channel.Id, before, cancellationToken);
            if (lastMessage is null || lastMessage.Timestamp < firstPage[0].Timestamp)
                return firstPage.Count;

            var hasMoreMessages = firstPage[^1].Id != lastMessage.Id;
            if (!MessageCountEstimator.ShouldEstimate(firstPage.Count, hasMoreMessages))
                return firstPage.Count;

            var samples = new List<MessageDensitySample>();
            if (TryCreateDensitySample(firstPage) is { } firstSample)
                samples.Add(firstSample);

            const int sampleCount = 10;
            var start = firstPage[0].Timestamp;
            var end = lastMessage.Timestamp;
            var duration = end - start;

            for (var i = 1; i < sampleCount - 1; i++)
            {
                var fraction = i / (double)(sampleCount - 1);
                var sampleTime = start + duration * fraction;
                var page = await GetMessageProbePageAfterAsync(
                    channel.Id,
                    Snowflake.FromDate(sampleTime),
                    before,
                    cancellationToken
                );

                if (TryCreateDensitySample(page) is { } sample)
                    samples.Add(sample);
            }

            if (
                TryCreateDensitySample(
                    await GetMessageProbePageAfterAsync(
                        channel.Id,
                        Snowflake.FromDate(end - TimeSpan.FromSeconds(1)),
                        before,
                        cancellationToken
                    )
                ) is
                { } lastSample
            )
            {
                samples.Add(lastSample);
            }

            var estimated = MessageCountEstimator.EstimateTotal(start, end, samples);
            return estimated is not null ? Math.Max(firstPage.Count, estimated.Value) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async IAsyncEnumerable<Message> GetMessagesAsync(
        Snowflake channelId,
        Snowflake? after = null,
        Snowflake? before = null,
        IProgress<Percentage>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        // Get the last message in the specified range, so we can later calculate the
        // progress based on the difference between message timestamps.
        // This also snapshots the boundaries, which means that messages posted after
        // the export started will not appear in the output.
        var lastMessage = await TryGetLastMessageAsync(channelId, before, cancellationToken);
        if (lastMessage is null || after is not null && lastMessage.Id.Value <= after.Value.Value)
            yield break;

        // Keep track of the first message in range in order to calculate the progress
        var firstMessage = default(Message);

        var currentAfter = after ?? Snowflake.Zero;
        while (true)
        {
            var url = new UrlBuilder()
                .SetPath($"channels/{channelId}/messages")
                .SetQueryParameter("limit", "100")
                .SetQueryParameter("after", currentAfter.ToString())
                .Build();

            var response = await GetJsonResponseAsync(url, cancellationToken);

            var messages = response
                .EnumerateArray()
                .Select(Message.Parse)
                // Messages are returned from newest to oldest, so we need to reverse them
                .Reverse()
                .ToArray();

            // Break if there are no messages (can happen if messages are deleted during execution)
            if (!messages.Any())
                yield break;

            // If all messages are empty, make sure that it's not because the bot account doesn't
            // have the MESSAGE_CONTENT intent enabled.
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/1106#issuecomment-1741548959
            if (messages.All(m => m.IsEmpty))
                await EnsureMessageContentIntentAsync(cancellationToken);

            foreach (var message in messages)
            {
                firstMessage ??= message;

                // Ensure that the messages are in range. Use snowflake IDs instead of
                // timestamps so same-millisecond boundary messages are still excluded.
                if (before is not null && message.Id.Value >= before.Value.Value)
                    yield break;

                // Report progress based on timestamps
                if (progress is not null)
                {
                    var exportedDuration = (message.Timestamp - firstMessage.Timestamp).Duration();
                    var totalDuration = (lastMessage.Timestamp - firstMessage.Timestamp).Duration();

                    progress.Report(
                        Percentage.FromFraction(
                            // Avoid division by zero if all messages have the exact same timestamp
                            // (which happens when there's only one message in the channel)
                            totalDuration > TimeSpan.Zero
                                ? exportedDuration / totalDuration
                                : 1
                        )
                    );
                }

                var resolvedMessage = await ResolveThreadStarterMessageAsync(
                    message,
                    cancellationToken
                );
                if (resolvedMessage is not null)
                    yield return resolvedMessage;

                currentAfter = message.Id;
            }
        }
    }

    public async IAsyncEnumerable<Message> GetMessagesInReverseAsync(
        Snowflake channelId,
        Snowflake? after = null,
        Snowflake? before = null,
        IProgress<Percentage>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        // Get the first message in the specified range, so we can later calculate the
        // progress based on the difference between message timestamps.
        // Snapshotting is not necessary here because new messages can't appear in the past.
        var firstMessage = await TryGetFirstMessageAsync(channelId, after, cancellationToken);
        if (
            firstMessage is null
            || before is not null && firstMessage.Id.Value >= before.Value.Value
        )
            yield break;

        // Keep track of the last message in range in order to calculate the progress
        var lastMessage = default(Message);

        var currentBefore = before;
        while (true)
        {
            var url = new UrlBuilder()
                .SetPath($"channels/{channelId}/messages")
                .SetQueryParameter("limit", "100")
                .SetQueryParameter("before", currentBefore?.ToString())
                .Build();

            var response = await GetJsonResponseAsync(url, cancellationToken);

            var messages = response.EnumerateArray().Select(Message.Parse).ToArray();

            // Break if there are no messages (can happen if messages are deleted during execution)
            if (!messages.Any())
                yield break;

            // If all messages are empty, make sure that it's not because the bot account doesn't
            // have the MESSAGE_CONTENT intent enabled.
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/1106#issuecomment-1741548959
            if (messages.All(m => m.IsEmpty))
                await EnsureMessageContentIntentAsync(cancellationToken);

            foreach (var message in messages)
            {
                // Reverse exports walk newest to oldest, so once we hit the lower
                // exclusive bound, every later item in the page is also out of range.
                if (after is not null && message.Id.Value <= after.Value.Value)
                    yield break;

                lastMessage ??= message;

                // Report progress based on timestamps
                if (progress is not null)
                {
                    var exportedDuration = (lastMessage.Timestamp - message.Timestamp).Duration();
                    var totalDuration = (lastMessage.Timestamp - firstMessage.Timestamp).Duration();

                    progress.Report(
                        Percentage.FromFraction(
                            // Avoid division by zero if all messages have the exact same timestamp
                            // (which happens when there's only one message in the channel)
                            totalDuration > TimeSpan.Zero
                                ? exportedDuration / totalDuration
                                : 1
                        )
                    );
                }

                var resolvedMessage = await ResolveThreadStarterMessageAsync(
                    message,
                    cancellationToken
                );
                if (resolvedMessage is not null)
                    yield return resolvedMessage;
            }

            currentBefore = messages.Last().Id;
        }
    }

    public async IAsyncEnumerable<User> GetMessageReactionsAsync(
        Snowflake channelId,
        Snowflake messageId,
        Emoji emoji,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var reactionName = emoji.Id is not null
            // Custom emoji
            ? emoji.Name + ':' + emoji.Id
            // Standard emoji
            : emoji.Name;

        var currentAfter = Snowflake.Zero;
        while (true)
        {
            var url = new UrlBuilder()
                .SetPath(
                    $"channels/{channelId}/messages/{messageId}/reactions/{Uri.EscapeDataString(reactionName)}"
                )
                .SetQueryParameter("limit", "100")
                .SetQueryParameter("after", currentAfter.ToString())
                .Build();

            // Can be null on reactions with an emoji that has been deleted (?)
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/1226
            var response = await TryGetJsonResponseAsync(url, cancellationToken);
            if (response is null)
                yield break;

            var count = 0;
            foreach (var userJson in response.Value.EnumerateArray())
            {
                var user = User.Parse(userJson);
                yield return user;

                currentAfter = user.Id;
                count++;
            }

            if (count <= 0)
                yield break;
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _userRequestGate.Dispose();
        _botRequestGate.Dispose();
        _userRequestStartGate.Dispose();
    }
}
