using System.Text.Json;

namespace M2DPlus.Core;

public static partial class Settings
{
    // Shared by the syntax-only Parse API and typed Resolve path. Keep addressing and value semantics in callers.
    // Quoted JSON strings permit names containing #, brackets, quotes or backslashes to round-trip.
    private static string ReadHeader(string line)
    {
        var end = -1; var quoted = false; var escaped = false;
        for (var i = 1; i < line.Length; i++)
        {
            if (escaped) { escaped = false; continue; }
            if (quoted && line[i] == '\\') { escaped = true; continue; }
            if (line[i] == '"') quoted = !quoted;
            if (line[i] == ']' && !quoted) { end = i; break; }
        }
        if (end < 2 || line[(end + 1)..].Trim() is var tail && tail.Length != 0 && !tail.StartsWith('#'))
            throw new FormatException("Invalid view header. Use [View name], [#index] or [*].");
        var text = line[1..end].Trim();
        try { text = text.StartsWith('"') ? JsonSerializer.Deserialize<string>(text) ?? "" : text; }
        catch (JsonException) { throw new FormatException("Invalid quoted view header."); }
        return text.Length > 0 ? text : throw new FormatException("Empty view header.");
    }

    private static string StripComment(string line)
    {
        var quoted = false; var escaped = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (escaped) { escaped = false; continue; }
            if (quoted && c == '\\') { escaped = true; continue; }
            // Only a leading value quote starts a JSON string; a distance's inch suffix stays unquoted.
            if (c == '"') quoted = !quoted && (quoted || line[..i].TrimEnd().EndsWith('='));
            if (c == '#' && !quoted) return line[..i];
        }
        return line;
    }
}
