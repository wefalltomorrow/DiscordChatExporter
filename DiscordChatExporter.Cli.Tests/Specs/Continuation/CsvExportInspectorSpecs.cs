using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class CsvExportInspectorSpecs
{
    private static async Task<string> WriteAsync(string content, string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{name} [222] - {Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private const string LegacyHeader = "AuthorID,Author,Date,Content,Attachments,Reactions\r\n";
    private const string Header =
        "MessageID,AuthorID,Author,Date,Content,Attachments,Reactions\r\n";

    [Fact]
    public async Task I_can_read_the_cutoff_count_and_order_from_a_csv_export()
    {
        var body =
            "\"1001\",\"5\",\"A\",\"2021-07-19T13:34:18.0000000+00:00\",\"hi\",\"\",\"\"\r\n"
            + "\"1002\",\"5\",\"A\",\"2021-07-24T13:49:13.0000000+00:00\",\"bye, really\",\"\",\"\"\r\n";
        var path = await WriteAsync(Header + body, "Guild - general");
        try
        {
            var info = await CsvExportInspector.InspectAsync(path);
            info.ChannelId.Value.Should().Be(222UL);
            info.ExistingCount.Should().Be(2);
            info.IsChronological.Should().BeTrue();
            info.CutoffIsExact.Should().BeTrue();
            info.Cutoff.Value.Should().Be(1002UL);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_can_parse_rows_whose_content_contains_commas_quotes_and_newlines()
    {
        var body =
            "\"5\",\"A\",\"2021-07-19T13:34:18.0000000+00:00\",\"line1\nline2, with \"\"quote\"\"\",\"\",\"\"\r\n"
            + "\"5\",\"A\",\"2021-07-24T13:49:13.0000000+00:00\",\"ok\",\"\",\"\"\r\n";
        var path = await WriteAsync(LegacyHeader + body, "Guild - general");
        try
        {
            var info = await CsvExportInspector.InspectAsync(path);
            info.ExistingCount.Should().Be(2); // embedded newline did NOT create a phantom row
            info.CutoffIsExact.Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_cannot_continue_a_csv_with_no_data_rows()
    {
        var path = await WriteAsync(LegacyHeader, "Guild - general");
        try
        {
            var act = async () => await CsvExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_cannot_continue_a_csv_whose_filename_lacks_a_channel_id()
    {
        var path = Path.Combine(Path.GetTempPath(), $"renamed-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(
            path,
            LegacyHeader + "\"5\",\"A\",\"2021-07-24T13:49:13.0000000+00:00\",\"x\",\"\",\"\"\r\n"
        );
        try
        {
            var act = async () => await CsvExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_cannot_continue_a_csv_export_with_mixed_message_order()
    {
        var body =
            "\"5\",\"A\",\"2021-07-19T13:34:18.0000000+00:00\",\"first\",\"\",\"\"\r\n"
            + "\"5\",\"A\",\"2021-07-24T13:49:13.0000000+00:00\",\"second\",\"\",\"\"\r\n"
            + "\"5\",\"A\",\"2021-07-20T13:49:13.0000000+00:00\",\"third\",\"\",\"\"\r\n";
        var path = await WriteAsync(LegacyHeader + body, "Guild - general");
        try
        {
            var act = async () => await CsvExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_cannot_continue_a_before_bounded_csv_export_without_exact_bound_metadata()
    {
        var body = "\"5\",\"A\",\"2021-07-19T13:34:18.0000000+00:00\",\"first\",\"\",\"\"\r\n";
        var dir = Path.Combine(Path.GetTempPath(), $"DceCsvBeforeBound_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Guild - general [222] (2021-07-01 to 2021-07-31).csv");
        await File.WriteAllTextAsync(path, LegacyHeader + body);
        try
        {
            var act = async () => await CsvExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidExportException>();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
