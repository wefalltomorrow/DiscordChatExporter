using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Cli.Tests.Infra;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class HtmlExportMergerSpecs
{
    private static async Task<string> WriteAsync(string html, string suffix = "")
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"dce-htmlmerge-{Guid.NewGuid():N}{suffix}.html"
        );
        await File.WriteAllTextAsync(path, html);
        return path;
    }

    // Local copy of HtmlExportInspector.MessageIdRegex (which is internal to Core and not
    // visible to this test assembly — there is no InternalsVisibleTo). Same quote-tolerant
    // pattern, so it reads the same ids the merger does.
    private static long[] Ids(string html) =>
        Regex
            .Matches(html, "data-message-id=\"?(\\d+)\"?")
            .Select(m => long.Parse(m.Groups[1].Value))
            .ToArray();

    [Fact]
    public async Task It_splices_new_groups_recomputes_the_count_and_dedupes_overlap()
    {
        var existing = await WriteAsync(HtmlSample.Export([1000L, 2000L]));
        var fresh = await WriteAsync(HtmlSample.Export([2000L, 3000L]), "-new");
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(2000),
            null,
            true,
            2,
            true
        );
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);
            total.Should().Be(3);

            var merged = await File.ReadAllTextAsync(existing);
            Ids(merged).Should().Equal(1000L, 2000L, 3000L); // ascending, deduped (2000 not doubled)
            Regex.Matches(merged, "<div class=\"chatlog\">").Count.Should().Be(1);
            Regex.Matches(merged, "<div class=\"?postamble\"?>").Count.Should().Be(1);
            merged.Should().Contain("Exported 3 message(s)");
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
    public async Task Count_rewrite_does_not_use_ambient_culture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        var existing = await WriteAsync(
            HtmlSample.Export([.. Enumerable.Range(1, 1000).Select(i => (long)i)])
        );
        var fresh = await WriteAsync(HtmlSample.Export([1001L]), "-new");
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(1000),
            null,
            true,
            1000,
            true
        );
        try
        {
            await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);

            var merged = await File.ReadAllTextAsync(existing);
            merged.Should().Contain("Exported 1,001 message(s)");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task Merge_preserves_existing_file_when_cancelled()
    {
        var existing = await WriteAsync(HtmlSample.Export([1000L]));
        var existingBefore = await File.ReadAllTextAsync(existing);
        var fresh = await WriteAsync(HtmlSample.Export([2000L]), "-new");
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(1000),
            null,
            true,
            1,
            true
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            var act = async () =>
                await HtmlExportMerger.MergeAsync(existing, fresh, cutoff, cancellation.Token);

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
    public async Task Merge_preserves_existing_file_when_new_html_is_malformed()
    {
        var existing = await WriteAsync(HtmlSample.Export([1000L]));
        var existingBefore = await File.ReadAllTextAsync(existing);
        var fresh = await WriteAsync("<html><body>not an export</body></html>", "-new");
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(1000),
            null,
            true,
            1,
            true
        );

        try
        {
            var act = async () => await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);

            await act.Should().ThrowAsync<InvalidExportException>();
            (await File.ReadAllTextAsync(existing)).Should().Be(existingBefore);
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
        }
    }

    [Fact]
    public async Task It_appends_into_an_html_export_that_had_no_messages()
    {
        var existing = await WriteAsync(HtmlSample.Export());
        var fresh = await WriteAsync(HtmlSample.Export([1000L]), "-new");
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(1),
            null,
            true,
            0,
            true
        );
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);
            total.Should().Be(1);
            Ids(await File.ReadAllTextAsync(existing)).Should().Equal(1000L);
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    // Regression: the count rewrite must only touch the postamble. A user message whose body
    // literally reads "Exported 7 message(s)" precedes the postamble; before the fix the rewrite
    // grabbed the FIRST match (the user's text) and left the real postamble count stale.
    [Fact]
    public async Task It_does_not_rewrite_a_count_that_appears_in_message_content()
    {
        // Existing: one message (id 1000) whose CONTENT is the misleading "Exported 7 message(s)".
        var existing = await WriteAsync(
            HtmlSample.ExportDetailed([(1000L, "Exported 7 message(s)", false)])
        );
        // Fresh: adds id 2000. New total is 2 — distinct from both the in-content 7 and the old 1.
        var fresh = await WriteAsync(HtmlSample.ExportDetailed([(2000L, "hi", false)]), "-new");
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(1000),
            null,
            true,
            1,
            true
        );
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);
            total.Should().Be(2);

            var merged = await File.ReadAllTextAsync(existing);
            // The user's text is untouched...
            merged.Should().Contain("Exported 7 message(s)");
            // ...and the postamble shows the real recomputed total.
            merged.Should().Contain("Exported 2 message(s)");
            Ids(merged).Should().Equal(1000L, 2000L);
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
    public async Task It_does_not_drop_a_new_message_whose_id_is_forged_in_old_body_text()
    {
        var existing = await WriteAsync(
            HtmlSample.ExportDetailed([(100L, "data-message-id=200", false)])
        );
        var fresh = await WriteAsync(
            HtmlSample.ExportDetailed([(200L, "real new", false)]),
            "-new"
        );
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(100),
            null,
            true,
            1,
            true
        );
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);

            var merged = await File.ReadAllTextAsync(existing);
            merged.Should().Contain("id=chatlog__message-container-200");
            total.Should().Be(2);
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
    public async Task It_does_not_drop_a_new_message_whose_id_is_forged_in_old_body_link_attribute()
    {
        var existing = await WriteAsync(
            HtmlSample.ExportDetailed([
                (
                    100L,
                    "<a href=\"https://example.test/?class=chatlog__message-container&data-message-id=200\">link</a>",
                    false
                ),
            ])
        );
        var fresh = await WriteAsync(
            HtmlSample.ExportDetailed([(200L, "real new", false)]),
            "-new"
        );
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(100),
            null,
            true,
            1,
            true
        );
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);

            var merged = await File.ReadAllTextAsync(existing);
            merged.Should().Contain("id=chatlog__message-container-200");
            total.Should().Be(2);
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    // Regression: a pinned message renders multi-token `class="chatlog__message-container
    // chatlog__message-container--pinned"`, which the minifier keeps QUOTED. The container-split
    // marker must be quote-tolerant or the pinned container in the overlap window isn't deduped,
    // producing a duplicate id that aborts the merge. The pinned 2000 must live in the FRESH export
    // (the slice being split/deduped) for this to bite.
    [Fact]
    public async Task It_does_not_drop_a_new_message_whose_id_is_forged_in_old_body_div_attribute_value()
    {
        var existing = await WriteAsync(
            HtmlSample.ExportDetailed([
                (
                    100L,
                    "<div class=chatlog__sticker title=\"x class=chatlog__message-container data-message-id=200\"></div>",
                    false
                ),
            ])
        );
        var fresh = await WriteAsync(
            HtmlSample.ExportDetailed([(200L, "real new", false)]),
            "-new"
        );
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(100),
            null,
            true,
            1,
            true
        );
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);

            var merged = await File.ReadAllTextAsync(existing);
            merged.Should().Contain("id=chatlog__message-container-200");
            total.Should().Be(2);
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
    public async Task It_does_not_drop_a_new_message_whose_id_is_forged_in_old_nested_body_container()
    {
        var existing = await WriteAsync(
            HtmlSample.ExportDetailed([
                (
                    100L,
                    "<div class=chatlog__sticker><div class=chatlog__message-container data-message-id=200></div></div>",
                    false
                ),
            ])
        );
        var fresh = await WriteAsync(
            HtmlSample.ExportDetailed([(200L, "real new", false)]),
            "-new"
        );
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(100),
            null,
            true,
            1,
            true
        );
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);

            var merged = await File.ReadAllTextAsync(existing);
            merged.Should().Contain("id=chatlog__message-container-200");
            total.Should().Be(2);
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
    public async Task It_does_not_leave_orphaned_html_when_overlap_body_contains_group_marker_attribute_value()
    {
        var existing = await WriteAsync(HtmlSample.Export([200L]));
        var fresh = await WriteAsync(
            HtmlSample.ExportDetailed([
                (
                    200L,
                    "<div class=chatlog__sticker title=\"x class=chatlog__message-group\"></div>",
                    false
                ),
            ]),
            "-new"
        );
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(200),
            null,
            true,
            1,
            true
        );
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);

            var merged = await File.ReadAllTextAsync(existing);
            total.Should().Be(1);
            merged.Should().NotContain("chatlog__sticker");
            merged.Should().Contain("Exported 1 message(s)");
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
    public async Task It_dedupes_a_pinned_message_in_the_overlap_window()
    {
        var existing = await WriteAsync(HtmlSample.Export([1000L, 2000L]));
        // Fresh overlaps 2000 (pinned this time) and adds 3000.
        var fresh = await WriteAsync(
            HtmlSample.ExportDetailed([(2000L, "pinned msg", true), (3000L, "new msg", false)]),
            "-new"
        );
        // Sanity: the fresh export really does keep the pinned class quoted (else the test is moot).
        (await File.ReadAllTextAsync(fresh))
            .Should()
            .Contain("class=\"chatlog__message-container chatlog__message-container--pinned\"");

        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(2000),
            null,
            true,
            2,
            true
        );
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);
            total.Should().Be(3);

            var merged = await File.ReadAllTextAsync(existing);
            Ids(merged).Should().Equal(1000L, 2000L, 3000L); // 2000 deduped, no duplicate
            merged.Should().Contain("Exported 3 message(s)");
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
