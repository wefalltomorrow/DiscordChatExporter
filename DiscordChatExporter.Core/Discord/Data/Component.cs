using System.Text.Json;
using JsonExtensions.Reading;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/components/reference
//
// Components form a recursive, heterogeneous tree whose set of node types and per-type fields
// Discord keeps extending. Projecting that onto a fixed model would mean silently dropping
// whatever the model doesn't know about yet, so the payload is preserved verbatim instead and
// only reshaped at write time. The kind is lifted out because it's what identifies a node.
public partial record Component(int Kind, JsonElement Json);

public partial record Component
{
    public static Component Parse(JsonElement json)
    {
        var kind = json.GetPropertyOrNull("type")?.GetInt32OrNull() ?? 0;

        // The element is detached from the response it came out of, so that it stays readable
        // for as long as the message does
        return new Component(kind, json.Clone());
    }
}
