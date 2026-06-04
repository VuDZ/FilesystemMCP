using System.Text;
using System.Text.Json;

namespace FilesystemMcp;

internal static class LogSanitizer
{
    private const int DefaultMaxLength = 200;
    private const int ContentMaxLength = 80;

    private static readonly HashSet<string> ContentPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "content",
        "text",
        "target_snippet",
        "replacement_snippet",
        "snippet",
        "body",
        "data",
        "message"
    };

    public static string SanitizeForLog(JsonElement element, int maxDepth = 6)
    {
        var builder = new StringBuilder(256);
        AppendElement(element, builder, depth: 0, maxDepth);
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
                    builder.Append(EscapeJsonString(property.Name));
                    builder.Append("\":");

                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        AppendTruncatedString(property.Value.GetString() ?? string.Empty, property.Name, builder);
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
                AppendTruncatedString(element.GetString() ?? string.Empty, propertyName: null, builder);
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

    private static void AppendTruncatedString(string value, string? propertyName, StringBuilder builder)
    {
        var maxLength = propertyName is not null && ContentPropertyNames.Contains(propertyName)
            ? ContentMaxLength
            : DefaultMaxLength;

        builder.Append('"');
        if (value.Length <= maxLength)
        {
            builder.Append(EscapeJsonString(value));
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

    private static string EscapeJsonString(string value) =>
        EscapeJsonString(value.AsSpan());

    private static string EscapeJsonString(ReadOnlySpan<char> value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
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
