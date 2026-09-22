using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Observability;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Sibling spans with no ordering guarantee must not make a baseline comparison fail.
/// </summary>
/// <remarks>
/// <para>
/// The comparer walked children positionally, pairing actual[i] with expected[i]. Spans emitted
/// concurrently arrive in whatever order they finish, so a detached stage and an inline stage could
/// swap places between runs and the comparison would report four differences for a trace that was
/// entirely correct.
/// </para>
/// <para>
/// It surfaced as an intermittent baseline failure that passed in isolation and failed under
/// full-suite load, which is the worst shape for a test to fail in: rerunning makes it go away, so
/// it gets rerun rather than fixed.
/// </para>
/// </remarks>
[Category("Observability")]
public class TraceSnapshotComparerOrderingTests {

  [Test]
  public async Task SiblingsInADifferentOrderStillMatchAsync() {
    var expected = _tree("root", "DistributeDetached", "PostDistributeInline");
    var actual = _tree("root", "PostDistributeInline", "DistributeDetached");

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    await Assert.That(comparison.IsMatch).IsTrue()
      .Because("concurrently emitted siblings have no defined order, so comparing them by "
             + "position reports differences in a trace that is correct");
  }

  [Test]
  public async Task AGenuinelyMissingSiblingIsStillReportedAsync() {
    var expected = _tree("root", "DistributeDetached", "PostDistributeInline");
    var actual = _tree("root", "DistributeDetached");

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    // Order insensitivity must not become blindness: a span that is absent is still a difference.
    await Assert.That(comparison.IsMatch).IsFalse();
  }

  [Test]
  public async Task ADifferentlyNamedSiblingIsStillReportedAsync() {
    var expected = _tree("root", "DistributeDetached", "PostDistributeInline");
    var actual = _tree("root", "DistributeDetached", "SomethingElse");

    var comparison = TraceSnapshotComparer.Compare(actual, expected);

    await Assert.That(comparison.IsMatch).IsFalse();
  }

  private static TraceTree _tree(string root, params string[] children) {
    var spans = new List<CapturedSpan> {
      _span(root, "1", null),
    };
    for (var i = 0; i < children.Length; i++) {
      spans.Add(_span(children[i], $"c{i}", "1"));
    }
    return TraceTree.Build(spans);
  }

  private static CapturedSpan _span(string name, string spanId, string? parentSpanId) => new() {
    Name = name,
    Kind = ActivityKind.Internal,
    TraceId = "t",
    SpanId = spanId,
    ParentSpanId = parentSpanId,
    Duration = TimeSpan.Zero,
    Status = ActivityStatusCode.Unset,
    Tags = new Dictionary<string, object?>(),
    Events = [],
    SourceName = "test",
    StartTime = DateTimeOffset.UnixEpoch,
  };
}
