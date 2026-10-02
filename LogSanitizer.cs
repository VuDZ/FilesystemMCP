using System.Text;
using System.Text.Json;

namespace FilesystemMcp;

internal static class LogSanitizer
{
    internal const int DefaultMaxLength = 200;
    private const int ContentMaxLength = 80;
    private const int MaxPropertyNameLength = 60;
    private const int MaxOutputLength = 2000;

    /// <summary>
    /// Property names whose values are never logged, not even partially: a log line is a
    /// diagnostic, and truncating content would still be publishing a secret. The value is
    /// replaced by its length so an operator can still tell "payload was 25 chars".
    /// Forbidden by contract: content, text, snippets, bodies, data, messages and patterns.
    /// </summary>
    private static readonly HashSet<string> ContentPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "content",
        "text",
        "target_snippet",
        "replacement_snippet",
        "snippet",
        "body",
        "data",
        "message",
        "old_string",
        "new_string",
        "old_text",
        "new_text",
        "pattern",
        "regex"
    };

    public static string SanitizeForLog(JsonElement element, int maxDepth = 6)
    {
        var builder = new StringBuilder(256);
        AppendElement(element, builder, depth: 0, maxDepth);
        if (builder.Length > MaxOutputLength)
        {
            var suffix = "…[truncated, " + builder.Length + " chars]";
            var keep = Math.Max(0, MaxOutputLength - suffix.Length);
            var text = builder.ToString();
            return string.Concat(text.AsSpan(0, Math.Min(keep, text.Length)), suffix);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Bounded, single-line text shared by diagnostics and by client-visible error
    /// details: control characters are flattened so a value cannot forge extra log
    /// lines or JSON structure, and truncation is marked explicitly instead of
    /// silently editing the value.
    /// </summary>
    public static string SanitizeText(string? value, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var truncated = value.Length > maxLength;
        var limit = truncated ? maxLength : value.Length;
        var builder = new StringBuilder(limit + 24);
        for (var i = 0; i < limit; i++)
        {
            var ch = value[i];
            builder.Append(char.IsControl(ch) ? ' ' : ch);
        }

        if (truncated)
        {
            builder.Append("…[truncated, ");
            builder.Append(value.Length);
            builder.Append(" chars]");
        }

        return builder.ToString();
    }

    private static void AppendElement(JsonElement element, StringBuilder builder, int depth, int maxDepth)
    {
        if (depth > maxDepth)
        {
            builder.Append("\"<max depth>\"");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject())
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append('"');
                    builder.Append(EscapeJsonString(property.Name, MaxPropertyNameLength));
                    builder.Append("\":");

                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        AppendStringValue(property.Value.GetString() ?? string.Empty, property.Name, builder);
                    }
                    else if (ContentPropertyNames.Contains(property.Name))
                    {
                        // A non-string content carrier (array/object) is replaced as a whole.
                        AppendRedaction(property.Value, builder);
                    }
                    else
                    {
                        AppendElement(property.Value, builder, depth + 1, maxDepth);
                    }
                }

                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    AppendElement(item, builder, depth + 1, maxDepth);
                }

                builder.Append(']');
                break;

            case JsonValueKind.String:
                AppendStringValue(element.GetString() ?? string.Empty, propertyName: null, builder);
                break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                builder.Append(element.GetRawText());
                break;

            default:
                builder.Append("\"<unsupported>\"");
                break;
        }
    }

    private static void AppendStringValue(string value, string? propertyName, StringBuilder builder)
    {
        if (propertyName is not null && ContentPropertyNames.Contains(propertyName))
        {
            builder.Append("\"<redacted ").Append(value.Length).Append(" chars>\"");
            return;
        }

        var maxLength = DefaultMaxLength;
        builder.Append('"');
        if (value.Length <= maxLength)
        {
            builder.Append(EscapeJsonString(value.AsSpan()));
        }
        else
        {
            builder.Append(EscapeJsonString(value.AsSpan(0, maxLength)));
            builder.Append("…[truncated, ");
            builder.Append(value.Length);
            builder.Append(" chars]");
        }

        builder.Append('"');
    }

    private static void AppendRedaction(JsonElement element, StringBuilder builder)
    {
        var count = element.ValueKind switch
        {
            JsonValueKind.Array => element.GetArrayLength(),
            JsonValueKind.Object => CountProperties(element),
            _ => 0
        };

        builder.Append("\"<redacted ").Append(count).Append(" item(s)>\"");
    }

    private static int CountProperties(JsonElement element)
    {
        var count = 0;
        foreach (var _ in element.EnumerateObject())
        {
            count++;
        }

        return count;
    }

    private static string EscapeJsonString(string value) =>
        EscapeJsonString(value.AsSpan(), value.Length);

    private static string EscapeJsonString(ReadOnlySpan<char> value) =>
        EscapeJsonString(value, value.Length);

    private static string EscapeJsonString(ReadOnlySpan<char> value, int maxLength)
    {
        var limit = Math.Min(value.Length, maxLength);
        var builder = new StringBuilder(limit);
        for (var i = 0; i < limit; i++)
        {
            var ch = value[i];
            switch (ch)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        builder.Append("\\u");
                        builder.Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
