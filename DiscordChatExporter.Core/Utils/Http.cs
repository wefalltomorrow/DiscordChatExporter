using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HttpCloak;
using Polly;
using Polly.Retry;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Utils;

public static class Http
{
    public static HttpClient Client { get; } = new();

    internal static ResiliencePropertyKey<
        Func<TimeSpan, CancellationToken, ValueTask>
    > RateLimitDelayHandlerKey { get; } = new("DiscordRateLimitDelayHandler");

    private static ResiliencePropertyKey<TimeSpan> RateLimitDelayKey { get; } =
        new("DiscordRateLimitDelay");

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
        ||
        // Treat all server-side errors as retryable
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/908
        (int)statusCode >= 500;

    private static bool IsRetryableException(Exception exception) =>
        exception
            .GetSelfAndDescendants()
            .Any(ex =>
                ex
                    is TimeoutException
                        or SocketException
                        or AuthenticationException
                        or HttpCloakException
                || ex is HttpRequestException hrex
                    && IsRetryableStatusCode(hrex.StatusCode ?? HttpStatusCode.OK)
            );

    private static bool IsRateLimitResponse(HttpResponseMessage? response) =>
        response?.StatusCode == HttpStatusCode.TooManyRequests;

    private static async ValueTask<TimeSpan?> TryGetDiscordRetryAfterAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        if (!IsRateLimitResponse(response))
            return null;

        try
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            using var json = JsonDocument.Parse(payload);
            if (!json.RootElement.TryGetProperty("retry_after", out var retryAfterElement))
                return null;

            double retryAfterSeconds;
            if (
                retryAfterElement.ValueKind == JsonValueKind.Number
                && retryAfterElement.TryGetDouble(out retryAfterSeconds)
            )
            {
                // Parsed below.
            }
            else if (
                retryAfterElement.ValueKind == JsonValueKind.String
                && double.TryParse(
                    retryAfterElement.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out retryAfterSeconds
                )
            )
            {
                // Parsed below.
            }
            else
            {
                return null;
            }

            return retryAfterSeconds >= 0 && double.IsFinite(retryAfterSeconds)
                ? TimeSpan.FromSeconds(retryAfterSeconds)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static async ValueTask<TimeSpan> GetResponseRetryDelayAsync(
        RetryDelayGeneratorArguments<HttpResponseMessage> args
    )
    {
        // Discord's 429 body is the most direct source of retry timing. Prefer it, then
        // standard/advisory headers, and only then fall back to exponential retry.
        if (args.Outcome.Result is { } response && IsRateLimitResponse(response))
        {
            if (
                await TryGetDiscordRetryAfterAsync(response, args.Context.CancellationToken) is
                { } bodyRetryAfter
            )
            {
                return bodyRetryAfter + TimeSpan.FromSeconds(1);
            }

            if (response.Headers.RetryAfter?.Delta is { } headerRetryAfter)
                return headerRetryAfter + TimeSpan.FromSeconds(1);

            var resetAfterSeconds = response
                .Headers.TryGetValue("X-RateLimit-Reset-After")
                ?.Pipe(v => double.ParseOrNull(v, CultureInfo.InvariantCulture));

            if (resetAfterSeconds is >= 0)
                return TimeSpan.FromSeconds(resetAfterSeconds.Value) + TimeSpan.FromSeconds(1);
        }

        return TimeSpan.FromSeconds(Math.Pow(2, args.AttemptNumber) + 1);
    }

    public static ResiliencePipeline ResiliencePipeline { get; } =
        new ResiliencePipelineBuilder()
            .AddRetry(
                new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(IsRetryableException),
                    MaxRetryAttempts = 4,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(1),
                    UseJitter = true,
                }
            )
            .Build();

    public static ResiliencePipeline<HttpResponseMessage> ResponseResiliencePipeline { get; } =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(
                new RetryStrategyOptions<HttpResponseMessage>
                {
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<Exception>(IsRetryableException)
                        .HandleResult(m => IsRetryableStatusCode(m.StatusCode)),
                    MaxRetryAttempts = 8,
                    DelayGenerator = async args =>
                    {
                        var delay = await GetResponseRetryDelayAsync(args);

                        if (
                            IsRateLimitResponse(args.Outcome.Result)
                            && args.Context.Properties.TryGetValue(RateLimitDelayHandlerKey, out _)
                        )
                        {
                            args.Context.Properties.Set(RateLimitDelayKey, delay);
                            return TimeSpan.Zero;
                        }

                        return delay;
                    },
                    OnRetry = async args =>
                    {
                        var response = args.Outcome.Result;
                        try
                        {
                            if (!IsRateLimitResponse(response))
                                return;

                            if (
                                !args.Context.Properties.TryGetValue(
                                    RateLimitDelayHandlerKey,
                                    out var delayHandler
                                )
                            )
                            {
                                return;
                            }

                            var delay = args.Context.Properties.GetValue(
                                RateLimitDelayKey,
                                args.RetryDelay
                            );

                            response?.Dispose();
                            await delayHandler(delay, args.Context.CancellationToken);
                        }
                        finally
                        {
                            response?.Dispose();
                        }
                    },
                }
            )
            .Build();
}
