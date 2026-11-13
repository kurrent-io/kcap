namespace Capacitor.Cli.Ide;

/// <summary>JSON-RPC error codes the stdio session host answers with. Values are never reused.</summary>
internal static class IdeErrorCodes {
    public const int UnsupportedProtocol = -32001;
    public const int NotInitialized      = -32002;
    public const int ParseError          = -32700;
    public const int InvalidRequest      = -32600;
    public const int MethodNotFound      = -32601;
    public const int InvalidParams       = -32602;
    public const int InternalError       = -32603;
}
