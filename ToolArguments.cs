using System.Text.Json;

namespace FilesystemMcp;

internal static class ToolArguments
{
    private static readonly string[] PathPropertyNames = ["path", "filePath", "file_path"];

    public static string GetRequiredPath(JsonElement arguments)
    {
        if (TryGetPath(arguments, out var path))
        {
            return path;
        }

        throw new ArgumentException("Missing required argument: path (or filePath / file_path).");
    }

    public static bool TryGetPath(JsonElement arguments, out string path)
    {
        foreach (var propertyName in PathPropertyNames)
        {
            if (!arguments.TryGetProperty(propertyName, out var node)
                || node.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || node.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = node.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                path = value;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }
}
