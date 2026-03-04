using System.Globalization;

namespace Whizbang.Testing.Observability;

/// <summary>
/// Compares trace snapshots for baseline testing.
/// </summary>
/// <remarks>
/// <para>
/// This comparer validates that actual trace output matches expected baselines.
/// Volatile fields (TraceId, SpanId, Duration, StartTime) are ignored during comparison.
/// </para>
/// <para>
/// <strong>Usage:</strong>
/// <code>
/// var actual = collector.BuildTree();
/// var expected = TraceTree.FromSnapshot(File.ReadAllText("baselines/test.json"));
/// var comparison = TraceSnapshotComparer.Compare(actual, expected);
/// Assert.That(comparison.IsMatch).IsTrue();
/// </code>
/// </para>
/// </remarks>
public static class TraceSnapshotComparer {
  /// <summary>
  /// Compares actual trace against expected baseline.
  /// Ignores volatile fields (TraceId, SpanId, Duration, StartTime).
  /// </summary>
  /// <param name="actual">The actual trace tree from test execution.</param>
  /// <param name="expected">The expected baseline trace tree.</param>
  /// <returns>Comparison result with any differences found.</returns>
  public static TraceComparison Compare(TraceTree actual, TraceTree expected) {
    ArgumentNullException.ThrowIfNull(actual);
    ArgumentNullException.ThrowIfNull(expected);

    var differences = new List<TraceDifference>();
    _compareNodes(actual, expected, "", differences);
    return new TraceComparison(differences.Count == 0, differences);
  }

  /// <summary>
  /// Generates a baseline snapshot from actual trace output.
  /// Run once to create expected.json, then commit to source control.
  /// </summary>
  /// <param name="actual">The actual trace tree to snapshot.</param>
  /// <returns>JSON string suitable for saving as a baseline file.</returns>
  public static string GenerateBaseline(TraceTree actual) {
    ArgumentNullException.ThrowIfNull(actual);
    return actual.ToSnapshot();
  }

  private static void _compareNodes(TraceTree actual, TraceTree expected, string path, List<TraceDifference> differences) {
    // Compare span names
    if (actual.Span?.Name != expected.Span?.Name) {
      differences.Add(new TraceDifference(
        path,
        TraceDifferenceKind.NameMismatch,
        expected.Span?.Name ?? "(null)",
        actual.Span?.Name ?? "(null)"));
    }

    // Compare span kind
    if (actual.Span?.Kind != expected.Span?.Kind) {
      differences.Add(new TraceDifference(
        path,
        TraceDifferenceKind.KindMismatch,
        expected.Span?.Kind.ToString() ?? "(null)",
        actual.Span?.Kind.ToString() ?? "(null)"));
    }

    // Compare span status
    if (actual.Span?.Status != expected.Span?.Status) {
      differences.Add(new TraceDifference(
        path,
        TraceDifferenceKind.StatusMismatch,
        expected.Span?.Status.ToString() ?? "(null)",
        actual.Span?.Status.ToString() ?? "(null)"));
    }

    // Compare tags (non-volatile only)
    _compareTags(actual, expected, path, differences);

    // Compare child count
    if (actual.Children.Count != expected.Children.Count) {
      differences.Add(new TraceDifference(
        path,
        TraceDifferenceKind.ChildCountMismatch,
        expected.Children.Count.ToString(CultureInfo.InvariantCulture),
        actual.Children.Count.ToString(CultureInfo.InvariantCulture)));
    }

    // Compare children by position
    var minChildren = Math.Min(actual.Children.Count, expected.Children.Count);
    for (var i = 0; i < minChildren; i++) {
      var childPath = string.IsNullOrEmpty(path)
        ? (expected.Children[i].Span?.Name ?? $"[{i}]")
        : $"{path}/{expected.Children[i].Span?.Name ?? $"[{i}]"}";
      _compareNodes(actual.Children[i], expected.Children[i], childPath, differences);
    }

    // Report missing children (in actual)
    for (var i = minChildren; i < expected.Children.Count; i++) {
      var missingName = expected.Children[i].Span?.Name ?? $"[{i}]";
      var childPath = string.IsNullOrEmpty(path) ? missingName : $"{path}/{missingName}";
      differences.Add(new TraceDifference(
        childPath,
        TraceDifferenceKind.MissingChild,
        missingName,
        "(missing)"));
    }

    // Report extra children (in actual)
    for (var i = minChildren; i < actual.Children.Count; i++) {
      var extraName = actual.Children[i].Span?.Name ?? $"[{i}]";
      var childPath = string.IsNullOrEmpty(path) ? extraName : $"{path}/{extraName}";
      differences.Add(new TraceDifference(
        childPath,
        TraceDifferenceKind.ExtraChild,
        "(none)",
        extraName));
    }
  }

  private static void _compareTags(TraceTree actual, TraceTree expected, string path, List<TraceDifference> differences) {
    if (actual.Span is null && expected.Span is null) {
      return;
    }

    var actualTags = actual.Span?.Tags
      .Where(kvp => !_isVolatileTag(kvp.Key))
      .ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString())
      ?? new Dictionary<string, string?>();

    var expectedTags = expected.Span?.Tags
      .Where(kvp => !_isVolatileTag(kvp.Key))
      .ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString())
      ?? new Dictionary<string, string?>();

    // Check for missing or different tags
    foreach (var (key, expectedValue) in expectedTags) {
      if (!actualTags.TryGetValue(key, out var actualValue)) {
        differences.Add(new TraceDifference(
          $"{path}/@{key}",
          TraceDifferenceKind.MissingTag,
          expectedValue ?? "(null)",
          "(missing)"));
      } else if (actualValue != expectedValue) {
        differences.Add(new TraceDifference(
          $"{path}/@{key}",
          TraceDifferenceKind.TagValueMismatch,
          expectedValue ?? "(null)",
          actualValue ?? "(null)"));
      }
    }

    // Check for extra tags
    foreach (var (key, actualValue) in actualTags) {
      if (!expectedTags.ContainsKey(key)) {
        differences.Add(new TraceDifference(
          $"{path}/@{key}",
          TraceDifferenceKind.ExtraTag,
          "(none)",
          actualValue ?? "(null)"));
      }
    }
  }

  private static bool _isVolatileTag(string key) {
    // Tags that change between runs
    return key.StartsWith("otel.", StringComparison.Ordinal)
        || key == "thread.id"
        || key == "thread.name";
  }
}

/// <summary>
/// Result of comparing actual trace against expected baseline.
/// </summary>
/// <param name="IsMatch">True if traces match (no differences).</param>
/// <param name="Differences">List of differences found.</param>
public sealed record TraceComparison(bool IsMatch, IReadOnlyList<TraceDifference> Differences) {
  /// <summary>
  /// Returns a human-readable summary of the comparison.
  /// </summary>
  public override string ToString() {
    if (IsMatch) {
      return "Traces match.";
    }

    var lines = new List<string> { $"Found {Differences.Count} difference(s):" };
    foreach (var diff in Differences) {
      lines.Add($"  [{diff.Kind}] at '{diff.Path}': expected '{diff.Expected}', actual '{diff.Actual}'");
    }
    return string.Join(Environment.NewLine, lines);
  }
}

/// <summary>
/// A single difference found between actual and expected traces.
/// </summary>
/// <param name="Path">XPath-like path to the difference location.</param>
/// <param name="Kind">The type of difference.</param>
/// <param name="Expected">Expected value from baseline.</param>
/// <param name="Actual">Actual value from test.</param>
public sealed record TraceDifference(string Path, TraceDifferenceKind Kind, string Expected, string Actual);

/// <summary>
/// Type of difference between actual and expected traces.
/// </summary>
public enum TraceDifferenceKind {
  /// <summary>Span names do not match.</summary>
  NameMismatch,

  /// <summary>Span kinds do not match.</summary>
  KindMismatch,

  /// <summary>Span status codes do not match.</summary>
  StatusMismatch,

  /// <summary>Child span counts do not match.</summary>
  ChildCountMismatch,

  /// <summary>Expected child span is missing.</summary>
  MissingChild,

  /// <summary>Extra child span in actual.</summary>
  ExtraChild,

  /// <summary>Expected tag is missing.</summary>
  MissingTag,

  /// <summary>Tag values do not match.</summary>
  TagValueMismatch,

  /// <summary>Extra tag in actual.</summary>
  ExtraTag
}
