using FilesystemMcp.Tests.Infrastructure;

namespace FilesystemMcp.Tests;

[Trait("Status", "Baseline")]
public sealed class UnicodePathTests
{
    [Fact]
    public async Task UnicodePathsWorkForCreateReadListSearchAndReplaceOverStdio()
    {
        using var sandbox = new Sandbox();
        const string path = "Папка/файл 中文 😀.txt";
        await using var server = await ServerProcess.StartAsync(sandbox.Workspace);
        var create = await server.ToolAsync("create_file", new { path, content = "Привет old" });
        Assert.False(create.GetProperty("result").GetProperty("isError").GetBoolean());
        var read = ServerProcess.Payload(await server.ToolAsync("read_file", new { path }));
        Assert.Equal("Привет old", read.GetProperty("text").GetString());
        var listPayload = ServerProcess.Payload(await server.ToolAsync("list_directory", new { path = "Папка" }));
        var list = listPayload.ValueKind == System.Text.Json.JsonValueKind.Array ? listPayload : listPayload.GetProperty("entries");
        Assert.Equal("файл 中文 😀.txt", Assert.Single(list.EnumerateArray()).GetProperty("name").GetString());
        var searchPayload = ServerProcess.Payload(await server.ToolAsync("search", new { regex = "Привет" }));
        var matches = searchPayload.ValueKind == System.Text.Json.JsonValueKind.Array ? searchPayload : searchPayload.GetProperty("matches");
        Assert.Equal(path.Replace('/', Path.DirectorySeparatorChar), Assert.Single(matches.EnumerateArray()).GetProperty("path").GetString());
        var replaced = await server.ToolAsync("replace_in_file", new
        {
            path, target_snippet = "old", replacement_snippet = "новый", original_hash = read.GetProperty("sha256").GetString()
        });
        Assert.False(replaced.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("Привет новый", await File.ReadAllTextAsync(Path.Combine(sandbox.Workspace, path)));
    }
}
