using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DiscordChatExporter.Core.Discord;

internal sealed class UserTokenRequestStopStore(string directoryPath)
{
    private readonly string _directoryPath = directoryPath;

    internal static UserTokenRequestStopStore CreateDefault()
    {
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
            reason =
                "A previous HTTP 429 for this user token was recorded locally. The token remains blocked across restarts.";
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
            reason = "The persistent stop state for this user token cannot be read. Further requests are blocked.";
            return true;
        }
    }

    internal void MarkStopped(string token)
    {
        try
        {
            Directory.CreateDirectory(_directoryPath);
            var path = GetMarkerPath(token);
            using var marker = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
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
