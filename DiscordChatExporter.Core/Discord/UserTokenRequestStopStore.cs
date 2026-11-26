using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DiscordChatExporter.Core.Discord;

internal sealed class UserTokenRequestStopStore(string directoryPath)
{
    internal const string DirectoryOverrideEnvironmentVariable =
        "DISCORDCHATEXPORTER_USER_TOKEN_STOP_DIR";

    private const string LegacyRateLimitStopReason =
        "A previous HTTP 429 for this user token was recorded locally. The token remains blocked across restarts.";

    private readonly string _directoryPath = directoryPath;

    internal static UserTokenRequestStopStore CreateDefault()
    {
        // Lets tests, CI, and portable setups keep stop markers out of the real
        // application-data directory. Clearing the markers is already documented,
        // so this override does not weaken the stop itself.
        if (
            Environment.GetEnvironmentVariable(DirectoryOverrideEnvironmentVariable) is
            { Length: > 0 } overrideDirectoryPath
        )
            return new UserTokenRequestStopStore(overrideDirectoryPath);

        var rootPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(rootPath))
            rootPath = AppContext.BaseDirectory;

        return new UserTokenRequestStopStore(
            Path.Combine(rootPath, "DiscordChatExporter", "UserTokenRequestStops")
        );
    }

    internal bool TryGetStopReason(string token, out string? reason)
    {
        var path = GetMarkerPath(token);

        try
        {
            using var marker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(marker);
            var recordedReason = reader.ReadToEnd().Trim();

            // Markers written before the reason was recorded are empty files.
            reason = recordedReason.Length > 0 ? recordedReason : LegacyRateLimitStopReason;
            return true;
        }
        catch (FileNotFoundException)
        {
            reason = null;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            reason = null;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason =
                "The persistent stop state for this user token cannot be read. Further requests are blocked.";
            return true;
        }
    }

    internal void MarkStopped(string token, string reason)
    {
        try
        {
            Directory.CreateDirectory(_directoryPath);
            var path = GetMarkerPath(token);
            using var marker = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read
            );
            marker.Write(Encoding.UTF8.GetBytes(reason));
            marker.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(GetMarkerPath(token)))
        {
            // Another process may have recorded the same stop concurrently.
        }
    }

    private string GetMarkerPath(string token)
    {
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Path.Combine(_directoryPath, $"{Convert.ToHexString(tokenHash)}.stop");
    }
}
