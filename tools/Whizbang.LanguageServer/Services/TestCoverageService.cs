namespace Whizbang.LanguageServer.Services;

/// <summary>
/// Wraps code-tests-map.json data for test coverage lookups.
/// Supports exact and partial (EndsWith) symbol matching plus reverse lookup.
/// </summary>
public sealed class TestCoverageService {
  private Dictionary<string, List<TestCoverageEntry>> _codeToTests = new();
  private Dictionary<string, List<string>> _testsToCode = new();

  /// <summary>
  /// Replaces all coverage data with the provided mapping.
  /// Builds both forward (code→tests) and reverse (test→code) indexes.
  /// </summary>
  public void SetData(IReadOnlyDictionary<string, IReadOnlyList<TestCoverageEntry>> codeToTests) {
    var forward = new Dictionary<string, List<TestCoverageEntry>>(codeToTests.Count);
    var reverse = new Dictionary<string, List<string>>();

    foreach (var (symbol, entries) in codeToTests) {
      var list = new List<TestCoverageEntry>(entries.Count);
      foreach (var entry in entries) {
        list.Add(entry);

        if (entry.TestClass is not null) {
          if (!reverse.TryGetValue(entry.TestClass, out var codeSymbols)) {
            codeSymbols = [];
            reverse[entry.TestClass] = codeSymbols;
          }
          if (!codeSymbols.Contains(symbol)) {
            codeSymbols.Add(symbol);
          }
        }
      }
      forward[symbol] = list;
    }

    _codeToTests = forward;
    _testsToCode = reverse;
  }

  /// <summary>
  /// Gets test entries for a symbol. Tries exact match first, then falls back to
  /// EndsWith partial matching (e.g., "IDispatcher" matches "Whizbang.Core.IDispatcher").
  /// </summary>
  public IReadOnlyList<TestCoverageEntry> GetTests(string symbolName) {
    // Exact match first
    if (_codeToTests.TryGetValue(symbolName, out var exact)) {
      return exact;
    }

    // Partial match: find keys ending with the symbol name (dot-separated boundary)
    foreach (var (key, value) in _codeToTests) {
      if (key.EndsWith($".{symbolName}", StringComparison.Ordinal)) {
        return value;
      }
    }

    return [];
  }

  /// <summary>
  /// Reverse lookup: given a test class name, returns the code symbols it tests.
  /// </summary>
  public IReadOnlyList<string> GetCodeForTest(string testClassName) {
    if (_testsToCode.TryGetValue(testClassName, out var symbols)) {
      return symbols;
    }
    return [];
  }

  /// <summary>
  /// Returns the total number of test entries across all symbols.
  /// </summary>
  public int GetTestCount() {
    var count = 0;
    foreach (var entries in _codeToTests.Values) {
      count += entries.Count;
    }
    return count;
  }

  /// <summary>
  /// Returns the list of all symbol names that have test coverage.
  /// </summary>
  public IReadOnlyList<string> GetTestedSymbols() {
    return [.. _codeToTests.Keys];
  }
}

/// <summary>
/// Internal representation of a single test coverage entry linking a code symbol to a test.
/// </summary>
public sealed record TestCoverageEntry {
  /// <summary>The test file path relative to the test project root.</summary>
  public required string TestFile { get; init; }

  /// <summary>The test method name.</summary>
  public required string TestMethod { get; init; }

  /// <summary>The test class name (used for reverse lookup).</summary>
  public string? TestClass { get; init; }

  /// <summary>How the link was established: "convention", "tag", etc.</summary>
  public string? LinkSource { get; init; }
}
