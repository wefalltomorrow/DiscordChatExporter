using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class CsvExportMergerSpecs
{
    private const string Header = "AuthorID,Author,Date,Content,Attachments,Reactions\r\n";
    private const string HeaderWithMessageId =
        "MessageID,AuthorID,Author,Date,Content,Attachments,Reactions\r\n";

    private static string Row(string date, string content) =>
        $"\"5\",\"A\",\"{date}\",\"{content}\",\"\",\"\"\r\n";

    private static string Row(ulong id, string date, string content) =>
        $"\"{id}\",\"5\",\"A\",\"{date}\",\"{content}\",\"\",\"\"\r\n";

    private static async Task<string> WriteAsync(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-csvmerge-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Fact]
    public async Task Merge_wraps_an_unreadable_new_rows_file_as_InvalidExportException()
    {
        var existing = await WriteAsync("AuthorID,Author,Date,Content\r\n");
        var existingBefore = await File.ReadAllTextAsync(existing);
        var missingIncoming = Path.Combine(
            Path.GetTempPath(),
            $"dce-missing-{Guid.NewGuid():N}.csv"
        );

        try
        {
            var act = async () =>
                await CsvExportMerger.MergeAsync(
                    existing,
                    missingIncoming,
                    new ContinuationCutoff(
                        new Snowflake(333),
                        new Snowflake(0),
                        null,
                        true,
                        0,
                        false
                    )
                );

            await act.Should().ThrowAsync<InvalidExportException>();
            (await File.ReadAllTextAsync(existing)).Should().Be(existingBefore);
            File.Exists(existing + ".merging.tmp").Should().BeFalse();
            File.Exists(existing + ".bak").Should().BeFalse();
        }
        finally
        {
            File.Delete(existing);
            if (File.Exists(existing + ".merging.tmp"))
                File.Delete(existing + ".merging.tmp");
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task Merge_preserves_existing_file_when_cancelled()
    {
        var d1 = "2021-07-24T13:49:13.0000000+00:00";
        var d2 = "2021-07-25T10:00:00.0000000+00:00";
        var existing = await WriteAsync(Header + Row(d1, "existing"));
        var existingBefore = await File.ReadAllTextAsync(existing);
        var fresh = await WriteAsync(Header + Row(d2, "fresh"));
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            Snowflake.FromDate(DateTimeOffset.Parse(d1)),
            null,
            true,
            1,
            false
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            var act = async () =>
                await CsvExportMerger.MergeAsync(existing, fresh, cutoff, cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            (await File.ReadAllTextAsync(existing)).Should().Be(existingBefore);
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
        }
    }

    [Fact]
    public async Task It_appends_new_rows_skipping_the_header_and_boundary_rows()
    {
        var d1 = "2021-07-19T13:34:18.0000000+00:00";
        var d2 = "2021-07-24T13:49:13.0000000+00:00";
        var d3 = "2021-07-25T10:00:00.0000000+00:00";
        var existing = await WriteAsync(Header + Row(d1, "a") + Row(d2, "b"));
        var fresh = await WriteAsync(Header + Row(d2, "b") + Row(d3, "c"));
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            Snowflake.FromDate(DateTimeOffset.Parse(d2)),
            null,
            true,
            2,
            false
        );
        try
        {
            var added = await CsvExportMerger.MergeAsync(existing, fresh, cutoff);
            added.Should().Be(1);

            var lines = (await File.ReadAllLinesAsync(existing)).Where(l => l.Length > 0).ToArray();
            lines[0].Should().StartWith("AuthorID,");
            lines.Count(l => l.StartsWith("AuthorID,")).Should().Be(1);
            lines.Last().Should().Contain("2021-07-25");
            // The atomic-replace .bak is a transient crash-safety net, cleaned up on success.
            File.Exists(existing + ".bak").Should().BeFalse();
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task It_appends_same_timestamp_rows_with_greater_message_ids()
    {
        var d1 = "2021-07-24T13:49:13.0000000+00:00";
        var existing = await WriteAsync(HeaderWithMessageId + Row(1002, d1, "existing"));
        var fresh = await WriteAsync(
            HeaderWithMessageId
                + Row(1002, d1, "duplicate")
                + Row(1003, d1, "same timestamp newer")
                + Row(1004, "2021-07-25T10:00:00.0000000+00:00", "later")
        );
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(1002),
            null,
            true,
            1,
            true
        );

        try
        {
            var added = await CsvExportMerger.MergeAsync(existing, fresh, cutoff);

            added.Should().Be(2);
            var text = await File.ReadAllTextAsync(existing);
            text.Should().NotContain("duplicate");
            text.Should().Contain("same timestamp newer");
            text.Should().Contain("later");
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task It_separates_appended_rows_when_existing_file_has_no_trailing_newline()
    {
        var d1 = "2021-07-24T13:49:13.0000000+00:00";
        var d2 = "2021-07-25T10:00:00.0000000+00:00";
        var existing = await WriteAsync((Header + Row(d1, "existing")).TrimEnd('\r', '\n'));
        var fresh = await WriteAsync(Header + Row(d2, "fresh"));
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            Snowflake.FromDate(DateTimeOffset.Parse(d1)),
            null,
            true,
            1,
            false
        );

        try
        {
            var added = await CsvExportMerger.MergeAsync(existing, fresh, cutoff);

            added.Should().Be(1);
            var text = await File.ReadAllTextAsync(existing);
            text.Should().Contain("\"existing\",\"\",\"\"\r\n\"5\",\"A\"");
            File.ReadAllLines(existing).Should().HaveCount(3);
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }
}
