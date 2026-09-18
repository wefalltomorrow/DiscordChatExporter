using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using AsyncKeyedLock;
using DiscordChatExporter.Core.Utils;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal partial class ExportAssetDownloader(
    string workingDirPath,
    bool reuse,
    HttpClient? httpClient = null,
    long maxFileSizeBytes = 512 * 1024 * 1024
)
{
    private static readonly AsyncKeyedLocker<string> Locker = new();
    private const int CopyBufferSize = 81920;

    // File paths of the previously downloaded assets
    private readonly Dictionary<string, string> _previousPathsByNormalizedUrl = new(
        StringComparer.Ordinal
    );

    // Number of distinct asset URLs resolved during this export (downloaded or reused).
    // Best-effort metric for the export manifest.
    public int DownloadedAssetCount => _previousPathsByNormalizedUrl.Count;

    public async ValueTask<string> DownloadAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedUrl = NormalizeUrl(url);
        var fileName = GetFileNameFromUrl(url);
        var filePath = Path.Combine(workingDirPath, fileName);

        using var _ = await Locker.LockAsync(filePath, cancellationToken);

        if (_previousPathsByNormalizedUrl.TryGetValue(normalizedUrl, out var cachedFilePath))
            return cachedFilePath;

        // Reuse existing files if we're allowed to
        if (reuse && File.Exists(filePath))
            return _previousPathsByNormalizedUrl[normalizedUrl] = filePath;

        // Check for a file cached by the legacy naming scheme (5-char hash) and rename it
        // to the new naming scheme to preserve backwards compatibility with existing exports
        if (reuse)
        {
            foreach (var legacyFileName in GetLegacyFileNamesFromUrl(url))
            {
                var legacyFilePath = Path.Combine(workingDirPath, legacyFileName);
                if (!File.Exists(legacyFilePath))
                    continue;

                // Overwrite in case the destination file was created concurrently between our
                // earlier existence check and this move operation.
                try
                {
                    File.Move(legacyFilePath, filePath, overwrite: true);
                    return _previousPathsByNormalizedUrl[normalizedUrl] = filePath;
                }
                catch (IOException)
                {
                    // The legacy file was moved/deleted concurrently. Upgrading old files is
                    // best-effort, so try the next legacy variant or fall through to download.
                }
            }
        }

        Directory.CreateDirectory(workingDirPath);

        await Http.ResiliencePipeline.ExecuteAsync(
            async innerCancellationToken =>
            {
                var tempFilePath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

                try
                {
                    // Download the file
                    using var response = await (httpClient ?? Http.Client).GetAsync(
                        url,
                        HttpCompletionOption.ResponseHeadersRead,
                        innerCancellationToken
                    );
                    response.EnsureSuccessStatusCode();
                    ThrowIfTooLarge(response.Content.Headers.ContentLength, maxFileSizeBytes, url);

                    await using (var output = File.Create(tempFilePath))
                    {
                        await CopyToAsync(
                            response.Content,
                            output,
                            maxFileSizeBytes,
                            url,
                            innerCancellationToken
                        );
                    }

                    File.Move(tempFilePath, filePath, overwrite: true);
                }
                catch
                {
                    try
                    {
                        File.Delete(tempFilePath);
                    }
                    catch
                    { /* best-effort temp cleanup */
                    }

                    throw;
                }
            },
            cancellationToken
        );

        return _previousPathsByNormalizedUrl[normalizedUrl] = filePath;
    }

    private static void ThrowIfTooLarge(long? contentLength, long maxFileSizeBytes, string url)
    {
        if (contentLength is not null && contentLength > maxFileSizeBytes)
            throw new IOException(
                $"Asset '{url}' exceeds the maximum download size of {maxFileSizeBytes} bytes."
            );
    }

    private static async ValueTask CopyToAsync(
        HttpContent content,
        Stream output,
        long maxFileSizeBytes,
        string url,
        CancellationToken cancellationToken
    )
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[CopyBufferSize];
        long totalBytesRead = 0;

        while (true)
        {
            var bytesRead = await input.ReadAsync(buffer, cancellationToken);
            if (bytesRead <= 0)
                return;

            totalBytesRead += bytesRead;
            if (totalBytesRead > maxFileSizeBytes)
            {
                throw new IOException(
                    $"Asset '{url}' exceeds the maximum download size of {maxFileSizeBytes} bytes."
                );
            }

            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }
    }
}

internal partial class ExportAssetDownloader
{
    private static string NormalizeUrl(string url)
    {
        // Remove signature parameters from Discord CDN/media URLs to normalize them
        var uri = new Uri(url);
        if (
            !string.Equals(uri.Host, "cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Host, "media.discordapp.net", StringComparison.OrdinalIgnoreCase)
        )
        {
            return url;
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        query.Remove("ex");
        query.Remove("is");
        query.Remove("hm");

        return uri.GetLeftPart(UriPartial.Path) + query;
    }

    private static string GetFileNameFromUrl(string url, string urlHash)
    {
        // Try to extract the file name from URL
        var fileName = new Uri(url, UriKind.RelativeOrAbsolute).TryGetFileName();

        // If it's not there, just use the URL hash as the file name
        if (string.IsNullOrWhiteSpace(fileName))
            return urlHash;

        // Otherwise, use the original file name but inject the hash in the middle
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var fileExtension = Path.GetExtension(fileName);

        // Probably not a file extension, just a dot in a long file name
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/812
        if (fileExtension.Length > 41)
        {
            fileNameWithoutExtension = fileName;
            fileExtension = "";
        }

        return Path.EscapeFileName(
            fileNameWithoutExtension.Truncate(42) + '-' + urlHash + fileExtension
        );
    }

    private static string GetFileNameFromUrl(string url) =>
        GetFileNameFromUrl(
            url,
            // 16 chars = 64 bits, reaches 1% collision probability at ~609 million files
            SHA256
                .HashData(Encoding.UTF8.GetBytes(NormalizeUrl(url)))
                .Pipe(Convert.ToHexStringLower)
                .Truncate(16)
        );

    // Legacy naming used a 5-char hash, kept for backwards compatibility with existing exports.
    // Both lowercase (newer) and uppercase (older) variants exist in the wild.
    private static IReadOnlyList<string> GetLegacyFileNamesFromUrl(string url)
    {
        var hashData = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeUrl(url)));

        return
        [
            GetFileNameFromUrl(url, Convert.ToHexStringLower(hashData).Truncate(5)),
            GetFileNameFromUrl(url, Convert.ToHexString(hashData).Truncate(5)),
        ];
    }
}
