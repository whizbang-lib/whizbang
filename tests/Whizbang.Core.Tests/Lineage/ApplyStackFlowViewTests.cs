#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lineage;

namespace Whizbang.Core.Tests.Lineage;

/// <summary>
/// The anchored flow view is a pure projection of path signatures — the Application Insights-style
/// before/after graph. These tests lock its semantics: first-occurrence anchoring, the run-length
/// <c>+</c> suffix matching the plain anchor, weight merging across signatures, radius bounds, and
/// the per-column long-tail collapse into <c>(others)</c>.
/// </summary>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
public class ApplyStackFlowViewTests {

  private static ApplyPathSignature _sig(long streams, params string[] path) =>
    new(path, streams, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

  [Test]
  public async Task Compute_SingleSignature_ProjectsNodesAndEdgesAroundTheAnchorAsync() {
    var graph = ApplyStackFlowView.Compute(
      [_sig(7, "Created", "Updated", "Closed")], "Updated", radius: 1);

    await Assert.That(graph.Nodes).IsEquivalentTo([
      new ApplyStackFlowNode(-1, "Created", 7),
      new ApplyStackFlowNode(0, "Updated", 7),
      new ApplyStackFlowNode(1, "Closed", 7),
    ]).Because("a ±1 view around the anchor is exactly the before/anchor/after columns");

    await Assert.That(graph.Edges).IsEquivalentTo([
      new ApplyStackFlowEdge(-1, "Created", "Updated", 7),
      new ApplyStackFlowEdge(0, "Updated", "Closed", 7),
    ]).Because("each adjacent pair inside the window is one weighted transition");
  }

  [Test]
  public async Task Compute_SignaturesWithoutTheAnchor_AreExcludedAsync() {
    var graph = ApplyStackFlowView.Compute(
      [_sig(5, "Created", "Closed"), _sig(3, "Created", "Updated", "Closed")], "Updated", radius: 1);

    await Assert.That(graph.Nodes.Sum(n => n.StreamCount)).IsEqualTo(9L)
      .Because("only the 3-stream signature contains the anchor; 3 nodes × 3 streams = 9");
  }

  [Test]
  public async Task Compute_AnchorMatchesTheRunLengthSuffixFormAsync() {
    var graph = ApplyStackFlowView.Compute(
      [_sig(4, "Created", "Updated+", "Closed")], "Updated", radius: 1);

    await Assert.That(graph.Nodes).Contains(new ApplyStackFlowNode(0, "Updated+", 4))
      .Because("anchoring on the plain type must also anchor its run-length-collapsed form, and the node keeps the collapsed label");
  }

  [Test]
  public async Task Compute_MergesWeightsAcrossSignaturesSharingCellsAsync() {
    var graph = ApplyStackFlowView.Compute(
      [
        _sig(10, "Created", "Updated", "Closed"),
        _sig(6, "Imported", "Updated", "Closed"),
      ], "Updated", radius: 1);

    await Assert.That(graph.Nodes).Contains(new ApplyStackFlowNode(0, "Updated", 16))
      .Because("both signatures pass through the anchor cell, so its weight is the sum");
    await Assert.That(graph.Edges).Contains(new ApplyStackFlowEdge(0, "Updated", "Closed", 16))
      .Because("both signatures take the same transition after the anchor");
    await Assert.That(graph.Nodes).Contains(new ApplyStackFlowNode(-1, "Created", 10));
    await Assert.That(graph.Nodes).Contains(new ApplyStackFlowNode(-1, "Imported", 6));
  }

  [Test]
  public async Task Compute_AnchorsAtTheFirstOccurrenceOnlyAsync() {
    var graph = ApplyStackFlowView.Compute(
      [_sig(2, "Updated", "Closed", "Updated", "Archived")], "Updated", radius: 1);

    await Assert.That(graph.Nodes).IsEquivalentTo([
      new ApplyStackFlowNode(0, "Updated", 2),
      new ApplyStackFlowNode(1, "Closed", 2),
    ]).Because("the path anchors once, at its first occurrence — the later occurrence is not a second anchoring");
  }

  [Test]
  public async Task Compute_RadiusBoundsTheWindowAndPathEdgesTruncateAsync() {
    var graph = ApplyStackFlowView.Compute(
      [_sig(1, "A", "B", "C", "D", "E")], "C", radius: 1);

    await Assert.That(graph.Nodes.Select(n => n.EventType)).IsEquivalentTo(["B", "C", "D"])
      .Because("radius 1 keeps exactly one step either side; A and E are outside the window");

    var atStart = ApplyStackFlowView.Compute([_sig(1, "A", "B")], "A", radius: 3);
    await Assert.That(atStart.Nodes.Select(n => n.EventType)).IsEquivalentTo(["A", "B"])
      .Because("an anchor at the path head has no before-column; the window truncates without error");
  }

  [Test]
  public async Task Compute_CollapsesTheLongTailPerColumnIntoOthersAsync() {
    var graph = ApplyStackFlowView.Compute(
      [
        _sig(50, "Big1", "Anchor"),
        _sig(40, "Big2", "Anchor"),
        _sig(3, "Small1", "Anchor"),
        _sig(2, "Small2", "Anchor"),
      ], "Anchor", radius: 1, maxBranchesPerColumn: 2);

    await Assert.That(graph.Nodes).Contains(new ApplyStackFlowNode(-1, ApplyStackFlowView.OTHERS, 5))
      .Because("beyond the branch cap, the tail merges into one (others) node carrying the summed weight");
    await Assert.That(graph.Nodes).Contains(new ApplyStackFlowNode(-1, "Big1", 50));
    await Assert.That(graph.Nodes).Contains(new ApplyStackFlowNode(-1, "Big2", 40));
    await Assert.That(graph.Nodes.Select(n => n.EventType)).DoesNotContain("Small1");

    await Assert.That(graph.Edges).Contains(new ApplyStackFlowEdge(-1, ApplyStackFlowView.OTHERS, "Anchor", 5))
      .Because("edges from collapsed nodes re-target (others) with their weights merged");
  }

  [Test]
  public async Task Compute_OrdersNodesByColumnThenWeightAsync() {
    var graph = ApplyStackFlowView.Compute(
      [
        _sig(6, "Heavy", "Anchor"),
        _sig(1, "Light", "Anchor"),
      ], "Anchor", radius: 1);

    await Assert.That(graph.Nodes.Select(n => (n.Offset, n.EventType)).ToList()).IsEquivalentTo([
      (-1, "Heavy"),
      (-1, "Light"),
      (0, "Anchor"),
    ]).Because("nodes order by offset column first, then heaviest first within the column — the render order");
  }

  [Test]
  public async Task Compute_GuardsItsArgumentsAsync() {
    await Assert.That(() => ApplyStackFlowView.Compute([], "A", radius: -1)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => ApplyStackFlowView.Compute([], "A", radius: 1, maxBranchesPerColumn: 0)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => ApplyStackFlowView.Compute([], "", radius: 1)).Throws<ArgumentException>();
  }
}
