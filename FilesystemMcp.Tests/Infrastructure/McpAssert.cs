using System.Text.Json;

namespace FilesystemMcp.Tests.Infrastructure;

internal static class McpAssert
{
    /// <summary>Fragments that only a leaked stack trace or exception type name produces.</summary>
    private static readonly string[] StackTraceMarkers =
        ["   at ", ".cs:line ", "System.IO.", "System.InvalidOperationException", "System.ArgumentException"];

    public static void ProtocolError(JsonElement response, int expectedCode)
    {
        Assert.True(response.TryGetProperty("error", out var error), "Expected protocol error, received: " + response);
        Assert.Equal(expectedCode, error.GetProperty("code").GetInt32());
    }

    public static void ToolError(JsonElement response, string expectedCode)
    {
        Assert.False(response.TryGetProperty("error", out _), "Expected operational result, received protocol error: " + response);
        Assert.True(response.GetProperty("result").GetProperty("isError").GetBoolean(), "Expected tool error, received success: " + response);
        var payload = ServerProcess.Payload(response);
        Assert.True(payload.TryGetProperty("code", out var code), "Missing machine-readable error code: " + payload);
        Assert.Equal(expectedCode, code.GetString());
        // The code is the API: it must stay a stable machine token, not a human sentence.
        Assert.Matches("^[a-z][a-z0-9_]*$", code.GetString()!);
        Assert.True(payload.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(message.GetString()),
            "Missing human-readable message: " + payload);
        Assert.True(payload.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object,
            "Missing bounded details object: " + payload);
        Assert.True(details.TryGetProperty("retryable", out var retryable)
            && retryable.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "Missing machine-readable retryable flag: " + payload);
    }

    /// <summary>Operational error plus the retryable semantics the code promises.</summary>
    public static void OperationalError(JsonElement response, string expectedCode, bool retryable)
    {
        ToolError(response, expectedCode);
        Assert.Equal(retryable, ErrorPayload(response).GetProperty("details").GetProperty("retryable").GetBoolean());
    }

    /// <summary>Parsed <c>{code,message,details?}</c> object of an operational error result.</summary>
    public static JsonElement ErrorPayload(JsonElement response) => ServerProcess.Payload(response);

    /// <summary>Success shape: a result without an error object and with <c>isError=false</c>.</summary>
    public static void Success(JsonElement response)
    {
        Assert.False(response.TryGetProperty("error", out _), "Expected success, received protocol error: " + response);
        Assert.False(response.GetProperty("result").GetProperty("isError").GetBoolean(), "Expected success, received tool error: " + response);
    }

    /// <summary>
    /// Fails when any string in the element carries one of the forbidden fragments.
    /// Tool payloads travel as JSON <em>text</em> inside <c>result.content[0].text</c>,
    /// where a Windows path is escaped as <c>C:\\dir\\file</c>; every string that parses
    /// as JSON is therefore decoded and inspected too, so a leaked absolute path cannot
    /// hide behind its backslash escapes.
    /// </summary>
    public static void NoSensitiveContent(JsonElement element, params string[] secrets)
    {
        var values = new List<string>();
        CollectStrings(element, values, depth: 0);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(values, value => value.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var marker in StackTraceMarkers)
        {
            Assert.DoesNotContain(values, value => value.Contains(marker, StringComparison.Ordinal));
        }
    }

    private static void CollectStrings(JsonElement element, List<string> values, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    values.Add(property.Name);
                    CollectStrings(property.Value, values, depth);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectStrings(item, values, depth);
                }

                break;
            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                values.Add(text);
                // A nested protocol payload: content[0].text is JSON text, not an object.
                if (depth < 4 && text.Length > 1 && (text[0] == '{' || text[0] == '['))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(text);
                        CollectStrings(document.RootElement, values, depth + 1);
                    }
                    catch (JsonException)
                    {
                        // Not JSON after all: the raw text is already collected.
                    }
                }

                break;
        }
    }
}
