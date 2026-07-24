using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Testing.Observability;

/// <summary>
/// Hierarchical tree representation of captured spans for structured assertions.
/// </summary>
/// <remarks>
/// <para>
/// A TraceTree represents one or more complete traces with proper parent-child
/// relationships. Each node contains a span and its children.
/// </para>
/// <para>
/// <strong>Fluent assertion API:</strong>
/// <code>
/// tree.AssertName("POST /graphql/{**slug}")
///     .AssertHasChild("Dispatch ReseedSystemCommand")
///     .Child("Dispatch ReseedSystemCommand")
///       .AssertHasChild("Lifecycle PreDistributeDetached")
///       .AssertChildCount(5);
/// </code>
/// </para>
/// </remarks>
public sealed class TraceTree {

  /// <summary>
  /// The span at this tree node, or null for the root container of multiple traces.
  /// </summary>
  public CapturedSpan? Span { get; }

  /// <summary>
  /// Child nodes in this tree.
  /// </summary>
  public IReadOnlyList<TraceTree> Children { get; }

  /// <summary>
  /// All traces captured (for multi-trace scenarios).
  /// If there's only one trace, this contains one element.
  /// </summary>
  public IReadOnlyList<TraceTree> Traces { get; }

  private TraceTree(CapturedSpan? span, IReadOnlyList<TraceTree> children) {
    Span = span;
    Children = children;
    Traces = span is null ? children : [this];
  }

  /// <summary>
  /// Builds a TraceTree from a collection of spans.
  /// </summary>
  /// <param name="spans">The spans to organize into a tree.</param>
  /// <returns>A TraceTree representing the span hierarchy.</returns>
  public static TraceTree Build(IReadOnlyList<CapturedSpan> spans) {
    if (spans.Count == 0) {
      return new TraceTree(null, []);
    }

    // Group spans by TraceId
    var byTrace = spans.GroupBy(s => s.TraceId).ToList();

    // Build a tree for each trace
    var trees = new List<TraceTree>();
    foreach (var traceGroup in byTrace) {
      var traceSpans = traceGroup.ToList();
      var spanById = traceSpans.ToDictionary(s => s.SpanId);

      // Find roots (spans with no parent or parent not in this trace)
      var roots = traceSpans
        .Where(s => s.ParentSpanId is null || !spanById.ContainsKey(s.ParentSpanId))
        .OrderBy(s => s.StartTime)
        .ToList();

      foreach (var root in roots) {
        trees.Add(_buildSubtree(root, spanById));
      }
    }

    // If single trace with single root, return it directly
    if (trees.Count == 1) {
      return trees[0];
    }

    // Multiple traces - return a container
    return new TraceTree(null, trees);
  }

  private static TraceTree _buildSubtree(CapturedSpan span, Dictionary<string, CapturedSpan> spanById) {
    var children = spanById.Values
      .Where(s => s.ParentSpanId == span.SpanId)
      .OrderBy(s => s.StartTime)
      .Select(child => _buildSubtree(child, spanById))
      .ToList();

    return new TraceTree(span, children);
  }

  // ============== Fluent Assertions ==============

  /// <summary>
  /// Asserts that this node's span has the expected name.
  /// </summary>
  /// <param name="expected">Expected span name.</param>
  /// <returns>This tree for chaining.</returns>
  /// <exception cref="TraceAssertionException">Thrown if name doesn't match.</exception>
  public TraceTree AssertName(string expected) {
    if (Span is null) {
      throw new TraceAssertionException($"Expected span name '{expected}' but this is a root container (no span).");
    }
    if (Span.Name != expected) {
      throw new TraceAssertionException($"Expected span name '{expected}' but was '{Span.Name}'.");
    }
    return this;
  }

  /// <summary>
  /// Asserts that this node's span name contains the expected substring.
  /// </summary>
  /// <param name="substring">Substring to find in span name.</param>
  /// <returns>This tree for chaining.</returns>
  /// <exception cref="TraceAssertionException">Thrown if substring not found.</exception>
  public TraceTree AssertNameContains(string substring) {
    if (Span is null) {
      throw new TraceAssertionException($"Expected span name containing '{substring}' but this is a root container.");
    }
    if (!Span.Name.Contains(substring, StringComparison.Ordinal)) {
      throw new TraceAssertionException($"Expected span name containing '{substring}' but was '{Span.Name}'.");
    }
    return this;
  }

  /// <summary>
  /// Asserts that this node has a child with the specified name.
  /// </summary>
  /// <param name="childName">Expected child span name.</param>
  /// <returns>This tree for chaining.</returns>
  /// <exception cref="TraceAssertionException">Thrown if child not found.</exception>
  public TraceTree AssertHasChild(string childName) {
    if (!Children.Any(c => c.Span?.Name == childName)) {
      var childNames = string.Join(", ", Children.Select(c => $"'{c.Span?.Name}'"));
      throw new TraceAssertionException(
        $"Expected child span '{childName}' but found: [{childNames}]."
      );
    }
    return this;
  }

  /// <summary>
  /// Asserts that this node has a child with name containing the substring.
  /// </summary>
  /// <param name="substring">Substring to find in child span name.</param>
  /// <returns>This tree for chaining.</returns>
  /// <exception cref="TraceAssertionException">Thrown if no matching child found.</exception>
  public TraceTree AssertHasChildContaining(string substring) {
    if (!Children.Any(c => c.Span?.Name.Contains(substring, StringComparison.Ordinal) == true)) {
      var childNames = string.Join(", ", Children.Select(c => $"'{c.Span?.Name}'"));
      throw new TraceAssertionException(
        $"Expected child span containing '{substring}' but found: [{childNames}]."
      );
    }
    return this;
  }

  /// <summary>
  /// Asserts that this node has exactly the specified number of children.
  /// </summary>
  /// <param name="expected">Expected child count.</param>
  /// <returns>This tree for chaining.</returns>
  /// <exception cref="TraceAssertionException">Thrown if count doesn't match.</exception>
  public TraceTree AssertChildCount(int expected) {
    if (Children.Count != expected) {
      throw new TraceAssertionException(
        $"Expected {expected} children but found {Children.Count}."
      );
    }
    return this;
  }

  /// <summary>
  /// Asserts that this node has at least the specified number of children.
  /// </summary>
  /// <param name="minimum">Minimum child count.</param>
  /// <returns>This tree for chaining.</returns>
  /// <exception cref="TraceAssertionException">Thrown if count is less than minimum.</exception>
  public TraceTree AssertMinChildCount(int minimum) {
    if (Children.Count < minimum) {
      throw new TraceAssertionException(
        $"Expected at least {minimum} children but found {Children.Count}."
      );
    }
    return this;
  }

  /// <summary>
  /// Asserts that this node's span has a tag with the expected value.
  /// </summary>
  /// <param name="key">Tag key.</param>
  /// <param name="expected">Expected tag value.</param>
  /// <returns>This tree for chaining.</returns>
  /// <exception cref="TraceAssertionException">Thrown if tag missing or value doesn't match.</exception>
  public TraceTree AssertTag(string key, object expected) {
    if (Span is null) {
      throw new TraceAssertionException($"Expected tag '{key}' but this is a root container.");
    }
    if (!Span.Tags.TryGetValue(key, out var actual)) {
      throw new TraceAssertionException($"Expected tag '{key}' but it was not present.");
    }
    if (!Equals(actual, expected)) {
      throw new TraceAssertionException(
        $"Expected tag '{key}' = '{expected}' but was '{actual}'."
      );
    }
    return this;
  }

  /// <summary>
  /// Asserts that this node's span has a tag with the specified key (any value).
  /// </summary>
  /// <param name="key">Tag key.</param>
  /// <returns>This tree for chaining.</returns>
  /// <exception cref="TraceAssertionException">Thrown if tag not present.</exception>
  public TraceTree AssertHasTag(string key) {
    if (Span is null) {
      throw new TraceAssertionException($"Expected tag '{key}' but this is a root container.");
    }
    if (!Span.Tags.ContainsKey(key)) {
      throw new TraceAssertionException($"Expected tag '{key}' but it was not present.");
    }
    return this;
  }

  /// <summary>
  /// Asserts that there are no orphaned spans in this tree.
  /// An orphaned span references a parent that doesn't exist.
  /// </summary>
  /// <returns>This tree for chaining.</returns>
  /// <exception cref="TraceAssertionException">Thrown if orphaned spans found.</exception>
  public TraceTree AssertNoOrphanedSpans() {
    var allSpans = GetAllSpans().ToList();
    var spanIds = allSpans.Where(s => s is not null).Select(s => s!.SpanId).ToHashSet();

    var orphaned = allSpans
      .Where(s => s?.ParentSpanId is not null && !spanIds.Contains(s.ParentSpanId))
      .ToList();

    if (orphaned.Count > 0) {
      var orphanNames = string.Join(", ", orphaned.Select(s => $"'{s?.Name}'"));
      throw new TraceAssertionException(
        $"Found {orphaned.Count} orphaned spans: [{orphanNames}]."
      );
    }
    return this;
  }

  // ============== Navigation ==============

  /// <summary>
  /// Gets a child node by index.
  /// </summary>
  /// <param name="index">Zero-based child index.</param>
  /// <returns>The child tree node.</returns>
  /// <exception cref="TraceAssertionException">Thrown if index out of range.</exception>
  public TraceTree Child(int index) {
    if (index < 0 || index >= Children.Count) {
      throw new TraceAssertionException(
        $"Child index {index} out of range. Have {Children.Count} children."
      );
    }
    return Children[index];
  }

  /// <summary>
  /// Gets the first child with the specified name.
  /// </summary>
  /// <param name="name">Child span name.</param>
  /// <returns>The child tree node.</returns>
  /// <exception cref="TraceAssertionException">Thrown if child not found.</exception>
  public TraceTree Child(string name) {
    var child = Children.FirstOrDefault(c => c.Span?.Name == name);
    if (child is null) {
      var childNames = string.Join(", ", Children.Select(c => $"'{c.Span?.Name}'"));
      throw new TraceAssertionException(
        $"Child '{name}' not found. Available: [{childNames}]."
      );
    }
    return child;
  }

  /// <summary>
  /// Gets the first child with name containing the substring.
  /// </summary>
  /// <param name="substring">Substring to find in child name.</param>
  /// <returns>The child tree node.</returns>
  /// <exception cref="TraceAssertionException">Thrown if no matching child found.</exception>
  public TraceTree ChildContaining(string substring) {
    var child = Children.FirstOrDefault(c =>
      c.Span?.Name.Contains(substring, StringComparison.Ordinal) == true);
    if (child is null) {
      var childNames = string.Join(", ", Children.Select(c => $"'{c.Span?.Name}'"));
      throw new TraceAssertionException(
        $"No child containing '{substring}'. Available: [{childNames}]."
      );
    }
    return child;
  }

  /// <summary>
  /// Gets all spans in this tree (depth-first traversal).
  /// </summary>
  /// <returns>All spans including this node and descendants.</returns>
  public IEnumerable<CapturedSpan?> GetAllSpans() {
    yield return Span;
    foreach (var child in Children) {
      foreach (var span in child.GetAllSpans()) {
        yield return span;
      }
    }
  }

  /// <summary>
  /// Counts total spans in this tree.
  /// </summary>
  public int TotalSpanCount => GetAllSpans().Count(s => s is not null);

  // ============== Serialization ==============

  /// <summary>
  /// Converts this tree to a JSON snapshot for baseline testing.
  /// Excludes volatile fields (TraceId, SpanId, Duration, StartTime).
  /// </summary>
  /// <returns>JSON string representation.</returns>
  public string ToSnapshot() {
    var snapshot = _toSnapshotModel();
    return JsonSerializer.Serialize(snapshot, TraceSnapshotJsonContext.Default.TraceSnapshotModel);
  }

  /// <summary>
  /// Creates a TraceTree from a JSON snapshot.
  /// </summary>
  /// <param name="json">JSON snapshot string.</param>
  /// <returns>A TraceTree representing the snapshot.</returns>
  public static TraceTree FromSnapshot(string json) {
    var snapshot = JsonSerializer.Deserialize(json, TraceSnapshotJsonContext.Default.TraceSnapshotModel)
      ?? throw new ArgumentException("Invalid snapshot JSON", nameof(json));
    return _fromSnapshotModel(snapshot);
  }

  private TraceSnapshotModel _toSnapshotModel() {
    return new TraceSnapshotModel {
      Name = Span?.Name,
      Kind = Span?.Kind.ToString(),
      Status = Span?.Status.ToString(),
      Tags = Span?.Tags
        .Where(kvp => !_isVolatileTag(kvp.Key))
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString()),
      Children = [.. Children.Select(c => c._toSnapshotModel())]
    };
  }

  private static TraceTree _fromSnapshotModel(TraceSnapshotModel model) {
    CapturedSpan? span = null;
    if (model.Name is not null) {
      span = new CapturedSpan {
        Name = model.Name,
        Kind = Enum.TryParse<System.Diagnostics.ActivityKind>(model.Kind, out var kind)
          ? kind
          : System.Diagnostics.ActivityKind.Internal,
        TraceId = "snapshot",
        SpanId = Guid.NewGuid().ToString("N")[..16],
        ParentSpanId = null,
        Duration = TimeSpan.Zero,
        Status = Enum.TryParse<System.Diagnostics.ActivityStatusCode>(model.Status, out var status)
          ? status
          : System.Diagnostics.ActivityStatusCode.Unset,
        Tags = model.Tags?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value)
          ?? [],
        Events = [],
        SourceName = "snapshot",
        StartTime = DateTimeOffset.MinValue
      };
    }

    var children = model.Children?.Select(_fromSnapshotModel).ToList()
      ?? [];

    return new TraceTree(span, children);
  }

  private static bool _isVolatileTag(string key) {
    // Tags that change between runs
    return key.StartsWith("otel.", StringComparison.Ordinal)
        || key == "thread.id"
        || key == "thread.name";
  }

  /// <summary>
  /// Returns a human-readable string representation of this tree.
  /// </summary>
  public override string ToString() {
    var sb = new StringBuilder();
    _appendToString(sb, 0);
    return sb.ToString();
  }

  private void _appendToString(StringBuilder sb, int indent) {
    var prefix = new string(' ', indent * 2);
    if (Span is not null) {
      sb.AppendLine(CultureInfo.InvariantCulture, $"{prefix}- {Span.Name} ({Span.Duration.TotalMilliseconds:F2}ms)");
    } else {
      sb.AppendLine(CultureInfo.InvariantCulture, $"{prefix}[Traces: {Children.Count}]");
    }
    foreach (var child in Children) {
      child._appendToString(sb, indent + 1);
    }
  }
}

/// <summary>
/// JSON-serializable model for trace snapshots.
/// </summary>
internal sealed class TraceSnapshotModel {
  public string? Name { get; set; }
  public string? Kind { get; set; }
  public string? Status { get; set; }
  public Dictionary<string, string?>? Tags { get; set; }
  public List<TraceSnapshotModel>? Children { get; set; }
}

/// <summary>
/// Source-generated JSON serialization context for trace snapshots.
/// Enables AOT-compatible JSON serialization.
/// </summary>
[JsonSourceGenerationOptions(
  WriteIndented = true,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TraceSnapshotModel))]
internal sealed partial class TraceSnapshotJsonContext : JsonSerializerContext;

/// <summary>
/// Exception thrown when a trace assertion fails.
/// </summary>
public sealed class TraceAssertionException : Exception {
  /// <summary>
  /// Creates a new TraceAssertionException with a default message.
  /// </summary>
  public TraceAssertionException() : base("Trace assertion failed.") { }

  /// <summary>
  /// Creates a new TraceAssertionException with the specified message.
  /// </summary>
  /// <param name="message">The assertion failure message.</param>
  public TraceAssertionException(string message) : base(message) { }

  /// <summary>
  /// Creates a new TraceAssertionException with the specified message and inner exception.
  /// </summary>
  /// <param name="message">The assertion failure message.</param>
  /// <param name="innerException">The inner exception.</param>
  public TraceAssertionException(string message, Exception innerException) : base(message, innerException) { }
}
