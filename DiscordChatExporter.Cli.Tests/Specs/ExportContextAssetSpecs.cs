using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public sealed class ExportContextAssetSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceAssets_" + Guid.NewGuid().ToString("N")
    );

    public ExportContextAssetSpecs() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handleAsync
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => handleAsync(request, cancellationToken);
    }

    private ExportContext CreateHtmlContext(bool shouldDownloadAssets, string? assetsDirPath = null)
    {
        var guild = new Guild(new Snowflake(1), "Test Guild", "");
        var channel = new Channel(
            new Snowflake(2),
            ChannelKind.GuildTextChat,
            guild.Id,
            null,
            "general",
            null,
            null,
            null,
            false,
            null
        );
        var outputPath = Path.Combine(_dir, "chat.html");
        var request = new ExportRequest(
            guild,
            channel,
            outputPath,
            assetsDirPath,
            ExportFormat.HtmlDark,
            null,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: true,
            shouldDownloadAssets,
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );
        return new ExportContext(new DiscordClient("fake-token"), request);
    }

    [Fact]
    public async Task Resolve_asset_url_preserves_user_cancellation()
    {
        var context = CreateHtmlContext(shouldDownloadAssets: true);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        var act = async () =>
            await context.ResolveAssetUrlAsync(
                "https://example.com/avatar.png",
                cancellationTokenSource.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Resolve_asset_url_falls_back_to_remote_url_on_local_io_failure()
    {
        var blockedAssetsPath = Path.Combine(_dir, "assets-as-file");
        await File.WriteAllTextAsync(blockedAssetsPath, "not a directory");
        var context = CreateHtmlContext(shouldDownloadAssets: true, blockedAssetsPath);
        var url = "https://example.com/avatar.png";

        var result = await context.ResolveAssetUrlAsync(url);

        result.Should().Be(url);
    }

    [Fact]
    public async Task Resolve_asset_url_does_not_download_non_http_urls()
    {
        var context = CreateHtmlContext(shouldDownloadAssets: true);

        var result = await context.ResolveAssetUrlAsync("javascript:alert(1)");

        result.Should().Be("javascript:alert(1)");
    }

    [Fact]
    public async Task Download_asset_reuses_signed_discord_cdn_variants_by_normalized_url()
    {
        var requestUrls = new List<string>();
        using var client = new HttpClient(
            new DelegateHttpMessageHandler(
                (request, _) =>
                {
                    requestUrls.Add(request.RequestUri!.ToString());
                    return Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("asset"),
                        }
                    );
                }
            )
        );
        var downloader = new ExportAssetDownloader(_dir, reuse: false, client);

        var firstPath = await downloader.DownloadAsync(
            "https://cdn.discordapp.com/attachments/1/2/avatar.png?ex=111&is=222&hm=aaa"
        );
        var secondPath = await downloader.DownloadAsync(
            "https://cdn.discordapp.com/attachments/1/2/avatar.png?ex=333&is=444&hm=bbb"
        );

        secondPath.Should().Be(firstPath);
        downloader.DownloadedAssetCount.Should().Be(1);
        requestUrls.Should().ContainSingle();
    }

    [Fact]
    public async Task Download_asset_rejects_content_length_over_size_limit()
    {
        using var client = new HttpClient(
            new DelegateHttpMessageHandler(
                (_, _) =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("asset"),
                    };
                    response.Content.Headers.ContentLength = 5;
                    return Task.FromResult(response);
                }
            )
        );
        var downloader = new ExportAssetDownloader(_dir, reuse: false, client, maxFileSizeBytes: 4);

        var act = async () => await downloader.DownloadAsync("https://example.com/avatar.png");

        await act.Should().ThrowAsync<IOException>();
        Directory.EnumerateFiles(_dir).Should().BeEmpty();
    }

    [Fact]
    public async Task Download_asset_rejects_stream_over_size_limit_without_content_length()
    {
        using var client = new HttpClient(
            new DelegateHttpMessageHandler(
                (_, _) =>
                    Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("asset")),
                        }
                    )
            )
        );
        var downloader = new ExportAssetDownloader(_dir, reuse: false, client, maxFileSizeBytes: 4);

        var act = async () => await downloader.DownloadAsync("https://example.com/avatar.png");

        await act.Should().ThrowAsync<IOException>();
        Directory.EnumerateFiles(_dir).Should().BeEmpty();
    }
}
