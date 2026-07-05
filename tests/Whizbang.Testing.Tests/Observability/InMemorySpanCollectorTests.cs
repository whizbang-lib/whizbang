using System.Diagnostics;
using Whizbang.Testing.Observability;

namespace Whizbang.Testing.Tests.Observability;

/// <summary>
/// Tests for <see cref="InMemorySpanCollector"/> using real <see cref="ActivitySource"/>
/// instances with unique names per test to avoid cross-test interference.
/// </summary>
public class InMemorySpanCollectorTests {
  private static string _uniqueSourceName() => $"WhizbangTest.{Guid.NewGuid():N}";

  private static ActivityContext _remoteParentContext() =>
    new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);

  [Test]
  public async Task Collector_CapturesStoppedActivities_FromListenedSourceAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);

    using (var activity = source.StartActivity("operation")) {
      await Assert.That(activity).IsNotNull();
      activity!.SetTag("kind", "test");
    }

    await Assert.That(collector.Count).IsEqualTo(1);
    var span = collector.Spans[0];
    await Assert.That(span.Name).IsEqualTo("operation");
    await Assert.That(span.SourceName).IsEqualTo(sourceName);
    await Assert.That(span.GetTag("kind")).IsEqualTo("test");
    await Assert.That(span.GetTag<string>("kind")).IsEqualTo("test");
    await Assert.That(span.GetTag("missing")).IsNull();
    await Assert.That(span.IsRoot).IsTrue();
  }

  [Test]
  public async Task Collector_IgnoresActivities_FromOtherSourcesAsync() {
    var listenedName = _uniqueSourceName();
    var otherName = _uniqueSourceName();
    using var listened = new ActivitySource(listenedName);
    using var other = new ActivitySource(otherName);
    using var collector = new InMemorySpanCollector(listenedName);
    // Plain listener (not a collector) so the other source's activities actually start
    // without stealing the collector's AsyncLocal scope.
    using var otherListener = new ActivityListener {
      ShouldListenTo = s => s.Name == otherName,
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
    };
    ActivitySource.AddActivityListener(otherListener);

    using (listened.StartActivity("wanted")) { }
    using (other.StartActivity("unwanted")) { }

    await Assert.That(collector.Count).IsEqualTo(1);
    await Assert.That(collector.Spans[0].Name).IsEqualTo("wanted");
  }

  [Test]
  public async Task Collector_NoSourceNames_ListensToAllSourcesAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector();

    using (source.StartActivity("any-source-op")) { }

    await Assert.That(collector.WithNameContaining("any-source-op").Any()).IsTrue();
  }

  [Test]
  public async Task Collector_IgnoresActivities_StartedBeforeCreationAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    // Bootstrap collector so the activity has a listener and actually starts.
    using var bootstrap = new InMemorySpanCollector(sourceName);

    var activity = source.StartActivity("early");
    await Assert.That(activity).IsNotNull();
    // Push start time firmly into the past so the created-at filter is deterministic.
    activity!.SetStartTime(DateTime.UtcNow.AddMinutes(-5));

    using var collector = new InMemorySpanCollector(sourceName);
    activity.Stop();

    await Assert.That(collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Collector_QueryHelpers_FilterSpansAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);

    using (source.StartActivity("prefix-alpha")) { }
    using (source.StartActivity("prefix-beta")) { }
    using (source.StartActivity("other-gamma")) { }

    await Assert.That(collector.Where(s => s.Name.EndsWith("beta", StringComparison.Ordinal)).Count()).IsEqualTo(1);
    await Assert.That(collector.WithNamePrefix("prefix-").Count()).IsEqualTo(2);
    await Assert.That(collector.WithNameContaining("gamma").Count()).IsEqualTo(1);
    await Assert.That(collector.FirstOrDefault(s => s.Name == "prefix-alpha")).IsNotNull();
    await Assert.That(collector.FirstOrDefault(s => s.Name == "nope")).IsNull();
  }

  [Test]
  public async Task Collector_GetRoots_And_GetChildren_ReflectHierarchyAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);

    using (source.StartActivity("parent")) {
      using (source.StartActivity("child")) { }
    }

    var roots = collector.GetRoots().ToList();
    await Assert.That(roots.Count).IsEqualTo(1);
    await Assert.That(roots[0].Name).IsEqualTo("parent");

    var children = collector.GetChildren(roots[0]).ToList();
    await Assert.That(children.Count).IsEqualTo(1);
    await Assert.That(children[0].Name).IsEqualTo("child");

    var byTrace = collector.GetByTraceId(roots[0].TraceId).ToList();
    await Assert.That(byTrace.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Collector_BuildTree_ProducesParentChildStructureAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);

    using (source.StartActivity("root-op")) {
      using (source.StartActivity("child-op")) { }
    }

    var tree = collector.BuildTree();

    await Assert.That(tree.Span!.Name).IsEqualTo("root-op");
    await Assert.That(tree.Children.Count).IsEqualTo(1);
    await Assert.That(tree.Children[0].Span!.Name).IsEqualTo("child-op");
  }

  [Test]
  public async Task Collector_Clear_RemovesCapturedSpansAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);

    using (source.StartActivity("op")) { }
    await Assert.That(collector.Count).IsEqualTo(1);

    collector.Clear();

    await Assert.That(collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Collector_HasOrphanedSpans_FalseForCompleteTraceAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);

    using (source.StartActivity("parent")) {
      using (source.StartActivity("child")) { }
    }

    await Assert.That(collector.HasOrphanedSpans()).IsFalse();
    await Assert.That(collector.GetOrphanedSpans().Count()).IsEqualTo(0);
  }

  [Test]
  public async Task Collector_HasOrphanedSpans_TrueWhenParentNotCapturedAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);

    using (source.StartActivity("orphan", ActivityKind.Internal, _remoteParentContext())) { }

    await Assert.That(collector.HasOrphanedSpans()).IsTrue();
    var orphans = collector.GetOrphanedSpans().ToList();
    await Assert.That(orphans.Count).IsEqualTo(1);
    await Assert.That(orphans[0].Name).IsEqualTo("orphan");
    await Assert.That(orphans[0].IsRoot).IsFalse();
  }

  [Test]
  public async Task Collector_Dispose_StopsCapturingAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    var collector = new InMemorySpanCollector(sourceName);
    // Keep a second live collector so activities still start after the first is disposed.
    using var keepAlive = new InMemorySpanCollector(sourceName);

    collector.Dispose();
    collector.Dispose(); // Double dispose must be safe.

    using (source.StartActivity("after-dispose")) { }

    await Assert.That(collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Collector_Nested_InnerCapturesWhileActive_OuterResumesAfterInnerDisposedAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var outer = new InMemorySpanCollector(sourceName);

    using (var inner = new InMemorySpanCollector(sourceName)) {
      using (source.StartActivity("inner-op")) { }

      await Assert.That(inner.Count).IsEqualTo(1);
      await Assert.That(outer.Count).IsEqualTo(0);
    }

    using (source.StartActivity("outer-op")) { }

    await Assert.That(outer.Count).IsEqualTo(1);
    await Assert.That(outer.Spans[0].Name).IsEqualTo("outer-op");
  }

  [Test]
  public async Task CapturedSpan_From_NullActivity_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentNullException>(() => CapturedSpan.From(null!));

    await Assert.That(ex!.ParamName).IsEqualTo("activity");
  }

  [Test]
  public async Task CapturedSpan_From_CapturesEventsAsync() {
    var sourceName = _uniqueSourceName();
    using var source = new ActivitySource(sourceName);
    using var collector = new InMemorySpanCollector(sourceName);

    using (var activity = source.StartActivity("with-event")) {
      activity!.AddEvent(new ActivityEvent("something-happened"));
    }

    var span = collector.Spans[0];
    await Assert.That(span.Events.Count).IsEqualTo(1);
    await Assert.That(span.Events[0].Name).IsEqualTo("something-happened");
  }
}
