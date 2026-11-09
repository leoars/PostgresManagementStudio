using System.Text.Encodings.Web;
using System.Text.Json;

namespace PGMS.PostgreSql;

/// <summary>Query Options ▸ Results ▸ Grid ▸ Display JSON in text columns as Hyperlink: which cells are links and the text they open.</summary>
public static class ResultJson
{
    /// <summary>Types whose values can be JSON documents, by the name the driver reports. Arrays print as {…} but are not JSON, so they are left out.</summary>
    public static bool MayHoldJson(string? type)
    {
        if (type is null) return false;
        int modifier = type.IndexOf('(');
        return (modifier >= 0 ? type[..modifier] : type).Trim() is "json" or "jsonb" or "text" or "character varying" or "character";
    }

    /// <summary>A complete JSON object or array; the first and last characters are checked first so ordinary text costs nothing.</summary>
    public static bool IsDocument(string? text) => TryFormat(text, out _);

    /// <summary>Indented text of a JSON object or array. Values, number spelling and repeated keys are kept; only the layout changes.</summary>
    public static bool TryFormat(string? text, out string formatted)
    {
        formatted = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var span = text.AsSpan().Trim();
        if (!(span[0] == '{' && span[^1] == '}' || span[0] == '[' && span[^1] == ']')) return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                document.RootElement.WriteTo(writer);
            formatted = System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            return true;
        }
        catch (JsonException) { return false; }
    }
}
