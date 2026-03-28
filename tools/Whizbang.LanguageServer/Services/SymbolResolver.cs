using Whizbang.LanguageServer.Protocol;

namespace Whizbang.LanguageServer.Services;

/// <summary>
/// Data entry from the message registry (commands, events, and their wiring).
/// </summary>
public sealed record MessageRegistryEntry {
  public required string Type { get; init; }
  public bool IsCommand { get; init; }
  public bool IsEvent { get; init; }
  public string? FilePath { get; init; }
  public int LineNumber { get; init; }
  public string? DocsUrl { get; init; }
  public int DispatcherCount { get; init; }
  public int ReceptorCount { get; init; }
  public int PerspectiveCount { get; init; }
  public int TestCount { get; init; }
}

/// <summary>
/// Data entry from the VSCode feed (type documentation info).
/// </summary>
public sealed record VscodeFeedEntry {
  public required string Docs { get; init; }
  public required string Title { get; init; }
  public string? File { get; init; }
  public int Line { get; init; }
  public IReadOnlyList<string>? Tests { get; init; }
}

/// <summary>
/// Data entry from the code-docs map (symbol to source/docs mapping).
/// </summary>
public sealed record CodeDocsEntry {
  public required string File { get; init; }
  public int Line { get; init; }
  public required string Symbol { get; init; }
  public required string Docs { get; init; }
}

/// <summary>
/// Combines multiple data sources (message registry, VSCode feed, code-docs map)
/// to resolve a symbol name to its full info including docs URL, source file,
/// test count, and message registry data.
/// </summary>
public sealed class SymbolResolver {
  private readonly string _docsBaseUrl;

  private IReadOnlyList<MessageRegistryEntry> _registryData = [];
  private IReadOnlyDictionary<string, VscodeFeedEntry> _vscodeFeedData =
      new Dictionary<string, VscodeFeedEntry>();
  private IReadOnlyDictionary<string, CodeDocsEntry> _codeDocsMapData =
      new Dictionary<string, CodeDocsEntry>();

  public SymbolResolver(string docsBaseUrl) {
    _docsBaseUrl = docsBaseUrl.TrimEnd('/');
  }

  /// <summary>
  /// Sets the message registry data source.
  /// </summary>
  public void SetRegistryData(IReadOnlyList<MessageRegistryEntry> messages) {
    _registryData = messages;
  }

  /// <summary>
  /// Sets the VSCode feed data source.
  /// </summary>
  public void SetVscodeFeedData(IReadOnlyDictionary<string, VscodeFeedEntry> types) {
    _vscodeFeedData = types;
  }

  /// <summary>
  /// Sets the code-docs map data source.
  /// </summary>
  public void SetCodeDocsMapData(IReadOnlyDictionary<string, CodeDocsEntry> entries) {
    _codeDocsMapData = entries;
  }

  /// <summary>
  /// Resolves a symbol name to its full info by checking registry, VSCode feed,
  /// and code-docs map. Supports partial matching via EndsWith for registry entries.
  /// </summary>
  public SymbolInfo? Resolve(string symbolName) {
    // 1. Check registry (exact match first, then EndsWith for partial names)
    var registryEntry = _findRegistryEntry(symbolName);

    // 2. Check VSCode feed (exact match on key)
    _vscodeFeedData.TryGetValue(
        registryEntry?.Type ?? symbolName,
        out var feedEntry);

    // If exact match on original name fails and we have a registry entry, try original too
    if (feedEntry is null && registryEntry is not null) {
      _vscodeFeedData.TryGetValue(symbolName, out feedEntry);
    }

    // 3. Check code-docs map (exact match on key)
    _codeDocsMapData.TryGetValue(
        registryEntry?.Type ?? symbolName,
        out var docsEntry);

    if (docsEntry is null && registryEntry is not null) {
      _codeDocsMapData.TryGetValue(symbolName, out docsEntry);
    }

    // If nothing found in any source, return null
    if (registryEntry is null && feedEntry is null && docsEntry is null) {
      return null;
    }

    // Build merged result
    var name = registryEntry?.Type ?? symbolName;
    var kind = _determineKind(registryEntry, feedEntry);
    var docsUrl = _buildDocsUrl(registryEntry, feedEntry, docsEntry);
    var docsTitle = feedEntry?.Title;
    var sourceFile = _nonEmpty(registryEntry?.FilePath) ?? feedEntry?.File ?? docsEntry?.File;
    var sourceLine = _nonZero(registryEntry?.LineNumber) ?? _nonZero(feedEntry?.Line) ?? docsEntry?.Line;
    var testCount = _nonZero(registryEntry?.TestCount) ?? feedEntry?.Tests?.Count ?? 0;

    return new SymbolInfo {
      Name = name,
      Kind = kind,
      DocsUrl = docsUrl,
      DocsTitle = docsTitle,
      SourceFile = sourceFile,
      SourceLine = sourceLine == 0 ? null : sourceLine,
      TestCount = testCount,
      IsCommand = registryEntry?.IsCommand ?? false,
      IsEvent = registryEntry?.IsEvent ?? false,
      DispatcherCount = registryEntry?.DispatcherCount ?? 0,
      ReceptorCount = registryEntry?.ReceptorCount ?? 0,
      PerspectiveCount = registryEntry?.PerspectiveCount ?? 0
    };
  }

  /// <summary>
  /// Returns a deduplicated list of all known symbols from all data sources.
  /// </summary>
  public IReadOnlyList<string> GetAllSymbols() {
    var symbols = new HashSet<string>(StringComparer.Ordinal);

    foreach (var entry in _registryData) {
      symbols.Add(entry.Type);
    }

    foreach (var key in _vscodeFeedData.Keys) {
      symbols.Add(key);
    }

    foreach (var key in _codeDocsMapData.Keys) {
      symbols.Add(key);
    }

    return [.. symbols];
  }

  private MessageRegistryEntry? _findRegistryEntry(string symbolName) {
    // Exact match first
    foreach (var entry in _registryData) {
      if (string.Equals(entry.Type, symbolName, StringComparison.Ordinal)) {
        return entry;
      }
    }

    // EndsWith match for partial names (e.g., "CreateOrderCommand" matches "Whizbang.Core.CreateOrderCommand")
    foreach (var entry in _registryData) {
      if (entry.Type.EndsWith("." + symbolName, StringComparison.Ordinal)) {
        return entry;
      }
    }

    return null;
  }

  private static string _determineKind(MessageRegistryEntry? registry, VscodeFeedEntry? feed) {
    if (registry is not null) {
      if (registry.IsCommand) {
        return "command";
      }

      if (registry.IsEvent) {
        return "event";
      }

      return "message";
    }

    // Feed and code-docs entries are generic types
    return feed is not null ? "type" : "type";
  }

  private string? _buildDocsUrl(
      MessageRegistryEntry? registry,
      VscodeFeedEntry? feed,
      CodeDocsEntry? docs) {
    // Feed docs path takes priority, then code-docs map, then registry
    if (feed is not null) {
      return $"{_docsBaseUrl}/{feed.Docs}";
    }

    if (docs is not null) {
      return $"{_docsBaseUrl}/{docs.Docs}";
    }

    return registry?.DocsUrl;
  }

  private static int? _nonZero(int? value) {
    return value is null or 0 ? null : value;
  }

  private static string? _nonEmpty(string? value) {
    return string.IsNullOrEmpty(value) ? null : value;
  }
}
