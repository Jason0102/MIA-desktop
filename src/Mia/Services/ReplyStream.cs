using System.Text;
using System.Text.Json;

namespace Mia.Services;

internal static class ReplyStream
{
    // Decode only the top-level user-facing string. Never expose routing JSON or tool instructions.
    internal static string MessagePrefix(string json)
    {
        int start = json.IndexOf('{');
        if (start < 0) return "";
        byte[] bytes = Encoding.UTF8.GetBytes(json[start..]);
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals("message")) continue;
                string tail = Encoding.UTF8.GetString(bytes.AsSpan((int)reader.BytesConsumed)).TrimStart();
                if (tail.Length == 0 || tail[0] != '"') return "";
                int end = 1;
                for (; end < tail.Length; end++)
                {
                    if (tail[end] == '"') break;
                    if (tail[end] != '\\') continue;
                    int escape = tail[(end + 1)..].StartsWith("u") ? 6 : 2;
                    if (end + escape > tail.Length) break;
                    end += escape - 1;
                }
                // A Unicode surrogate pair can be split between transport chunks.
                string fragment = tail[1..end];
                if (fragment.Length >= 6 && fragment[^6..].StartsWith("\\u", StringComparison.Ordinal)
                    && ushort.TryParse(fragment[^4..], System.Globalization.NumberStyles.HexNumber, null, out ushort code)
                    && char.IsHighSurrogate((char)code)) fragment = fragment[..^6];
                return JsonSerializer.Deserialize<string>('"' + fragment + '"') ?? "";
            }
        }
        catch (JsonException) { }
        return "";
    }
}
