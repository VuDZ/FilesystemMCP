using System.Text.Json;

namespace FilesystemMcp;

internal static class ToolArguments
{
    internal static readonly string[] PathPropertyNames = ["path", "filePath", "file_path"];

    /// <summary>
    /// FS-12: <c>additionalProperties: false</c> is enforced here. The published schema
    /// does not replace this check — a client that skips schema validation still cannot
    /// smuggle an unknown field.
    /// </summary>
    public static void RejectUnknownProperties(JsonElement arguments, params string[] allowed)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Arguments must be a JSON object.");
        }

        foreach (var property in arguments.EnumerateObject())
        {
            var known = false;
            foreach (var name in allowed)
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                throw new ArgumentException("Unknown argument: " + property.Name + ".");
            }
        }
    }

    public static string GetRequiredPath(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Arguments must be a JSON object.");
        }

        // Every present alias is validated. A wrong type, an empty string, or a
        // whitespace-only string is an argument error, not a value to skip in favour
        // of another alias: choosing silently is the defect FS-12 removes. The
        // whitespace set is the one PathPolicy.Resolve already rejects
        // (string.IsNullOrWhiteSpace); refusing it here is the same ArgumentException
        // and happens before any write. Two or more remaining aliases are legal only
        // when they are the same string.
        string? selected = null;
        foreach (var propertyName in PathPropertyNames)
        {
            if (!arguments.TryGetProperty(propertyName, out var node)
                || node.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            if (node.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("Argument " + propertyName + " must be a string.");
            }

            var value = node.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Argument " + propertyName + " must be a non-whitespace path.");
            }

            if (selected is null)
            {
                selected = value;
                continue;
            }

            if (!string.Equals(selected, value, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Conflicting path aliases: path, filePath and file_path must be equal when more than one is set.");
            }
        }

        if (selected is null)
        {
            throw new ArgumentException("Missing required argument: path (or filePath / file_path).");
        }

        return selected;
    }

    public static bool TryGetPath(JsonElement arguments, out string path)
    {
        try
        {
            path = GetRequiredPath(arguments);
            return true;
        }
        catch (ArgumentException)
        {
            path = string.Empty;
            return false;
        }
    }
}
