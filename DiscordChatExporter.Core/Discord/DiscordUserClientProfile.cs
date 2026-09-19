using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Utils;

namespace DiscordChatExporter.Core.Discord;

// Builds a browser-like user-token header set without relying on a third-party
// client-properties service. Based on the official-web fingerprint work from
// Tyrrrz/DiscordChatExporter#1582.
internal sealed class DiscordUserClientProfile
{
    internal const int BrowserMajorVersion = 150;

    internal const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";

    internal const int FallbackClientBuildNumber = 594503;

    // Client-mod detection bits documented by Discord's web client ecosystem.
    internal static readonly UInt128 LaunchSignatureMask = new(
        0x0080101008100800,
        0x2081004001000800
    );

    private readonly string _clientLaunchId = Guid.NewGuid().ToString();
    private readonly string _launchSignature = GenerateLaunchSignature();
    private readonly string _clientHeartbeatSessionId = Guid.NewGuid().ToString();

    private string? _xSuperPropertiesHeader;
    private int _clientBuildNumberResolved;

    public async ValueTask AddHeadersAsync(
        Dictionary<string, string> headers,
        CancellationToken cancellationToken,
        bool refreshClientBuildNumber = true
    )
    {
        if (refreshClientBuildNumber)
            await RefreshClientBuildNumberAsync(cancellationToken);

        headers["User-Agent"] = BrowserUserAgent;
        headers["X-Super-Properties"] = GetXSuperPropertiesHeader();
        headers["X-Discord-Locale"] = "en-US";
        headers["Accept-Language"] = "en-US";
        headers["X-Debug-Options"] = "bugReporterEnabled";

        if (TryGetIanaTimeZone() is { } timeZone)
            headers["X-Discord-Timezone"] = timeZone;
    }

    private string GetXSuperPropertiesHeader() =>
        _xSuperPropertiesHeader ??= EncodeXSuperProperties(
            FallbackClientBuildNumber,
            _clientLaunchId,
            _launchSignature,
            _clientHeartbeatSessionId
        );

    private async ValueTask RefreshClientBuildNumberAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _clientBuildNumberResolved, 1, 0) != 0)
            return;

        var buildNumber = await TryScrapeClientBuildNumberAsync(cancellationToken);
        if (buildNumber is null)
            return;

        _xSuperPropertiesHeader = EncodeXSuperProperties(
            buildNumber.Value,
            _clientLaunchId,
            _launchSignature,
            _clientHeartbeatSessionId
        );
    }

    private static async ValueTask<int?> TryScrapeClientBuildNumberAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/app");
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

            using var response = await Http.Client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                timeout.Token
            );

            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync(timeout.Token);
            return TryParseClientBuildNumber(html);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The hardcoded fallback is intentionally kept if scraping fails.
            return null;
        }
    }

    private static string? TryGetIanaTimeZone()
    {
        var timeZone = TimeZoneInfo.Local;
        if (timeZone.HasIanaId)
            return timeZone.Id;

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZone.Id, out var ianaId)
            ? ianaId
            : null;
    }

    internal static string GenerateLaunchSignature()
    {
        Span<byte> bytes = stackalloc byte[16];
        Guid.NewGuid().TryWriteBytes(bytes, bigEndian: true, out _);

        var value =
            new UInt128(
                BinaryPrimitives.ReadUInt64BigEndian(bytes),
                BinaryPrimitives.ReadUInt64BigEndian(bytes[8..])
            ) & ~LaunchSignatureMask;

        BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)(value >> 64));
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], (ulong)value);

        return new Guid(bytes, bigEndian: true).ToString();
    }

    internal static string EncodeXSuperProperties(
        int clientBuildNumber,
        string clientLaunchId,
        string launchSignature,
        string clientHeartbeatSessionId
    )
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("os", "Windows");
            writer.WriteString("browser", "Chrome");
            writer.WriteString("device", "");
            writer.WriteString("system_locale", "en-US");
            writer.WriteBoolean("has_client_mods", false);
            writer.WriteString("browser_user_agent", BrowserUserAgent);
            writer.WriteString("browser_version", $"{BrowserMajorVersion}.0.0.0");
            writer.WriteString("os_version", "10");
            writer.WriteString("referrer", "");
            writer.WriteString("referring_domain", "");
            writer.WriteString("referrer_current", "");
            writer.WriteString("referring_domain_current", "");
            writer.WriteString("release_channel", "stable");
            writer.WriteNumber("client_build_number", clientBuildNumber);
            writer.WriteNull("client_event_source");
            writer.WriteString("client_launch_id", clientLaunchId);
            writer.WriteString("launch_signature", launchSignature);
            writer.WriteString("client_heartbeat_session_id", clientHeartbeatSessionId);
            writer.WriteString("client_app_state", "unfocused");
            writer.WriteEndObject();
        }

        return Convert.ToBase64String(buffer.WrittenSpan);
    }

    internal static int? TryParseClientBuildNumber(string html)
    {
        const string marker = "\"BUILD_NUMBER\":\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += marker.Length;
        var end = html.IndexOf('"', start);
        if (end < 0)
            return null;

        return
            int.TryParse(
                html.AsSpan(start, end - start),
                CultureInfo.InvariantCulture,
                out var buildNumber
            )
            && buildNumber > 0
            ? buildNumber
            : null;
    }
}
