using System.Diagnostics;
using Whizbang.Testing.Observability;
using Whizbang.Testing.Tests.TestSupport;

namespace Whizbang.Testing.Tests.Observability;

/// <summary>
/// Tests for <see cref="TraceSnapshotComparer"/> - baseline comparison verdicts
/// for every <see cref="TraceDifferenceKind"/>.
/// </summary>
public class TraceSnapshotComparerTests {
  private static TraceTree _tree(params CapturedSpan[] spans) => TraceTree.Build(spans);

  private static CapturedSpan _root(
    string name = "root",
    ActivityKind kind = ActivityKind.Internal,
    ActivityStatusCode status = ActivityStatusCode.Unset,
    IReadOnlyDictionary<string, object?>? tags = null) {
    return SpanFactory.Create(name, spanId: "s-root", tags: tags, kind: kind, status: status);
  }

  private static CapturedSpan _child(string name, string spanId, int startSecond) {
    return SpanFactory.Create(
      name, spanId: spanId, parentSpanId: "s-root",
      startTime: DateTimeOffset.UnixEpoch.AddSeconds(startSecond));
  }

  [Test]
  public async Task Compare_IdenticalTrees_IsMatchAsync() {
    var actual = _tree(_root(), _child("child", "s-c", 1));
    var expected = _tree(_root(), _child("child", "s-c", 1));

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    await Assert.That(comparison.IsMatch).IsTrue();
    await Assert.That(comparison.Differences.Count).IsEqualTo(0);
    await Assert.That(comparison.ToString()).IsEqualTo("Traces match.");
  }

  [Test]
  public async Task Compare_NullActual_ThrowsAsync() {
    var expected = _tree(_root());

    var ex = Assert.Throws<ArgumentNullException>(() => TraceSnapshotComparer.Compare(null!, expected));

    await Assert.That(ex!.ParamName).IsEqualTo("actual");
  }

  [Test]
  public async Task Compare_NullExpected_ThrowsAsync() {
    var actual = _tree(_root());

    var ex = Assert.Throws<ArgumentNullException>(() => TraceSnapshotComparer.Compare(actual, null!));

    await Assert.That(ex!.ParamName).IsEqualTo("expected");
  }

  [Test]
  public async Task Compare_DifferentNames_ReportsNameMismatchAsync() {
    var actual = _tree(_root("actual-name"));
    var expected = _tree(_root("expected-name"));

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    await Assert.That(comparison.IsMatch).IsFalse();
    var diff = comparison.Differences.Single(d => d.Kind == TraceDifferenceKind.NameMismatch);
    await Assert.That(diff.Expected).IsEqualTo("expected-name");
    await Assert.That(diff.Actual).IsEqualTo("actual-name");
  }

  [Test]
  public async Task Compare_DifferentKinds_ReportsKindMismatchAsync() {
    var actual = _tree(_root(kind: ActivityKind.Client));
    var expected = _tree(_root(kind: ActivityKind.Server));

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    var diff = comparison.Differences.Single(d => d.Kind == TraceDifferenceKind.KindMismatch);
    await Assert.That(diff.Expected).IsEqualTo("Server");
    await Assert.That(diff.Actual).IsEqualTo("Client");
  }

  [Test]
  public async Task Compare_DifferentStatuses_ReportsStatusMismatchAsync() {
    var actual = _tree(_root(status: ActivityStatusCode.Error));
    var expected = _tree(_root(status: ActivityStatusCode.Ok));

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    var diff = comparison.Differences.Single(d => d.Kind == TraceDifferenceKind.StatusMismatch);
    await Assert.That(diff.Expected).IsEqualTo("Ok");
    await Assert.That(diff.Actual).IsEqualTo("Error");
  }

  [Test]
  public async Task Compare_MissingChildInActual_ReportsChildCountAndMissingChildAsync() {
    var actual = _tree(_root());
    var expected = _tree(_root(), _child("expected-child", "s-c", 1));

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    await Assert.That(comparison.Differences.Any(d => d.Kind == TraceDifferenceKind.ChildCountMismatch)).IsTrue();
    var missing = comparison.Differences.Single(d => d.Kind == TraceDifferenceKind.MissingChild);
    await Assert.That(missing.Path).IsEqualTo("expected-child");
    await Assert.That(missing.Actual).IsEqualTo("(missing)");
  }

  [Test]
  public async Task Compare_ExtraChildInActual_ReportsExtraChildAsync() {
    var actual = _tree(_root(), _child("surprise", "s-c", 1));
    var expected = _tree(_root());

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    var extra = comparison.Differences.Single(d => d.Kind == TraceDifferenceKind.ExtraChild);
    await Assert.That(extra.Path).IsEqualTo("surprise");
    await Assert.That(extra.Expected).IsEqualTo("(none)");
    await Assert.That(extra.Actual).IsEqualTo("surprise");
  }

  [Test]
  public async Task Compare_MissingTag_ReportsMissingTagWithPathAsync() {
    var actual = _tree(_root());
    var expected = _tree(_root(tags: new Dictionary<string, object?> { ["env"] = "test" }));

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    var diff = comparison.Differences.Single(d => d.Kind == TraceDifferenceKind.MissingTag);
    await Assert.That(diff.Path).IsEqualTo("/@env");
    await Assert.That(diff.Expected).IsEqualTo("test");
    await Assert.That(diff.Actual).IsEqualTo("(missing)");
  }

  [Test]
  public async Task Compare_TagValueMismatch_ReportsBothValuesAsync() {
    var actual = _tree(_root(tags: new Dictionary<string, object?> { ["env"] = "actual" }));
    var expected = _tree(_root(tags: new Dictionary<string, object?> { ["env"] = "expected" }));

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    var diff = comparison.Differences.Single(d => d.Kind == TraceDifferenceKind.TagValueMismatch);
    await Assert.That(diff.Expected).IsEqualTo("expected");
    await Assert.That(diff.Actual).IsEqualTo("actual");
  }

  [Test]
  public async Task Compare_ExtraTagInActual_ReportsExtraTagAsync() {
    var actual = _tree(_root(tags: new Dictionary<string, object?> { ["extra"] = "value" }));
    var expected = _tree(_root());

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    var diff = comparison.Differences.Single(d => d.Kind == TraceDifferenceKind.ExtraTag);
    await Assert.That(diff.Path).IsEqualTo("/@extra");
    await Assert.That(diff.Actual).IsEqualTo("value");
  }

  [Test]
  public async Task Compare_VolatileTags_AreIgnoredAsync() {
    var actual = _tree(_root(tags: new Dictionary<string, object?> {
      ["otel.status_code"] = "actual-only",
      ["thread.id"] = 1,
      ["thread.name"] = "worker"
    }));
    var expected = _tree(_root());

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    await Assert.That(comparison.IsMatch).IsTrue();
  }

  [Test]
  public async Task Compare_NestedDifference_BuildsSlashSeparatedPathAsync() {
    var actualSpans = new[] {
      SpanFactory.Create("root", spanId: "s-root"),
      SpanFactory.Create("branch", spanId: "s-b", parentSpanId: "s-root"),
      SpanFactory.Create("leaf-actual", spanId: "s-l", parentSpanId: "s-b")
    };
    var expectedSpans = new[] {
      SpanFactory.Create("root", spanId: "s-root"),
      SpanFactory.Create("branch", spanId: "s-b", parentSpanId: "s-root"),
      SpanFactory.Create("leaf-expected", spanId: "s-l", parentSpanId: "s-b")
    };

    var comparison = TraceSnapshotComparer.Compare(TraceTree.Build(actualSpans), TraceTree.Build(expectedSpans));

    var diff = comparison.Differences.Single(d => d.Kind == TraceDifferenceKind.NameMismatch);
    await Assert.That(diff.Path).IsEqualTo("branch/leaf-expected");
  }

  [Test]
  public async Task Compare_ContainerVsSpanTree_ReportsNameMismatchWithNullPlaceholderAsync() {
    var actual = TraceTree.Build([]);
    var expected = _tree(_root("expected-root"));

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    await Assert.That(comparison.IsMatch).IsFalse();
    var diff = comparison.Differences.Single(d => d.Kind == TraceDifferenceKind.NameMismatch);
    await Assert.That(diff.Actual).IsEqualTo("(null)");
  }

  [Test]
  public async Task GenerateBaseline_MatchesToSnapshotAsync() {
    var tree = _tree(_root(), _child("child", "s-c", 1));

    var baseline = TraceSnapshotComparer.GenerateBaseline(tree);

    await Assert.That(baseline).IsEqualTo(tree.ToSnapshot());
  }

  [Test]
  public async Task GenerateBaseline_Null_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentNullException>(() => TraceSnapshotComparer.GenerateBaseline(null!));

    await Assert.That(ex!.ParamName).IsEqualTo("actual");
  }

  [Test]
  public async Task TraceComparison_ToString_ListsEachDifferenceAsync() {
    var actual = _tree(_root("actual-name", kind: ActivityKind.Client));
    var expected = _tree(_root("expected-name", kind: ActivityKind.Server));

    var text = TraceSnapshotComparer.Compare(actual, expected).ToString();

    await Assert.That(text).Contains("difference(s):");
    await Assert.That(text).Contains("[NameMismatch]");
    await Assert.That(text).Contains("[KindMismatch]");
    await Assert.That(text).Contains("expected 'expected-name', actual 'actual-name'");
  }
}
