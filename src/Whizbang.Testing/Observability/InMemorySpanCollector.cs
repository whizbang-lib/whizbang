using System.Collections.Concurrent;
using System.Diagnostics;

namespace Whizbang.Testing.Observability;

/// <summary>
/// Collects OpenTelemetry spans emitted during test execution for assertions.
/// </summary>
/// <remarks>
/// <para>
/// This collector uses <see cref="ActivityListener"/> to capture all spans from
/// specified activity sources. Spans are stored in a thread-safe collection and
/// can be queried after test execution.
/// </para>
/// <para>
/// <strong>Usage:</strong>
/// <code>
/// using var collector = new InMemorySpanCollector("Whizbang.Tracing");
///
/// // Execute test code that emits spans
/// await fixture.SendCommandAsync(new ReseedSystemCommand());
///
/// // Assert on captured spans
/// var tree = collector.BuildTree();
/// tree.AssertHasChild("Lifecycle PreDistributeDetached");
/// </code>
/// </para>
/// </remarks>
public sealed class InMemorySpanCollector : IDisposable {
  private readonly ActivityListener _listener;
  private readonly ConcurrentBag<CapturedSpan> _spans = [];
  private readonly HashSet<string> _sourceNames;
  private bool _disposed;

  /// <summary>
  /// Creates a new span collector that listens to specified activity sources.
  /// </summary>
  /// <param name="activitySourceNames">
  /// Names of activity sources to listen to (e.g., "Whizbang.Tracing").
  /// If empty, listens to all sources.
  /// </param>
  public InMemorySpanCollector(params string[] activitySourceNames) {
    _sourceNames = activitySourceNames.Length > 0
      ? [.. activitySourceNames]
      : [];

    _listener = new ActivityListener {
      ShouldListenTo = source => _sourceNames.Count == 0 || _sourceNames.Contains(source.Name),
      Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded,
      ActivityStopped = _onActivityStopped
    };

    ActivitySource.AddActivityListener(_listener);
  }

  private void _onActivityStopped(Activity activity) {
    _spans.Add(CapturedSpan.From(activity));
  }

  /// <summary>
  /// All captured spans in the order they completed.
  /// </summary>
  public IReadOnlyList<CapturedSpan> Spans => [.. _spans];

  /// <summary>
  /// Number of captured spans.
  /// </summary>
  public int Count => _spans.Count;

  /// <summary>
  /// Gets spans matching a predicate.
  /// </summary>
  /// <param name="predicate">Filter predicate.</param>
  /// <returns>Matching spans.</returns>
  public IEnumerable<CapturedSpan> Where(Func<CapturedSpan, bool> predicate) =>
    _spans.Where(predicate);

  /// <summary>
  /// Gets spans with names starting with a prefix.
  /// </summary>
  /// <param name="prefix">Name prefix to match.</param>
  /// <returns>Matching spans.</returns>
  public IEnumerable<CapturedSpan> WithNamePrefix(string prefix) =>
    _spans.Where(s => s.Name.StartsWith(prefix, StringComparison.Ordinal));

  /// <summary>
  /// Gets spans with names containing a substring.
  /// </summary>
  /// <param name="substring">Substring to find in span names.</param>
  /// <returns>Matching spans.</returns>
  public IEnumerable<CapturedSpan> WithNameContaining(string substring) =>
    _spans.Where(s => s.Name.Contains(substring, StringComparison.Ordinal));

  /// <summary>
  /// Gets the first span matching a predicate, or null.
  /// </summary>
  /// <param name="predicate">Filter predicate.</param>
  /// <returns>First matching span or null.</returns>
  public CapturedSpan? FirstOrDefault(Func<CapturedSpan, bool> predicate) =>
    _spans.FirstOrDefault(predicate);

  /// <summary>
  /// Gets all root spans (spans with no parent).
  /// </summary>
  /// <returns>Root spans.</returns>
  public IEnumerable<CapturedSpan> GetRoots() =>
    _spans.Where(s => s.IsRoot);

  /// <summary>
  /// Gets all spans belonging to a specific trace.
  /// </summary>
  /// <param name="traceId">The trace ID to filter by.</param>
  /// <returns>Spans in the trace.</returns>
  public IEnumerable<CapturedSpan> GetByTraceId(string traceId) =>
    _spans.Where(s => s.TraceId == traceId);

  /// <summary>
  /// Gets direct children of a parent span.
  /// </summary>
  /// <param name="parent">The parent span.</param>
  /// <returns>Child spans.</returns>
  public IEnumerable<CapturedSpan> GetChildren(CapturedSpan parent) =>
    _spans.Where(s => s.ParentSpanId == parent.SpanId && s.TraceId == parent.TraceId);

  /// <summary>
  /// Builds a hierarchical tree representation of all captured spans.
  /// </summary>
  /// <returns>
  /// A <see cref="TraceTree"/> containing all traces with proper parent-child relationships.
  /// </returns>
  /// <remarks>
  /// If there are multiple root spans (multiple traces), this returns a forest with
  /// multiple trees. Use <see cref="TraceTree.Traces"/> to iterate them.
  /// </remarks>
  public TraceTree BuildTree() {
    var spans = Spans;
    return TraceTree.Build(spans);
  }

  /// <summary>
  /// Clears all captured spans.
  /// </summary>
  public void Clear() {
    _spans.Clear();
  }

  /// <summary>
  /// Checks if any orphaned spans exist (spans with non-null ParentSpanId
  /// but no matching parent in the captured spans).
  /// </summary>
  /// <returns>True if all non-root spans have their parents captured.</returns>
  public bool HasOrphanedSpans() {
    var spanIds = _spans.Select(s => s.SpanId).ToHashSet();
    return _spans.Any(s => s.ParentSpanId is not null && !spanIds.Contains(s.ParentSpanId));
  }

  /// <summary>
  /// Gets all orphaned spans (spans referencing a parent that wasn't captured).
  /// </summary>
  /// <returns>Orphaned spans.</returns>
  public IEnumerable<CapturedSpan> GetOrphanedSpans() {
    var spanIds = _spans.Select(s => s.SpanId).ToHashSet();
    return _spans.Where(s => s.ParentSpanId is not null && !spanIds.Contains(s.ParentSpanId));
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!_disposed) {
      _listener.Dispose();
      _disposed = true;
    }
  }
}
