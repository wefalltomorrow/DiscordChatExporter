using System.Collections.Generic;
using System.Linq;
using WebMarkupMin.Core;

namespace DiscordChatExporter.Cli.Tests.Infra;

// Produces HTML byte-shaped like a real DiscordChatExporter HTML export: each block minified
// separately with the SAME minifier HtmlMessageWriter uses, joined by newlines. The chatlog
// open/close are wrapped in wmm:ignore exactly like PreambleTemplate/PostambleTemplate, so the
// minifier preserves them verbatim (matching production).
public static class HtmlSample
{
    private static readonly HtmlMinifier Minifier = new();

    private static string Minify(string html) => Minifier.Minify(html, false).MinifiedContent;

    private const string PreambleHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>"
        + "<div class=\"preamble\"><div class=\"preamble__entry\">Guild</div></div>"
        + "<!--wmm:ignore--><div class=\"chatlog\"><!--/wmm:ignore-->";

    // A single message container. `pinned` adds the second class token that real exports render
    // for pinned messages — that makes `class` multi-token, so the minifier KEEPS the quotes
    // (verified against WebMarkupMin), unlike the single-token unquoted normal case. `content` is
    // the message body text (HTML-encoded by callers if needed; here it's emitted verbatim so a
    // test can plant text such as "Exported 7 message(s)" that must NOT be treated as the count).
    private static string ContainerHtml(long id, string content, bool pinned)
    {
        var cls = pinned
            ? "chatlog__message-container chatlog__message-container--pinned"
            : "chatlog__message-container";
        return $"<div id=\"chatlog__message-container-{id}\" class=\"{cls}\" data-message-id=\"{id}\">"
            + "<div class=\"chatlog__content chatlog__markdown\"><span class=\"chatlog__markdown-preserve\">"
            + content
            + "</span></div></div>";
    }

    private static string MessageGroupHtml(IEnumerable<long> ids) =>
        "<div class=\"chatlog__message-group\">"
        + string.Concat(ids.Select(id => ContainerHtml(id, "msg " + id, pinned: false)))
        + "</div>";

    // A group built from explicit (id, content, pinned) containers — lets a test control message
    // body text and the pinned flag per message.
    private static string MessageGroupHtml(
        IEnumerable<(long Id, string Content, bool Pinned)> msgs
    ) =>
        "<div class=\"chatlog__message-group\">"
        + string.Concat(msgs.Select(m => ContainerHtml(m.Id, m.Content, m.Pinned)))
        + "</div>";

    private static string PostambleHtml(long count) =>
        "<!--wmm:ignore--></div><!--/wmm:ignore-->"
        + "<div class=\"postamble\"><div class=\"postamble__entry\">Exported "
        + count.ToString("n0")
        + " message(s)</div></div></body></html>";

    public static string Export(params long[][] groups)
    {
        var blocks = new List<string> { Minify(PreambleHtml) };
        blocks.AddRange(groups.Select(g => Minify(MessageGroupHtml(g))));
        var total = groups.Sum(g => g.LongLength);
        blocks.Add(Minify(PostambleHtml(total)));
        return string.Join("\n", blocks);
    }

    // Like Export, but each group is described by explicit (id, content, pinned) message tuples so
    // a test can plant pinned containers (multi-token class → quotes preserved) and arbitrary body
    // text (e.g. content that looks like the postamble count). One inner array == one message-group.
    public static string ExportDetailed(params (long Id, string Content, bool Pinned)[][] groups)
    {
        var blocks = new List<string> { Minify(PreambleHtml) };
        blocks.AddRange(groups.Select(g => Minify(MessageGroupHtml(g))));
        var total = groups.Sum(g => g.LongLength);
        blocks.Add(Minify(PostambleHtml(total)));
        return string.Join("\n", blocks);
    }
}
