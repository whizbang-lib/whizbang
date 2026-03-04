namespace Whizbang.Testing.Observability;

/// <summary>
/// Extension methods for trace assertions in TUnit tests.
/// </summary>
/// <remarks>
/// <para>
/// These extensions provide convenient assertion patterns for validating
/// OpenTelemetry trace output in integration tests.
/// </para>
/// <para>
/// <strong>Usage:</strong>
/// <code>
/// using var collector = new InMemorySpanCollector("Whizbang.Tracing");
/// await fixture.SendCommandAsync(new ReseedSystemCommand());
///
/// // Assert no orphaned spans (parent-child linking is correct)
/// collector.AssertNoOrphanedSpans();
///
/// // Assert trace structure matches baseline
/// await collector.AssertMatchesBaselineAsync("baselines/reseed.json");
/// </code>
/// </para>
/// </remarks>
public static class TraceAssertionExtensions {
  /// <summary>
  /// Asserts that the collector has captured spans.
  /// </summary>
  /// <param name="collector">The span collector.</param>
  /// <exception cref="TraceAssertionException">Thrown if no spans were captured.</exception>
  public static void AssertHasSpans(this InMemorySpanCollector collector) {
    ArgumentNullException.ThrowIfNull(collector);

    if (collector.Count == 0) {
      throw new TraceAssertionException("Expected at least one span to be captured, but found none.");
    }
  }

  /// <summary>
  /// Asserts that the collector has at least the specified number of spans.
  /// </summary>
  /// <param name="collector">The span collector.</param>
  /// <param name="minimum">Minimum expected span count.</param>
  /// <exception cref="TraceAssertionException">Thrown if span count is below minimum.</exception>
  public static void AssertMinSpanCount(this InMemorySpanCollector collector, int minimum) {
    ArgumentNullException.ThrowIfNull(collector);

    if (collector.Count < minimum) {
      throw new TraceAssertionException(
        $"Expected at least {minimum} spans, but found {collector.Count}.");
    }
  }

  /// <summary>
  /// Asserts that no orphaned spans exist (all spans have valid parents except roots).
  /// </summary>
  /// <param name="collector">The span collector.</param>
  /// <exception cref="TraceAssertionException">Thrown if orphaned spans exist.</exception>
  public static void AssertNoOrphanedSpans(this InMemorySpanCollector collector) {
    ArgumentNullException.ThrowIfNull(collector);

    if (collector.HasOrphanedSpans()) {
      var orphaned = collector.GetOrphanedSpans().ToList();
      var orphanedNames = string.Join(", ", orphaned.Select(s => $"'{s.Name}'"));
      throw new TraceAssertionException(
        $"Found {orphaned.Count} orphaned spans (spans referencing missing parents): [{orphanedNames}].");
    }
  }

  /// <summary>
  /// Asserts that a span with the given name exists.
  /// </summary>
  /// <param name="collector">The span collector.</param>
  /// <param name="spanName">The exact span name to find.</param>
  /// <exception cref="TraceAssertionException">Thrown if span not found.</exception>
  public static void AssertHasSpan(this InMemorySpanCollector collector, string spanName) {
    ArgumentNullException.ThrowIfNull(collector);
    ArgumentNullException.ThrowIfNull(spanName);

    if (collector.FirstOrDefault(s => s.Name == spanName) is null) {
      var availableNames = string.Join(", ", collector.Spans.Take(10).Select(s => $"'{s.Name}'"));
      var suffix = collector.Count > 10 ? $" (showing 10 of {collector.Count})" : "";
      throw new TraceAssertionException(
        $"Expected span '{spanName}' not found. Available spans: [{availableNames}]{suffix}.");
    }
  }

  /// <summary>
  /// Asserts that a span with name containing the substring exists.
  /// </summary>
  /// <param name="collector">The span collector.</param>
  /// <param name="substring">Substring to find in span names.</param>
  /// <exception cref="TraceAssertionException">Thrown if no matching span found.</exception>
  public static void AssertHasSpanContaining(this InMemorySpanCollector collector, string substring) {
    ArgumentNullException.ThrowIfNull(collector);
    ArgumentNullException.ThrowIfNull(substring);

    if (!collector.WithNameContaining(substring).Any()) {
      throw new TraceAssertionException(
        $"Expected span containing '{substring}' not found in {collector.Count} captured spans.");
    }
  }

  /// <summary>
  /// Asserts that the trace structure matches a baseline snapshot.
  /// </summary>
  /// <param name="collector">The span collector.</param>
  /// <param name="expectedJson">JSON baseline content.</param>
  /// <exception cref="TraceAssertionException">Thrown if traces don't match.</exception>
  public static void AssertMatchesBaseline(this InMemorySpanCollector collector, string expectedJson) {
    ArgumentNullException.ThrowIfNull(collector);
    ArgumentNullException.ThrowIfNull(expectedJson);

    var actual = collector.BuildTree();
    var expected = TraceTree.FromSnapshot(expectedJson);
    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    if (!comparison.IsMatch) {
      throw new TraceAssertionException(comparison.ToString());
    }
  }

  /// <summary>
  /// Asserts that the trace structure matches a baseline file.
  /// </summary>
  /// <param name="collector">The span collector.</param>
  /// <param name="baselinePath">Path to JSON baseline file.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="TraceAssertionException">Thrown if traces don't match.</exception>
  /// <exception cref="FileNotFoundException">Thrown if baseline file doesn't exist.</exception>
  public static async Task AssertMatchesBaselineFileAsync(
    this InMemorySpanCollector collector,
    string baselinePath,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(collector);
    ArgumentNullException.ThrowIfNull(baselinePath);

    if (!File.Exists(baselinePath)) {
      throw new FileNotFoundException(
        $"Baseline file not found: {baselinePath}. " +
        $"Generate it using: File.WriteAllText(\"{baselinePath}\", collector.BuildTree().ToSnapshot());",
        baselinePath);
    }

    var expectedJson = await File.ReadAllTextAsync(baselinePath, cancellationToken).ConfigureAwait(false);
    collector.AssertMatchesBaseline(expectedJson);
  }

  /// <summary>
  /// Saves the current trace as a baseline snapshot file.
  /// </summary>
  /// <param name="collector">The span collector.</param>
  /// <param name="baselinePath">Path to save baseline file.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public static async Task SaveBaselineAsync(
    this InMemorySpanCollector collector,
    string baselinePath,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(collector);
    ArgumentNullException.ThrowIfNull(baselinePath);

    var directory = Path.GetDirectoryName(baselinePath);
    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) {
      Directory.CreateDirectory(directory);
    }

    var json = TraceSnapshotComparer.GenerateBaseline(collector.BuildTree());
    await File.WriteAllTextAsync(baselinePath, json, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Gets a span by name, throwing if not found.
  /// </summary>
  /// <param name="collector">The span collector.</param>
  /// <param name="spanName">The exact span name.</param>
  /// <returns>The matching span.</returns>
  /// <exception cref="TraceAssertionException">Thrown if span not found.</exception>
  public static CapturedSpan GetSpan(this InMemorySpanCollector collector, string spanName) {
    ArgumentNullException.ThrowIfNull(collector);
    ArgumentNullException.ThrowIfNull(spanName);

    var span = collector.FirstOrDefault(s => s.Name == spanName);
    if (span is null) {
      throw new TraceAssertionException($"Span '{spanName}' not found.");
    }
    return span;
  }

  /// <summary>
  /// Gets the single root span, throwing if none or multiple exist.
  /// </summary>
  /// <param name="collector">The span collector.</param>
  /// <returns>The single root span.</returns>
  /// <exception cref="TraceAssertionException">Thrown if no root or multiple roots.</exception>
  public static CapturedSpan GetSingleRoot(this InMemorySpanCollector collector) {
    ArgumentNullException.ThrowIfNull(collector);

    var roots = collector.GetRoots().ToList();
    if (roots.Count == 0) {
      throw new TraceAssertionException("No root spans found.");
    }
    if (roots.Count > 1) {
      var rootNames = string.Join(", ", roots.Select(s => $"'{s.Name}'"));
      throw new TraceAssertionException(
        $"Expected single root span, but found {roots.Count}: [{rootNames}].");
    }
    return roots[0];
  }
}
