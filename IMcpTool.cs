using System.Text.Json;

namespace FilesystemMcp;

internal interface IMcpTool
{
    string Name { get; }
    string Description { get; }
    string InputSchemaJson { get; }

    Task<string> ExecuteAsync(JsonElement arguments);
}
