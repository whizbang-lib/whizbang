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
  private const string NULL_PLACEHOLDER = "(null)";

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
        expected.Span?.Name ?? NULL_PLACEHOLDER,
        actual.Span?.Name ?? NULL_PLACEHOLDER));
    }

    // Compare span kind
    if (actual.Span?.Kind != expected.Span?.Kind) {
      differences.Add(new TraceDifference(
        path,
        TraceDifferenceKind.KindMismatch,
        expected.Span?.Kind.ToString() ?? NULL_PLACEHOLDER,
        actual.Span?.Kind.ToString() ?? NULL_PLACEHOLDER));
    }

    // Compare span status
    if (actual.Span?.Status != expected.Span?.Status) {
      differences.Add(new TraceDifference(
        path,
        TraceDifferenceKind.StatusMismatch,
        expected.Span?.Status.ToString() ?? NULL_PLACEHOLDER,
        actual.Span?.Status.ToString() ?? NULL_PLACEHOLDER));
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

    // Pair children by NAME first, then pair whatever is left by position.
    //
    // Name-first exists because spans emitted concurrently arrive in whatever order they finish,
    // so a detached stage and an inline stage can swap places between runs. Comparing purely by
    // position reported four differences for a trace that was entirely correct, and did so only
    // under load, which is the shape that gets a test rerun instead of fixed.
    //
    // Position-second exists because a RENAMED child has no name match, and reporting it as one
    // missing plus one unexpected child loses the more useful diagnosis: that this specific child
    // has the wrong name. Pairing the leftovers in order recovers it.
    //
    // Duplicates are handled by consuming each match, so two siblings with the same name still
    // require two actual siblings with that name.
    var unmatchedActual = new List<TraceTree>(actual.Children);
    var unmatchedExpected = new List<(TraceTree Node, string Path, string Name)>();

    for (var i = 0; i < expected.Children.Count; i++) {
      var expectedChild = expected.Children[i];
      var expectedName = expectedChild.Span?.Name ?? $"[{i}]";
      var childPath = string.IsNullOrEmpty(path) ? expectedName : $"{path}/{expectedName}";

      var matchIndex = unmatchedActual.FindIndex(
        c => string.Equals(c.Span?.Name, expectedChild.Span?.Name, StringComparison.Ordinal));

      if (matchIndex < 0) {
        unmatchedExpected.Add((expectedChild, childPath, expectedName));
        continue;
      }

      var actualChild = unmatchedActual[matchIndex];
      unmatchedActual.RemoveAt(matchIndex);
      _compareNodes(actualChild, expectedChild, childPath, differences);
    }

    // Leftovers, paired in order: these are renames, and comparing them yields the name mismatch.
    var paired = Math.Min(unmatchedExpected.Count, unmatchedActual.Count);
    for (var i = 0; i < paired; i++) {
      _compareNodes(unmatchedActual[i], unmatchedExpected[i].Node, unmatchedExpected[i].Path, differences);
    }

    for (var i = paired; i < unmatchedExpected.Count; i++) {
      differences.Add(new TraceDifference(
        unmatchedExpected[i].Path,
        TraceDifferenceKind.MissingChild,
        unmatchedExpected[i].Name,
        "(missing)"));
    }

    for (var i = paired; i < unmatchedActual.Count; i++) {
      var extraName = unmatchedActual[i].Span?.Name ?? "(unnamed)";
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
      ?? [];

    var expectedTags = expected.Span?.Tags
      .Where(kvp => !_isVolatileTag(kvp.Key))
      .ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString())
      ?? [];

    // Check for missing or different tags
    foreach (var (key, expectedValue) in expectedTags) {
      if (!actualTags.TryGetValue(key, out var actualValue)) {
        differences.Add(new TraceDifference(
          $"{path}/@{key}",
          TraceDifferenceKind.MissingTag,
          expectedValue ?? NULL_PLACEHOLDER,
          "(missing)"));
      } else if (actualValue != expectedValue) {
        differences.Add(new TraceDifference(
          $"{path}/@{key}",
          TraceDifferenceKind.TagValueMismatch,
          expectedValue ?? NULL_PLACEHOLDER,
          actualValue ?? NULL_PLACEHOLDER));
      }
    }

    // Check for extra tags
    foreach (var (key, actualValue) in actualTags) {
      if (!expectedTags.ContainsKey(key)) {
        differences.Add(new TraceDifference(
          $"{path}/@{key}",
          TraceDifferenceKind.ExtraTag,
          "(none)",
          actualValue ?? NULL_PLACEHOLDER));
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
