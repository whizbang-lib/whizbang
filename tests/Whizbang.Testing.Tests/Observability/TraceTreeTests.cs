using System.Diagnostics;
using Whizbang.Testing.Observability;
using Whizbang.Testing.Tests.TestSupport;

namespace Whizbang.Testing.Tests.Observability;

/// <summary>
/// Tests for <see cref="TraceTree"/> - span-tree building, fluent assertions,
/// navigation, and snapshot serialization.
/// </summary>
public class TraceTreeTests {
  private static DateTimeOffset _at(int seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

  private static TraceTree _singleRootTree() {
    var spans = new List<CapturedSpan> {
      SpanFactory.Create("root", spanId: "s-root", startTime: _at(0)),
      SpanFactory.Create("child-a", spanId: "s-a", parentSpanId: "s-root", startTime: _at(1)),
      SpanFactory.Create("child-b", spanId: "s-b", parentSpanId: "s-root", startTime: _at(2)),
      SpanFactory.Create("grandchild", spanId: "s-g", parentSpanId: "s-a", startTime: _at(3))
    };
    return TraceTree.Build(spans);
  }

  // ============== Build ==============

  [Test]
  public async Task Build_EmptySpans_ReturnsEmptyContainerAsync() {
    var tree = TraceTree.Build([]);

    await Assert.That(tree.Span).IsNull();
    await Assert.That(tree.Children.Count).IsEqualTo(0);
    await Assert.That(tree.Traces.Count).IsEqualTo(0);
    await Assert.That(tree.TotalSpanCount).IsEqualTo(0);
  }

  [Test]
  public async Task Build_SingleRootWithChildren_ReturnsRootDirectlyAsync() {
    var tree = _singleRootTree();

    await Assert.That(tree.Span!.Name).IsEqualTo("root");
    await Assert.That(tree.Children.Count).IsEqualTo(2);
    await Assert.That(tree.Traces.Count).IsEqualTo(1);
    await Assert.That(ReferenceEquals(tree.Traces[0], tree)).IsTrue();
    await Assert.That(tree.TotalSpanCount).IsEqualTo(4);
  }

  [Test]
  public async Task Build_ChildrenOrderedByStartTimeAsync() {
    var spans = new List<CapturedSpan> {
      SpanFactory.Create("root", spanId: "s-root", startTime: _at(0)),
      SpanFactory.Create("late", spanId: "s-late", parentSpanId: "s-root", startTime: _at(9)),
      SpanFactory.Create("early", spanId: "s-early", parentSpanId: "s-root", startTime: _at(1))
    };

    var tree = TraceTree.Build(spans);

    await Assert.That(tree.Children[0].Span!.Name).IsEqualTo("early");
    await Assert.That(tree.Children[1].Span!.Name).IsEqualTo("late");
  }

  [Test]
  public async Task Build_MultipleRootsInSameTrace_ReturnsContainerAsync() {
    var spans = new List<CapturedSpan> {
      SpanFactory.Create("root-1", spanId: "s-1", startTime: _at(0)),
      SpanFactory.Create("root-2", spanId: "s-2", startTime: _at(1))
    };

    var tree = TraceTree.Build(spans);

    await Assert.That(tree.Span).IsNull();
    await Assert.That(tree.Children.Count).IsEqualTo(2);
    await Assert.That(tree.Traces.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Build_MultipleTraces_ReturnsContainerWithOneTreePerTraceAsync() {
    var spans = new List<CapturedSpan> {
      SpanFactory.Create("trace-a-root", traceId: "trace-a", spanId: "s-1", startTime: _at(0)),
      SpanFactory.Create("trace-b-root", traceId: "trace-b", spanId: "s-2", startTime: _at(1))
    };

    var tree = TraceTree.Build(spans);

    await Assert.That(tree.Span).IsNull();
    await Assert.That(tree.Traces.Count).IsEqualTo(2);
    var traceNames = tree.Traces.Select(t => t.Span!.Name).ToList();
    await Assert.That(traceNames.Contains("trace-a-root")).IsTrue();
    await Assert.That(traceNames.Contains("trace-b-root")).IsTrue();
  }

  [Test]
  public async Task Build_OrphanSpan_ParentNotCaptured_IsTreatedAsRootAsync() {
    var spans = new List<CapturedSpan> {
      SpanFactory.Create("real-root", spanId: "s-root", startTime: _at(0)),
      SpanFactory.Create("orphan", spanId: "s-orphan", parentSpanId: "s-missing", startTime: _at(1))
    };

    var tree = TraceTree.Build(spans);

    // Both are roots of the same trace, so we get a container with two trees.
    await Assert.That(tree.Span).IsNull();
    await Assert.That(tree.Children.Count).IsEqualTo(2);
    await Assert.That(tree.Children.Select(c => c.Span!.Name).Contains("orphan")).IsTrue();
  }

  [Test]
  public async Task Build_DeepNesting_GetAllSpansTraversesDepthFirstAsync() {
    var spans = new List<CapturedSpan> {
      SpanFactory.Create("level-0", spanId: "s-0", startTime: _at(0)),
      SpanFactory.Create("level-1", spanId: "s-1", parentSpanId: "s-0", startTime: _at(1)),
      SpanFactory.Create("level-2", spanId: "s-2", parentSpanId: "s-1", startTime: _at(2)),
      SpanFactory.Create("level-3", spanId: "s-3", parentSpanId: "s-2", startTime: _at(3))
    };

    var tree = TraceTree.Build(spans);

    var names = tree.GetAllSpans().Select(s => s!.Name).ToList();
    await Assert.That(string.Join(",", names)).IsEqualTo("level-0,level-1,level-2,level-3");
    await Assert.That(tree.TotalSpanCount).IsEqualTo(4);
    await Assert.That(tree.Children[0].Children[0].Children[0].Span!.Name).IsEqualTo("level-3");
  }

  // ============== Fluent assertions ==============

  [Test]
  public async Task AssertName_Matching_ReturnsSameTreeForChainingAsync() {
    var tree = _singleRootTree();

    var result = tree.AssertName("root");

    await Assert.That(ReferenceEquals(result, tree)).IsTrue();
  }

  [Test]
  public async Task AssertName_Mismatch_ThrowsWithActualNameAsync() {
    var tree = _singleRootTree();

    var ex = Assert.Throws<TraceAssertionException>(() => tree.AssertName("wrong"));

    await Assert.That(ex!.Message).Contains("Expected span name 'wrong'");
    await Assert.That(ex.Message).Contains("'root'");
  }

  [Test]
  public async Task AssertName_OnRootContainer_ThrowsAsync() {
    var container = TraceTree.Build([]);

    var ex = Assert.Throws<TraceAssertionException>(() => container.AssertName("anything"));

    await Assert.That(ex!.Message).Contains("root container");
  }

  [Test]
  public async Task AssertNameContains_Matching_ReturnsTreeAsync() {
    var tree = _singleRootTree();

    var result = tree.AssertNameContains("oo");

    await Assert.That(ReferenceEquals(result, tree)).IsTrue();
  }

  [Test]
  public async Task AssertNameContains_NotFound_ThrowsAsync() {
    var tree = _singleRootTree();

    var ex = Assert.Throws<TraceAssertionException>(() => tree.AssertNameContains("zzz"));

    await Assert.That(ex!.Message).Contains("containing 'zzz'");
    await Assert.That(ex.Message).Contains("'root'");
  }

  [Test]
  public async Task AssertNameContains_OnRootContainer_ThrowsAsync() {
    var container = TraceTree.Build([]);

    var ex = Assert.Throws<TraceAssertionException>(() => container.AssertNameContains("x"));

    await Assert.That(ex!.Message).Contains("root container");
  }

  [Test]
  public async Task AssertHasChild_Existing_ReturnsTreeAsync() {
    var tree = _singleRootTree();

    var result = tree.AssertHasChild("child-a");

    await Assert.That(ReferenceEquals(result, tree)).IsTrue();
  }

  [Test]
  public async Task AssertHasChild_Missing_ThrowsListingAvailableChildrenAsync() {
    var tree = _singleRootTree();

    var ex = Assert.Throws<TraceAssertionException>(() => tree.AssertHasChild("nope"));

    await Assert.That(ex!.Message).Contains("Expected child span 'nope'");
    await Assert.That(ex.Message).Contains("'child-a'");
    await Assert.That(ex.Message).Contains("'child-b'");
  }

  [Test]
  public async Task AssertHasChildContaining_Existing_ReturnsTreeAsync() {
    var tree = _singleRootTree();

    var result = tree.AssertHasChildContaining("ild-b");

    await Assert.That(ReferenceEquals(result, tree)).IsTrue();
  }

  [Test]
  public async Task AssertHasChildContaining_Missing_ThrowsAsync() {
    var tree = _singleRootTree();

    var ex = Assert.Throws<TraceAssertionException>(() => tree.AssertHasChildContaining("zzz"));

    await Assert.That(ex!.Message).Contains("containing 'zzz'");
  }

  [Test]
  public async Task AssertChildCount_Correct_ReturnsTreeAsync() {
    var tree = _singleRootTree();

    var result = tree.AssertChildCount(2);

    await Assert.That(ReferenceEquals(result, tree)).IsTrue();
  }

  [Test]
  public async Task AssertChildCount_Wrong_ThrowsWithBothCountsAsync() {
    var tree = _singleRootTree();

    var ex = Assert.Throws<TraceAssertionException>(() => tree.AssertChildCount(5));

    await Assert.That(ex!.Message).Contains("Expected 5 children but found 2");
  }

  [Test]
  public async Task AssertMinChildCount_Satisfied_ReturnsTreeAsync() {
    var tree = _singleRootTree();

    var result = tree.AssertMinChildCount(2);

    await Assert.That(ReferenceEquals(result, tree)).IsTrue();
  }

  [Test]
  public async Task AssertMinChildCount_TooFew_ThrowsAsync() {
    var tree = _singleRootTree();

    var ex = Assert.Throws<TraceAssertionException>(() => tree.AssertMinChildCount(3));

    await Assert.That(ex!.Message).Contains("at least 3 children but found 2");
  }

  [Test]
  public async Task AssertTag_MatchingValue_ReturnsTreeAsync() {
    var span = SpanFactory.Create("tagged", tags: new Dictionary<string, object?> { ["env"] = "test" });
    var tree = TraceTree.Build([span]);

    var result = tree.AssertTag("env", "test");

    await Assert.That(ReferenceEquals(result, tree)).IsTrue();
  }

  [Test]
  public async Task AssertTag_MissingKey_ThrowsAsync() {
    var tree = _singleRootTree();

    var ex = Assert.Throws<TraceAssertionException>(() => tree.AssertTag("nope", "x"));

    await Assert.That(ex!.Message).Contains("Expected tag 'nope' but it was not present");
  }

  [Test]
  public async Task AssertTag_WrongValue_ThrowsWithBothValuesAsync() {
    var span = SpanFactory.Create("tagged", tags: new Dictionary<string, object?> { ["env"] = "test" });
    var tree = TraceTree.Build([span]);

    var ex = Assert.Throws<TraceAssertionException>(() => tree.AssertTag("env", "prod"));

    await Assert.That(ex!.Message).Contains("Expected tag 'env' = 'prod' but was 'test'");
  }

  [Test]
  public async Task AssertTag_OnRootContainer_ThrowsAsync() {
    var container = TraceTree.Build([]);

    var ex = Assert.Throws<TraceAssertionException>(() => container.AssertTag("k", "v"));

    await Assert.That(ex!.Message).Contains("root container");
  }

  [Test]
  public async Task AssertHasTag_Present_ReturnsTreeAsync() {
    var span = SpanFactory.Create("tagged", tags: new Dictionary<string, object?> { ["env"] = "test" });
    var tree = TraceTree.Build([span]);

    var result = tree.AssertHasTag("env");

    await Assert.That(ReferenceEquals(result, tree)).IsTrue();
  }

  [Test]
  public async Task AssertHasTag_Missing_ThrowsAsync() {
    var tree = _singleRootTree();

    var ex = Assert.Throws<TraceAssertionException>(() => tree.AssertHasTag("nope"));

    await Assert.That(ex!.Message).Contains("Expected tag 'nope'");
  }

  [Test]
  public async Task AssertHasTag_OnRootContainer_ThrowsAsync() {
    var container = TraceTree.Build([]);

    var ex = Assert.Throws<TraceAssertionException>(() => container.AssertHasTag("k"));

    await Assert.That(ex!.Message).Contains("root container");
  }

  [Test]
  public async Task AssertNoOrphanedSpans_CleanTree_ReturnsTreeAsync() {
    var tree = _singleRootTree();

    var result = tree.AssertNoOrphanedSpans();

    await Assert.That(ReferenceEquals(result, tree)).IsTrue();
  }

  [Test]
  public async Task AssertNoOrphanedSpans_WithOrphan_ThrowsNamingOrphanAsync() {
    var spans = new List<CapturedSpan> {
      SpanFactory.Create("real-root", spanId: "s-root", startTime: _at(0)),
      SpanFactory.Create("orphan", spanId: "s-orphan", parentSpanId: "s-missing", startTime: _at(1))
    };
    var tree = TraceTree.Build(spans);

    var ex = Assert.Throws<TraceAssertionException>(() => tree.AssertNoOrphanedSpans());

    await Assert.That(ex!.Message).Contains("Found 1 orphaned spans");
    await Assert.That(ex.Message).Contains("'orphan'");
  }

  // ============== Navigation ==============

  [Test]
  public async Task Child_ByIndex_ReturnsChildAsync() {
    var tree = _singleRootTree();

    var child = tree.Child(1);

    await Assert.That(child.Span!.Name).IsEqualTo("child-b");
  }

  [Test]
  public async Task Child_IndexOutOfRange_ThrowsAsync() {
    var tree = _singleRootTree();

    var ex = Assert.Throws<TraceAssertionException>(() => tree.Child(7));

    await Assert.That(ex!.Message).Contains("Child index 7 out of range. Have 2 children");
  }

  [Test]
  public async Task Child_NegativeIndex_ThrowsAsync() {
    var tree = _singleRootTree();

    var ex = Assert.Throws<TraceAssertionException>(() => tree.Child(-1));

    await Assert.That(ex!.Message).Contains("out of range");
  }

  [Test]
  public async Task Child_ByName_ReturnsChildAsync() {
    var tree = _singleRootTree();

    var child = tree.Child("child-a");

    await Assert.That(child.Span!.Name).IsEqualTo("child-a");
    await Assert.That(child.Children.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Child_ByName_NotFound_ThrowsListingAvailableAsync() {
    var tree = _singleRootTree();

    var ex = Assert.Throws<TraceAssertionException>(() => tree.Child("missing"));

    await Assert.That(ex!.Message).Contains("Child 'missing' not found");
    await Assert.That(ex.Message).Contains("'child-a'");
  }

  [Test]
  public async Task ChildContaining_Match_ReturnsChildAsync() {
    var tree = _singleRootTree();

    var child = tree.ChildContaining("-b");

    await Assert.That(child.Span!.Name).IsEqualTo("child-b");
  }

  [Test]
  public async Task ChildContaining_NoMatch_ThrowsAsync() {
    var tree = _singleRootTree();

    var ex = Assert.Throws<TraceAssertionException>(() => tree.ChildContaining("zzz"));

    await Assert.That(ex!.Message).Contains("No child containing 'zzz'");
  }

  // ============== Serialization ==============

  [Test]
  public async Task ToSnapshot_ExcludesVolatileTagsAsync() {
    var span = SpanFactory.Create("root", tags: new Dictionary<string, object?> {
      ["stable"] = "kept",
      ["otel.status_code"] = "dropped",
      ["thread.id"] = 42,
      ["thread.name"] = "dropped-too"
    });
    var tree = TraceTree.Build([span]);

    var json = tree.ToSnapshot();

    await Assert.That(json).Contains("stable");
    await Assert.That(json).Contains("kept");
    await Assert.That(json).DoesNotContain("otel.status_code");
    await Assert.That(json).DoesNotContain("thread.id");
    await Assert.That(json).DoesNotContain("thread.name");
  }

  [Test]
  public async Task ToSnapshot_FromSnapshot_RoundTripsStructureAsync() {
    var spans = new List<CapturedSpan> {
      SpanFactory.Create("root", spanId: "s-root", startTime: _at(0),
        kind: ActivityKind.Server, status: ActivityStatusCode.Ok,
        tags: new Dictionary<string, object?> { ["k"] = "v" }),
      SpanFactory.Create("child", spanId: "s-c", parentSpanId: "s-root", startTime: _at(1))
    };
    var original = TraceTree.Build(spans);

    var restored = TraceTree.FromSnapshot(original.ToSnapshot());

    await Assert.That(restored.Span!.Name).IsEqualTo("root");
    await Assert.That(restored.Span.Kind).IsEqualTo(ActivityKind.Server);
    await Assert.That(restored.Span.Status).IsEqualTo(ActivityStatusCode.Ok);
    await Assert.That(restored.Span.Tags["k"]).IsEqualTo("v");
    await Assert.That(restored.Span.TraceId).IsEqualTo("snapshot");
    await Assert.That(restored.Span.Duration).IsEqualTo(TimeSpan.Zero);
    await Assert.That(restored.Children.Count).IsEqualTo(1);
    await Assert.That(restored.Children[0].Span!.Name).IsEqualTo("child");
  }

  [Test]
  public async Task FromSnapshot_NullJson_ThrowsArgumentExceptionAsync() {
    var ex = Assert.Throws<ArgumentException>(() => TraceTree.FromSnapshot("null"));

    await Assert.That(ex!.Message).Contains("Invalid snapshot JSON");
  }

  [Test]
  public async Task FromSnapshot_UnknownKindAndStatus_FallBackToDefaultsAsync() {
    const string json = """
      {
        "name": "root",
        "kind": "NotAKind",
        "status": "NotAStatus"
      }
      """;

    var tree = TraceTree.FromSnapshot(json);

    await Assert.That(tree.Span!.Kind).IsEqualTo(ActivityKind.Internal);
    await Assert.That(tree.Span.Status).IsEqualTo(ActivityStatusCode.Unset);
  }

  [Test]
  public async Task FromSnapshot_NoName_ProducesContainerNodeAsync() {
    const string json = """
      {
        "children": [
          { "name": "a" },
          { "name": "b" }
        ]
      }
      """;

    var tree = TraceTree.FromSnapshot(json);

    await Assert.That(tree.Span).IsNull();
    await Assert.That(tree.Children.Count).IsEqualTo(2);
    await Assert.That(tree.Children[0].Span!.Name).IsEqualTo("a");
  }

  [Test]
  public async Task ToString_SingleTree_RendersIndentedSpansAsync() {
    var tree = _singleRootTree();

    var text = tree.ToString();

    await Assert.That(text).Contains("- root");
    await Assert.That(text).Contains("  - child-a");
    await Assert.That(text).Contains("    - grandchild");
  }

  [Test]
  public async Task ToString_Container_RendersTraceCountHeaderAsync() {
    var spans = new List<CapturedSpan> {
      SpanFactory.Create("r1", traceId: "t1", spanId: "s1", startTime: _at(0)),
      SpanFactory.Create("r2", traceId: "t2", spanId: "s2", startTime: _at(1))
    };
    var tree = TraceTree.Build(spans);

    var text = tree.ToString();

    await Assert.That(text).Contains("[Traces: 2]");
    await Assert.That(text).Contains("- r1");
    await Assert.That(text).Contains("- r2");
  }

  // ============== TraceAssertionException ==============

  [Test]
  public async Task TraceAssertionException_DefaultCtor_HasDefaultMessageAsync() {
    var ex = new TraceAssertionException();

    await Assert.That(ex.Message).IsEqualTo("Trace assertion failed.");
  }

  [Test]
  public async Task TraceAssertionException_MessageCtor_StoresMessageAsync() {
    var ex = new TraceAssertionException("custom");

    await Assert.That(ex.Message).IsEqualTo("custom");
  }

  [Test]
  public async Task TraceAssertionException_InnerExceptionCtor_StoresBothAsync() {
    var inner = new InvalidOperationException("inner");

    var ex = new TraceAssertionException("outer", inner);

    await Assert.That(ex.Message).IsEqualTo("outer");
    await Assert.That(ReferenceEquals(ex.InnerException, inner)).IsTrue();
  }
}
