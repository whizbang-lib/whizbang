using System.Text.Json.Serialization;

namespace Whizbang.LanguageServer.Protocol;

// ─── Request Parameters ─────────────────────────────────────────────────────

public sealed record SearchDocsParams {
  [JsonPropertyName("query")]
  public required string Query { get; init; }
}

public sealed record GetSymbolInfoParams {
  [JsonPropertyName("symbol")]
  public required string Symbol { get; init; }
}

public sealed record GetTestsForSymbolParams {
  [JsonPropertyName("symbol")]
  public required string Symbol { get; init; }
}

public sealed record GenerateFlowDiagramParams {
  [JsonPropertyName("messageType")]
  public required string MessageType { get; init; }
}

// ─── Response Types ─────────────────────────────────────────────────────────

public sealed record SearchResult {
  [JsonPropertyName("title")]
  public required string Title { get; init; }

  [JsonPropertyName("category")]
  public required string Category { get; init; }

  [JsonPropertyName("slug")]
  public required string Slug { get; init; }

  [JsonPropertyName("preview")]
  public required string Preview { get; init; }

  [JsonPropertyName("score")]
  public required double Score { get; init; }
}

public sealed record SymbolInfo {
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  [JsonPropertyName("kind")]
  public required string Kind { get; init; }

  [JsonPropertyName("docsUrl")]
  public string? DocsUrl { get; init; }

  [JsonPropertyName("docsTitle")]
  public string? DocsTitle { get; init; }

  [JsonPropertyName("sourceFile")]
  public string? SourceFile { get; init; }

  [JsonPropertyName("sourceLine")]
  public int? SourceLine { get; init; }

  [JsonPropertyName("testCount")]
  public int TestCount { get; init; }

  [JsonPropertyName("isCommand")]
  public bool IsCommand { get; init; }

  [JsonPropertyName("isEvent")]
  public bool IsEvent { get; init; }

  [JsonPropertyName("dispatcherCount")]
  public int DispatcherCount { get; init; }

  [JsonPropertyName("receptorCount")]
  public int ReceptorCount { get; init; }

  [JsonPropertyName("perspectiveCount")]
  public int PerspectiveCount { get; init; }
}

public sealed record TestEntry {
  [JsonPropertyName("testFile")]
  public required string TestFile { get; init; }

  [JsonPropertyName("testMethod")]
  public required string TestMethod { get; init; }

  [JsonPropertyName("testClass")]
  public string? TestClass { get; init; }

  [JsonPropertyName("linkSource")]
  public string? LinkSource { get; init; }
}

public sealed record FlowDiagramResult {
  [JsonPropertyName("mermaidCode")]
  public required string MermaidCode { get; init; }
}

public sealed record StatusInfo {
  [JsonPropertyName("messageCount")]
  public int MessageCount { get; init; }

  [JsonPropertyName("commandCount")]
  public int CommandCount { get; init; }

  [JsonPropertyName("eventCount")]
  public int EventCount { get; init; }

  [JsonPropertyName("typeDocCount")]
  public int TypeDocCount { get; init; }

  [JsonPropertyName("testCount")]
  public int TestCount { get; init; }

  [JsonPropertyName("cacheAgeMinutes")]
  public int CacheAgeMinutes { get; init; }

  [JsonPropertyName("serverUptime")]
  public string? ServerUptime { get; init; }

  [JsonPropertyName("isDebugPaused")]
  public bool IsDebugPaused { get; init; }
}

// ─── Notification Parameters ────────────────────────────────────────────────

public sealed record RegistryChangedNotification {
  [JsonPropertyName("messageCount")]
  public int MessageCount { get; init; }
}

public sealed record DataLoadedNotification {
  [JsonPropertyName("key")]
  public required string Key { get; init; }

  [JsonPropertyName("count")]
  public int Count { get; init; }
}

public sealed record LogNotification {
  [JsonPropertyName("level")]
  public required string Level { get; init; }

  [JsonPropertyName("message")]
  public required string Message { get; init; }
}
