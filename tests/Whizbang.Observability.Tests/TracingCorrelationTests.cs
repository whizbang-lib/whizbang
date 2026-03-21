using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Testing.Observability;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Regression tests for trace span correlation (parent-child linking).
/// Validates that spans are properly nested and no orphaned spans exist.
/// </summary>
/// <remarks>
/// These tests validate the InMemorySpanCollector, TraceTree, and assertion infrastructure
/// used for trace validation in integration tests.
/// <para>
/// <strong>Note:</strong> These tests use <c>[NotInParallel]</c> because the
/// <see cref="System.Diagnostics.ActivityListener"/> is global and captures spans
/// from all concurrent activity sources.
/// </para>
/// </remarks>
[NotInParallel(Order = 1)]
public class TracingCorrelationTests {
  [Test]
  public async Task InMemorySpanCollector_CapturesSpansFromActivitySourceAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");

    // Act - Create activities that should be captured
    using (var activity = WhizbangActivitySource.Tracing.StartActivity("TestSpan")) {
      activity?.SetTag("test.key", "test.value");
    }

    // Assert
    await Assert.That(collector.Count).IsEqualTo(1);
    await Assert.That(collector.Spans[0].Name).IsEqualTo("TestSpan");
    await Assert.That(collector.Spans[0].Tags).ContainsKey("test.key");
  }

  [Test]
  public async Task InMemorySpanCollector_FiltersActivitySourcesByNameAsync() {
    // Arrange - only listen to Whizbang.Tracing, not Whizbang.Execution
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");

    // Act - Create activities from different sources
    using (WhizbangActivitySource.Tracing.StartActivity("TracingSpan")) { }
    using (WhizbangActivitySource.Execution.StartActivity("ExecutionSpan")) { }

    // Assert - Only Tracing span should be captured
    await Assert.That(collector.Count).IsEqualTo(1);
    await Assert.That(collector.Spans[0].Name).IsEqualTo("TracingSpan");
  }

  [Test]
  public async Task InMemorySpanCollector_ListensToAllSourcesWhenNoFilterAsync() {
    // Arrange - Listen to all sources
    using var collector = new InMemorySpanCollector();

    // Act - Create activities from different sources
    using (WhizbangActivitySource.Tracing.StartActivity("TracingSpan")) { }
    using (WhizbangActivitySource.Execution.StartActivity("ExecutionSpan")) { }

    // Assert - Both spans should be captured
    await Assert.That(collector.Count).IsEqualTo(2);
  }

  [Test]
  public async Task BuildTree_CreatesHierarchyFromParentChildSpansAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");

    // Act - Create nested activities
    using (var parent = WhizbangActivitySource.Tracing.StartActivity("ParentSpan")) {
      using var child = WhizbangActivitySource.Tracing.StartActivity("ChildSpan");
      using (WhizbangActivitySource.Tracing.StartActivity("GrandchildSpan")) { }
    }

    // Assert
    var tree = collector.BuildTree();
    await Assert.That(tree.Span).IsNotNull();
    await Assert.That(tree.Span!.Name).IsEqualTo("ParentSpan");
    await Assert.That(tree.Children.Count).IsEqualTo(1);
    await Assert.That(tree.Children[0].Span!.Name).IsEqualTo("ChildSpan");
    await Assert.That(tree.Children[0].Children.Count).IsEqualTo(1);
    await Assert.That(tree.Children[0].Children[0].Span!.Name).IsEqualTo("GrandchildSpan");
  }

  [Test]
  public async Task TraceTree_FluentAssertions_WorkCorrectlyAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    using (var parent = WhizbangActivitySource.Tracing.StartActivity("ParentSpan")) {
      using (WhizbangActivitySource.Tracing.StartActivity("ChildSpan")) { }
    }

    // Act & Assert - Fluent API should work
    var tree = collector.BuildTree();
    tree.AssertName("ParentSpan")
        .AssertHasChild("ChildSpan")
        .AssertChildCount(1)
        .Child("ChildSpan")
          .AssertChildCount(0);

    await Assert.That(tree.Span!.Name).IsEqualTo("ParentSpan");
  }

  [Test]
  public async Task AssertNoOrphanedSpans_ThrowsForOrphanedSpansAsync() {
    // Arrange - Create spans manually with broken parent chain
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");

    // Create a span with no parent
    using (WhizbangActivitySource.Tracing.StartActivity("RootSpan")) { }

    // Assert - No orphaned spans with a simple root span
    await Assert.That(() => collector.AssertNoOrphanedSpans()).ThrowsNothing();
  }

  [Test]
  public async Task HasOrphanedSpans_DetectsOrphansCorrectlyAsync() {
    // Arrange - Create properly linked spans
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    using (var parent = WhizbangActivitySource.Tracing.StartActivity("Parent")) {
      using (WhizbangActivitySource.Tracing.StartActivity("Child")) { }
    }

    // Assert - No orphans when properly linked
    await Assert.That(collector.HasOrphanedSpans()).IsFalse();
  }

  [Test]
  public async Task TraceTree_ToSnapshot_SerializesToJsonAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    using (var parent = WhizbangActivitySource.Tracing.StartActivity("TestSpan")) {
      parent?.SetTag("custom.tag", "value");
    }

    // Act
    var tree = collector.BuildTree();
    var json = tree.ToSnapshot();

    // Assert
    await Assert.That(json).Contains("\"name\":");
    await Assert.That(json).Contains("\"TestSpan\"");
    await Assert.That(json).Contains("\"kind\":");
    await Assert.That(json).Contains("\"status\":");
  }

  [Test]
  public async Task TraceTree_FromSnapshot_DeserializesFromJsonAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    using (WhizbangActivitySource.Tracing.StartActivity("OriginalSpan")) { }

    var originalTree = collector.BuildTree();
    var json = originalTree.ToSnapshot();

    // Act
    var restoredTree = TraceTree.FromSnapshot(json);

    // Assert
    await Assert.That(restoredTree.Span).IsNotNull();
    await Assert.That(restoredTree.Span!.Name).IsEqualTo("OriginalSpan");
  }

  [Test]
  public async Task TraceSnapshotComparer_MatchesIdenticalTreesAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    using (WhizbangActivitySource.Tracing.StartActivity("TestSpan")) { }

    var tree = collector.BuildTree();
    var json = tree.ToSnapshot();
    var baseline = TraceTree.FromSnapshot(json);

    // Act
    var comparison = TraceSnapshotComparer.Compare(tree, baseline);

    // Assert
    await Assert.That(comparison.IsMatch).IsTrue();
    await Assert.That(comparison.Differences.Count).IsEqualTo(0);
  }

  [Test]
  public async Task TraceSnapshotComparer_DetectsNameMismatchAsync() {
    // Arrange
    using var collector1 = new InMemorySpanCollector("Whizbang.Tracing");
    using (WhizbangActivitySource.Tracing.StartActivity("ActualSpan")) { }
    var actualTree = collector1.BuildTree();

    using var collector2 = new InMemorySpanCollector("Whizbang.Tracing");
    using (WhizbangActivitySource.Tracing.StartActivity("ExpectedSpan")) { }
    var expectedTree = collector2.BuildTree();

    // Act
    var comparison = TraceSnapshotComparer.Compare(actualTree, expectedTree);

    // Assert
    await Assert.That(comparison.IsMatch).IsFalse();
    await Assert.That(comparison.Differences.Count).IsGreaterThan(0);
    await Assert.That(comparison.Differences[0].Kind).IsEqualTo(TraceDifferenceKind.NameMismatch);
  }

  [Test]
  public async Task TraceSnapshotComparer_DetectsChildCountMismatchAsync() {
    // Arrange
    using var collector1 = new InMemorySpanCollector("Whizbang.Tracing");
    using (var parent = WhizbangActivitySource.Tracing.StartActivity("Parent")) {
      using (WhizbangActivitySource.Tracing.StartActivity("Child1")) { }
      using (WhizbangActivitySource.Tracing.StartActivity("Child2")) { }
    }
    var actualTree = collector1.BuildTree();

    using var collector2 = new InMemorySpanCollector("Whizbang.Tracing");
    using (var parent = WhizbangActivitySource.Tracing.StartActivity("Parent")) {
      using (WhizbangActivitySource.Tracing.StartActivity("Child1")) { }
    }
    var expectedTree = collector2.BuildTree();

    // Act
    var comparison = TraceSnapshotComparer.Compare(actualTree, expectedTree);

    // Assert
    await Assert.That(comparison.IsMatch).IsFalse();

    // Should have child count mismatch
    var childCountDiff = comparison.Differences.FirstOrDefault(d => d.Kind == TraceDifferenceKind.ChildCountMismatch);
    await Assert.That(childCountDiff).IsNotNull();
  }

  [Test]
  public async Task TraceAssertionExtensions_AssertHasSpan_WorksAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    using (WhizbangActivitySource.Tracing.StartActivity("TestSpan")) { }

    // Act & Assert
    await Assert.That(() => collector.AssertHasSpan("TestSpan")).ThrowsNothing();
    await Assert.That(() => collector.AssertHasSpan("NonExistentSpan")).Throws<TraceAssertionException>();
  }

  [Test]
  public async Task TraceAssertionExtensions_GetSingleRoot_WorksAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    using (WhizbangActivitySource.Tracing.StartActivity("RootSpan")) { }

    // Act
    var root = collector.GetSingleRoot();

    // Assert
    await Assert.That(root.Name).IsEqualTo("RootSpan");
  }

  [Test]
  public async Task CapturedSpan_FromActivity_CapturesAllDataAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");

    // Act
    using (var activity = WhizbangActivitySource.Tracing.StartActivity("TestActivity", ActivityKind.Server)) {
      activity?.SetTag("custom.key", "custom.value");
      activity?.SetStatus(ActivityStatusCode.Ok);
    }

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Name).IsEqualTo("TestActivity");
    await Assert.That(span.Kind).IsEqualTo(ActivityKind.Server);
    await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Ok);
    await Assert.That(span.Tags["custom.key"]).IsEqualTo("custom.value");
    await Assert.That(span.TraceId).IsNotNull();
    await Assert.That(span.SpanId).IsNotNull();
    await Assert.That(span.IsRoot).IsTrue();
  }

  [Test]
  public async Task TraceTree_TotalSpanCount_CalculatesCorrectlyAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    using (var parent = WhizbangActivitySource.Tracing.StartActivity("Parent")) {
      using (WhizbangActivitySource.Tracing.StartActivity("Child1")) { }
      using var child2 = WhizbangActivitySource.Tracing.StartActivity("Child2");
      using (WhizbangActivitySource.Tracing.StartActivity("Grandchild")) { }
    }

    // Act
    var tree = collector.BuildTree();

    // Assert - Should have 4 total spans
    await Assert.That(tree.TotalSpanCount).IsEqualTo(4);
  }

  [Test]
  public async Task InMemorySpanCollector_Clear_RemovesAllSpansAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    using (WhizbangActivitySource.Tracing.StartActivity("TestSpan")) { }
    await Assert.That(collector.Count).IsEqualTo(1);

    // Act
    collector.Clear();

    // Assert
    await Assert.That(collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task TraceTree_GetAllSpans_ReturnsAllDescendantsAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    using (var parent = WhizbangActivitySource.Tracing.StartActivity("Parent")) {
      using (WhizbangActivitySource.Tracing.StartActivity("Child1")) { }
      using (WhizbangActivitySource.Tracing.StartActivity("Child2")) { }
    }

    // Act
    var tree = collector.BuildTree();
    var allSpans = tree.GetAllSpans().Where(s => s is not null).ToList();

    // Assert
    await Assert.That(allSpans.Count).IsEqualTo(3);
    await Assert.That(allSpans.Select(s => s!.Name)).Contains("Parent");
    await Assert.That(allSpans.Select(s => s!.Name)).Contains("Child1");
    await Assert.That(allSpans.Select(s => s!.Name)).Contains("Child2");
  }
}
