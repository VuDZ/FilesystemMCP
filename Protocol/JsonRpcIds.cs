using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesystemMcp;

/// <summary>
/// JSON-RPC id values that must survive <see cref="JsonIgnoreCondition.WhenWritingNull"/>.
/// A C# null on <see cref="JsonRpcResponse.Id"/> is omitted by the source-generated context;
/// a <see cref="JsonElement"/> whose kind is <see cref="JsonValueKind.Null"/> is a value, so
/// the member is written as <c>id:null</c>. Parse errors and any other failure whose id cannot
/// be determined use <see cref="Null"/>.
/// </summary>
internal static class JsonRpcIds
{
    internal static readonly JsonElement Null = CreateNull();

    internal static JsonElement OrNull(JsonElement? id) =>
        id is { } value && value.ValueKind != JsonValueKind.Undefined ? value : Null;

    private static JsonElement CreateNull()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }
}
