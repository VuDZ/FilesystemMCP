namespace FilesystemMcp;

internal readonly record struct DecodeResult(JsonRpcRequest? Request, JsonRpcResponse? Failure);
