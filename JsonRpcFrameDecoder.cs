using System.Text.Json;

namespace FilesystemMcp;

/// <summary>
/// Classifies one already-decoded stdio frame. Syntax failures are parse errors.
/// A JSON array is a batch and is rejected whole. A valid notification (no id member)
/// is not an error. An id that is not a string or integer cannot be echoed.
/// </summary>
internal static class JsonRpcFrameDecoder
{
    private const int ParseErrorCode = -32700;
    private const int InvalidRequestCode = -32600;

    internal readonly record struct DecodeResult(JsonRpcRequest? Request, JsonRpcResponse? Failure);

    internal static DecodeResult Decode(string frame)
    {
        if (frame is null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frame);
        }
        catch (JsonException)
        {
            return Failure(ParseErrorCode, "Parse error", JsonRpcIds.Null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                // One rejection for the whole batch. Elements are not dispatched.
                return Failure(InvalidRequestCode, "Batch requests are not supported.", JsonRpcIds.Null);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure(InvalidRequestCode, "Invalid Request", JsonRpcIds.Null);
            }

            var hasId = root.TryGetProperty("id", out var idElement);
            var idAccepted = hasId && IsStringOrIntegerId(idElement);
            // Clone before the document is disposed. A C# null is not used for a determined id:
            // the element itself is what the response echoes, including a JSON null when the
            // id cannot be determined.
            var echoedId = idAccepted ? idElement.Clone() : JsonRpcIds.Null;

            var jsonrpcOk = TryGetString(root, "jsonrpc", out var jsonrpc) && jsonrpc == JsonRpcConstants.Version;
            var methodOk = TryGetString(root, "method", out var method) && !string.IsNullOrWhiteSpace(method);
            var hasParams = root.TryGetProperty("params", out var paramsElement);
            JsonElement? clonedParams = hasParams ? paramsElement.Clone() : null;

            if (!hasId)
            {
                if (jsonrpcOk && methodOk)
                {
                    return Message(jsonrpc!, method!, clonedParams, hasId: false, hasParams);
                }

                // Not a notification: there is no id to echo.
                return Failure(InvalidRequestCode, jsonrpcOk ? "Method is required" : "Invalid Request", JsonRpcIds.Null);
            }

            if (!idAccepted)
            {
                return Failure(InvalidRequestCode, "Request id must be a string or an integer.", JsonRpcIds.Null);
            }

            if (!jsonrpcOk)
            {
                return Failure(InvalidRequestCode, "Invalid Request", echoedId);
            }

            if (!methodOk)
            {
                return Failure(InvalidRequestCode, "Method is required", echoedId);
            }

            return Message(jsonrpc!, method!, clonedParams, hasId: true, hasParams, echoedId);
        }
    }

    /// <summary>
    /// MCP request ids are strings or integers. Fractional numbers, booleans, null, objects
    /// and arrays are not ids. The raw literal is what decides integer-ness, so <c>1.0</c>
    /// and <c>1e2</c> are rejected instead of being coerced.
    /// </summary>
    private static bool IsStringOrIntegerId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => true,
        JsonValueKind.Number => IsJsonInteger(id.GetRawText()),
        _ => false
    };

    private static bool IsJsonInteger(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        var index = 0;
        if (raw[0] == '-')
        {
            if (raw.Length == 1)
            {
                return false;
            }

            index = 1;
        }

        if (raw[index] == '0')
        {
            return raw.Length == index + 1;
        }

        for (; index < raw.Length; index++)
        {
            if (raw[index] < '0' || raw[index] > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetString(JsonElement owner, string name, out string? value)
    {
        if (owner.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String)
        {
            value = node.GetString();
            return true;
        }

        value = null;
        return false;
    }

    private static DecodeResult Message(
        string jsonrpc,
        string method,
        JsonElement? parameters,
        bool hasId,
        bool hasParams,
        JsonElement? id = null) =>
        new(new JsonRpcRequest(jsonrpc, id, method, parameters)
        {
            HasId = hasId,
            HasParams = hasParams
        }, null);

    private static DecodeResult Failure(int code, string message, JsonElement id) =>
        new(null, new JsonRpcResponse(
            JsonRpcConstants.Version,
            id,
            null,
            new JsonRpcError(code, message)));
}
