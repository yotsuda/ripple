using System.Text.Json;

namespace Ripple.Services;

/// <summary>
/// AOT-safe JSON helpers for the pipe protocol. Uses Utf8JsonWriter for writes and
/// JsonDocument.Parse for reads — no reflection-based JsonSerializer calls.
/// </summary>
internal static class PipeJson
{
    internal static byte[] BuildObjectBytes(Action<Utf8JsonWriter> writeFields)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            writeFields(w);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    internal static JsonElement BuildObjectElement(Action<Utf8JsonWriter> writeFields)
    {
        var bytes = BuildObjectBytes(writeFields);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    internal static JsonElement ParseElement(ReadOnlySpan<byte> bytes)
    {
        using var doc = JsonDocument.Parse(bytes.ToArray());
        return doc.RootElement.Clone();
    }

    internal static byte[] ElementToBytes(JsonElement element)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            element.WriteTo(w);
        }
        return ms.ToArray();
    }

    internal static void WriteStringOrNull(this Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteString(name, value);
    }
}
