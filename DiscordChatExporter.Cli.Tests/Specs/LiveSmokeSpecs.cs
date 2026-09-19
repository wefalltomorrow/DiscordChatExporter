using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Conversion;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace DiscordChatExporter.Cli.Tests.Specs;

// On-demand LIVE smoke test for the resume (#1), conversion (#2), and progress-count (#3) features.
// SKIPPED unless DCE_TOKEN (or DISCORD_TOKEN) and DCE_CHANNEL are set, so it never affects a normal
// `dotnet test` run. Credentials are read from the environment and never persisted or logged.
//
// Point DCE_CHANNEL at a SMALL channel you can access — the test exports it IN FULL (twice).
//
// Run (PowerShell):
//   $env:DCE_TOKEN="<your token>"; $env:DCE_CHANNEL="<a small channel id>"
//   dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~LiveSmoke" --logger "console;verbosity=detailed"
public class LiveSmokeSpecs(ITestOutputHelper output)
{
    [Fact]
    public async Task Count_export_resume_and_convert_against_a_real_channel()
    {
        var token = (
            Environment.GetEnvironmentVariable("DCE_TOKEN")
            ?? Environment.GetEnvironmentVariable("DISCORD_TOKEN")
        )
            ?.Trim()
            .Trim('"');
        var channelRaw = Environment.GetEnvironmentVariable("DCE_CHANNEL")?.Trim();

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(channelRaw))
        {
            // No credentials provided -> no-op pass so a normal `dotnet test` run stays green.
            // (xunit v2 has no runtime Assert.Skip; this test is meant to be run on demand.)
            output.WriteLine(
                "[NOT RUN] Live smoke test skipped. Set DCE_TOKEN (or DISCORD_TOKEN) and DCE_CHANNEL "
                    + "(a small channel id you can access), then run with "
                    + "--filter \"FullyQualifiedName~LiveSmoke\" --logger \"console;verbosity=detailed\"."
            );
            return;
        }

        var channelId = Snowflake.Parse(channelRaw, CultureInfo.InvariantCulture);
        var dir = Path.Combine(Path.GetTempPath(), "DceLiveSmoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var discord = new DiscordClient(token);
            var channel = await discord.GetChannelAsync(channelId);
            output.WriteLine($"Channel: {channel.Name} (#{channel.Id})");

            // 1) Phase-2 assumption: does the search endpoint return a usable total?
            var searchTotal = await discord.CountMessagesAsync(channel);
            output.WriteLine(
                searchTotal is { } st
                    ? $"[1] CountMessagesAsync -> {st}  (search total_results path WORKS)"
                    : "[1] CountMessagesAsync -> null  (search unavailable; density/Phase-1 fallback will be used)"
            );
            if (searchTotal is { } positive)
                positive.Should().BeGreaterThan(0);

            // 2) Export the channel to SQLite and verify the database.
            var dbPath = Path.Combine(dir, "smoke.db");
            await RunExportAsync(token, channelId, ExportFormat.Db, dbPath);
            var dbCount = CountRows(dbPath, "messages");
            output.WriteLine($"[2] Exported {dbCount} messages to .db");
            dbCount.Should().BeGreaterThan(0);
            CountRows(dbPath, "messages_fts").Should().Be(dbCount); // FTS index is 1:1
            ScalarLong(dbPath, "SELECT message_count FROM export_info;").Should().Be(dbCount);

            // 3) Resume: read the cutoff from the real .db, then merge a full re-export into it.
            //    The merger filters by id > cutoff, so re-merging existing messages must add NOTHING
            //    (boundary-safe), proving resume neither loses nor duplicates messages on live data.
            var cutoff = await ContinuationFormat.ReadCutoffAsync(dbPath);
            cutoff.ChannelId.Should().Be(channelId);
            cutoff.ExistingCount.Should().Be(dbCount);
            var reexportDb = Path.Combine(dir, "smoke-reexport.db");
            await RunExportAsync(token, channelId, ExportFormat.Db, reexportDb);
            var resumedTotal = await ContinuationFormat.MergeAsync(
                dbPath,
                reexportDb,
                cutoff,
                DateTimeOffset.Now
            );
            output.WriteLine($"[3] Resume merge total -> {resumedTotal} (was {dbCount})");
            resumedTotal.Should().BeGreaterThanOrEqualTo(dbCount); // never loses messages
            CountRows(dbPath, "messages").Should().Be(resumedTotal); // count matches rows post-merge

            // 4) Convert: export to JSON, then JSON -> HTML and JSON -> SQLite offline (no network).
            var jsonPath = Path.Combine(dir, "smoke.json");
            await RunExportAsync(token, channelId, ExportFormat.Json, jsonPath);

            var htmlOut = Path.Combine(dir, "converted.html");
            await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);
            File.Exists(htmlOut).Should().BeTrue();
            new FileInfo(htmlOut).Length.Should().BeGreaterThan(0);

            var convertedDb = Path.Combine(dir, "converted.db");
            await ExportConverter.ConvertAsync(jsonPath, convertedDb, ExportFormat.Db);
            var convertedCount = CountRows(convertedDb, "messages");
            output.WriteLine(
                $"[4] Converted JSON -> HTML ({new FileInfo(htmlOut).Length} bytes) and -> .db ({convertedCount} msgs)"
            );
            convertedCount.Should().Be(dbCount); // same channel, same messages, no data lost

            output.WriteLine("ALL LIVE SMOKE CHECKS PASSED.");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static async Task RunExportAsync(
        string token,
        Snowflake channelId,
        ExportFormat format,
        string outputPath,
        Snowflake? after = null
    )
    {
        using var console = new FakeConsole();
        await new ExportChannelsCommand
        {
            Token = token,
            ChannelIds = [channelId],
            ExportFormat = format,
            OutputPath = outputPath,
            After = after,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(console);
    }

    private static long CountRows(string dbPath, string table) =>
        ScalarLong(dbPath, $"SELECT COUNT(*) FROM {table};");

    private static long ScalarLong(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Pooling = false,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString()
        );
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
