// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lineage;

namespace Whizbang.Core.Tests.Lineage;

/// <summary>
/// Branch coverage for <see cref="ApplyStackFlowView"/>'s long-tail collapse on the TARGET side of
/// an edge: the sibling suite collapses a column before the anchor, so only edge sources were ever
/// re-targeted. Here the collapsed column is after the anchor, so edges must be re-pointed at the
/// <c>(others)</c> node with their weights summed.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Lineage/ApplyStackFlowView.cs</code-under-test>
public class ApplyStackFlowViewBranchCoverageTests {

  private static ApplyPathSignature _sig(long streams, params string[] path) =>
    new(path, streams, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

  [Test]
  public async Task Compute_LongTailAfterTheAnchor_RetargetsEdgesIntoOthersWithSummedWeightsAsync() {
    var graph = ApplyStackFlowView.Compute([
      _sig(5, "Anchor", "Heavy"),
      _sig(2, "Anchor", "Light"),
      _sig(1, "Anchor", "Lighter"),
    ], "Anchor", radius: 1, maxBranchesPerColumn: 1);

    await Assert.That(graph.Nodes).IsEquivalentTo([
      new ApplyStackFlowNode(0, "Anchor", 8),
      new ApplyStackFlowNode(1, "Heavy", 5),
      new ApplyStackFlowNode(1, ApplyStackFlowView.OTHERS, 3),
    ]).Because("with one branch kept per column, the two lighter successors merge into one (others) node");

    await Assert.That(graph.Edges).IsEquivalentTo([
      new ApplyStackFlowEdge(0, "Anchor", "Heavy", 5),
      new ApplyStackFlowEdge(0, "Anchor", ApplyStackFlowView.OTHERS, 3),
    ]).Because("an edge whose target collapsed is re-pointed at (others), and two such edges sum into one; the "
      + "kept edge is left as it was");
  }
}
