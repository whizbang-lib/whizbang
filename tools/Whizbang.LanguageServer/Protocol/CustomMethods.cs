namespace Whizbang.LanguageServer.Protocol;

/// <summary>
/// String constants for custom LSP method names used by the Whizbang language server.
/// </summary>
public static class CustomMethods {
    // Client → Server requests
    public const string SearchDocs = "whizbang/searchDocs";
    public const string GetSymbolInfo = "whizbang/getSymbolInfo";
    public const string GetTestsForSymbol = "whizbang/getTestsForSymbol";
    public const string GenerateFlowDiagram = "whizbang/generateFlowDiagram";
    public const string GetStatus = "whizbang/getStatus";

    // Client → Server notifications (debug session)
    public const string DebugSessionPaused = "whizbang/debugSessionPaused";
    public const string DebugSessionResumed = "whizbang/debugSessionResumed";

    // Server → Client notifications
    public const string RegistryChanged = "whizbang/registryChanged";
    public const string DataLoaded = "whizbang/dataLoaded";
    public const string Log = "whizbang/log";
}
