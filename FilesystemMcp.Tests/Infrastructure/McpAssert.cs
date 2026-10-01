using System.Text.Json;

namespace FilesystemMcp.Tests.Infrastructure;

internal static class McpAssert
{
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
    }
}
