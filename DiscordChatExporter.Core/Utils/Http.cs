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
using DiscordChatExporter.Core.Exceptions;
using HttpCloak;
using Polly;
using Polly.Retry;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Utils;

public static class Http
{
    // Leave room for the one-second margin and the largest adaptive cushion.
    // Longer waits cannot be passed to Task.Delay; stop instead of retrying early.
    internal static TimeSpan ParseRateLimitDelay(string? seconds)
    {
        if (
            !double.TryParse(
                seconds,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value
            )
            || !double.IsFinite(value)
            || value < 0
            || value > (uint.MaxValue - 1) / 1000d - 61
        )
            throw new InvalidRateLimitException();
        return TimeSpan.FromSeconds(value);
    }

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
            if (
                json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("retry_after", out var retryAfterElement)
            )
                return null;

            return ParseRateLimitDelay(
                retryAfterElement.ValueKind switch
                {
                    JsonValueKind.Number => retryAfterElement.GetRawText(),
                    JsonValueKind.String => retryAfterElement.GetString(),
                    _ => null,
                }
            );
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
                return ParseRateLimitDelay(
                        headerRetryAfter.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                    ) + TimeSpan.FromSeconds(1);

            if (response.Headers.RetryAfter?.Date is { } retryAt)
                return (retryAt - DateTimeOffset.UtcNow).Clamp(TimeSpan.Zero, TimeSpan.MaxValue)
                    + TimeSpan.FromSeconds(1);

            if (response.Headers.Contains("Retry-After"))
                throw new InvalidRateLimitException();

            if (response.Headers.TryGetValue("X-RateLimit-Reset-After") is { } resetAfter)
                return ParseRateLimitDelay(resetAfter) + TimeSpan.FromSeconds(1);
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
                        TimeSpan delay;
                        try
                        {
                            delay = await GetResponseRetryDelayAsync(args);
                            if (IsRateLimitResponse(args.Outcome.Result))
                                ParseRateLimitDelay(
                                    delay.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                                );
                        }
                        catch (InvalidRateLimitException)
                        {
                            args.Outcome.Result?.Dispose();
                            throw;
                        }

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
