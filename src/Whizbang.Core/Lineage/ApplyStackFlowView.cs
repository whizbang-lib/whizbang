namespace Whizbang.Core.Lineage;

/// <summary>One node of the anchored flow graph: an event type at a signed offset from the anchor.</summary>
/// <param name="Offset">Steps from the anchor: negative = before, 0 = the anchor column, positive = after.</param>
/// <param name="EventType">The collapsed path element at this offset (may carry the run-length <c>+</c> suffix, or be the <c>(others)</c> collapse node).</param>
/// <param name="StreamCount">Total streams whose path passes through this (offset, type) cell.</param>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Core.Tests/Lineage/ApplyStackFlowViewTests.cs</tests>
public sealed record ApplyStackFlowNode(int Offset, string EventType, long StreamCount);

/// <summary>One weighted edge between adjacent offset columns of the anchored flow graph.</summary>
/// <param name="FromOffset">The source column; the target column is always <c>FromOffset + 1</c>.</param>
/// <param name="FromEventType">The source node's event type.</param>
/// <param name="ToEventType">The target node's event type.</param>
/// <param name="StreamCount">Total streams whose path takes this transition at this offset.</param>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Core.Tests/Lineage/ApplyStackFlowViewTests.cs</tests>
public sealed record ApplyStackFlowEdge(int FromOffset, string FromEventType, string ToEventType, long StreamCount);

/// <summary>
/// The anchored ±N flow graph: pick an anchor event type, see the weighted paths N steps either
/// side of it — the Application Insights-style before/after view, computed as a pure projection
/// of the path signatures.
/// </summary>
/// <param name="AnchorEventType">The anchor the view is centered on.</param>
/// <param name="Nodes">Cells of the view, ordered by (offset, weight desc, type).</param>
/// <param name="Edges">Transitions between adjacent columns, ordered by (offset, weight desc, from, to).</param>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Core.Tests/Lineage/ApplyStackFlowViewTests.cs</tests>
public sealed record ApplyStackFlowGraph(
  string AnchorEventType,
  IReadOnlyList<ApplyStackFlowNode> Nodes,
  IReadOnlyList<ApplyStackFlowEdge> Edges);

/// <summary>
/// Computes the anchored flow view from path signatures — a pure transform, no I/O, usable
/// identically by every serving surface and by the VS Code extension's offline-cache mode.
/// </summary>
/// <remarks>
/// <para>
/// Each signature containing the anchor contributes its <see cref="ApplyPathSignature.StreamCount"/>
/// to every (offset, type) cell within the radius, anchored at the <b>first</b> occurrence of the
/// anchor in its path. The anchor matches a path element that equals it exactly or equals it with
/// the run-length <c>+</c> suffix, so anchoring on <c>StatusUpdated</c> also anchors
/// <c>StatusUpdated+</c> columns.
/// </para>
/// <para>
/// The long tail collapses per column: beyond <c>maxBranchesPerColumn</c> distinct types in one
/// offset column, the heaviest keep their identity and the rest merge into a single
/// <see cref="OTHERS"/> node; edges touching merged nodes re-target it with their weights summed.
/// </para>
/// </remarks>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Core.Tests/Lineage/ApplyStackFlowViewTests.cs</tests>
public static class ApplyStackFlowView {
  /// <summary>The label of the per-column long-tail collapse node.</summary>
  public const string OTHERS = "(others)";

  /// <summary>Computes the anchored ±<paramref name="radius"/> flow view over <paramref name="signatures"/>.</summary>
  /// <param name="signatures">The path signatures to project (typically <see cref="IApplyStackQuery.GetPathSignaturesAsync"/>'s result).</param>
  /// <param name="anchorEventType">The event type to center on; signatures not containing it are excluded.</param>
  /// <param name="radius">How many steps either side of the anchor to include. Must be at least 0.</param>
  /// <param name="maxBranchesPerColumn">Distinct types kept per column before the long tail collapses into <see cref="OTHERS"/>. Must be at least 1.</param>
  public static ApplyStackFlowGraph Compute(
      IReadOnlyList<ApplyPathSignature> signatures,
      string anchorEventType,
      int radius,
      int maxBranchesPerColumn = 10) {
    ArgumentNullException.ThrowIfNull(signatures);
    ArgumentException.ThrowIfNullOrEmpty(anchorEventType);
    ArgumentOutOfRangeException.ThrowIfNegative(radius);
    ArgumentOutOfRangeException.ThrowIfLessThan(maxBranchesPerColumn, 1);

    var nodeWeights = new Dictionary<(int Offset, string EventType), long>();
    var edgeWeights = new Dictionary<(int FromOffset, string From, string To), long>();

    foreach (var signature in signatures) {
      var path = signature.Path;
      var anchorIndex = _firstAnchorIndex(path, anchorEventType);
      if (anchorIndex < 0) {
        continue;
      }

      var lo = Math.Max(0, anchorIndex - radius);
      var hi = Math.Min(path.Count - 1, anchorIndex + radius);
      for (var i = lo; i <= hi; i++) {
        var offset = i - anchorIndex;
        var key = (offset, path[i]);
        nodeWeights[key] = nodeWeights.GetValueOrDefault(key) + signature.StreamCount;
        if (i < hi) {
          var edgeKey = (offset, path[i], path[i + 1]);
          edgeWeights[edgeKey] = edgeWeights.GetValueOrDefault(edgeKey) + signature.StreamCount;
        }
      }
    }

    _collapseLongTail(nodeWeights, edgeWeights, maxBranchesPerColumn);

    var nodes = nodeWeights
      .Select(kv => new ApplyStackFlowNode(kv.Key.Offset, kv.Key.EventType, kv.Value))
      .OrderBy(n => n.Offset).ThenByDescending(n => n.StreamCount).ThenBy(n => n.EventType, StringComparer.Ordinal)
      .ToList();
    var edges = edgeWeights
      .Select(kv => new ApplyStackFlowEdge(kv.Key.FromOffset, kv.Key.From, kv.Key.To, kv.Value))
      .OrderBy(e => e.FromOffset).ThenByDescending(e => e.StreamCount)
      .ThenBy(e => e.FromEventType, StringComparer.Ordinal).ThenBy(e => e.ToEventType, StringComparer.Ordinal)
      .ToList();
    return new ApplyStackFlowGraph(anchorEventType, nodes, edges);
  }

  private static int _firstAnchorIndex(IReadOnlyList<string> path, string anchor) {
    for (var i = 0; i < path.Count; i++) {
      var element = path[i];
      if (element.Equals(anchor, StringComparison.Ordinal)
          || (element.Length == anchor.Length + 1
              && element[^1] == '+'
              && element.AsSpan(0, anchor.Length).SequenceEqual(anchor))) {
        return i;
      }
    }
    return -1;
  }

  private static void _collapseLongTail(
      Dictionary<(int Offset, string EventType), long> nodeWeights,
      Dictionary<(int FromOffset, string From, string To), long> edgeWeights,
      int maxBranchesPerColumn) {
    var collapsed = new HashSet<(int Offset, string EventType)>(
      nodeWeights.Keys
        .GroupBy(k => k.Offset)
        .SelectMany(column => column
          .OrderByDescending(k => nodeWeights[k]).ThenBy(k => k.EventType, StringComparer.Ordinal)
          .Skip(maxBranchesPerColumn)));
    if (collapsed.Count == 0) {
      return;
    }

    foreach (var key in collapsed) {
      var othersKey = (key.Offset, OTHERS);
      nodeWeights[othersKey] = nodeWeights.GetValueOrDefault(othersKey) + nodeWeights[key];
      nodeWeights.Remove(key);
    }

    foreach (var (key, weight) in edgeWeights.ToList()) {
      var from = collapsed.Contains((key.FromOffset, key.From)) ? OTHERS : key.From;
      var to = collapsed.Contains((key.FromOffset + 1, key.To)) ? OTHERS : key.To;
      if (from == key.From && to == key.To) {
        continue;
      }
      edgeWeights.Remove(key);
      var target = (key.FromOffset, from, to);
      edgeWeights[target] = edgeWeights.GetValueOrDefault(target) + weight;
    }
  }
}
