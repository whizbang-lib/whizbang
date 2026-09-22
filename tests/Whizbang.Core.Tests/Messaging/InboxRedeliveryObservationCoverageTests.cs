using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage round 23 tail: the two malformed-input guards in
/// <see cref="InboxRedeliveryObservation.ParseProjection"/> that every existing test skips —
/// every other test in <c>InboxRedeliveryObservationProjectionTests.cs</c> hands the parser a
/// well-formed JSON array, so the null/empty short-circuit and the non-array short-circuit have
/// never actually run.
/// </summary>
[Category("Messaging")]
public class InboxRedeliveryObservationCoverageTests {

  /// <summary>
  /// Operator impact: the store's projection query can legitimately return an empty/absent
  /// result (no redeliveries this poll). If this guard regressed into a throw, poison detection
  /// would crash the poll cycle instead of reporting zero redeliveries.
  /// </summary>
  [Test]
  public async Task ParseProjection_NullJson_ReturnsEmptyAsync() {
    var parsed = InboxRedeliveryObservation.ParseProjection(null);

    await Assert.That(parsed.Count).IsEqualTo(0)
      .Because("null projection JSON means the store had nothing to report — the parser must "
             + "short-circuit rather than throw on JsonDocument.Parse(null)");
  }

  /// <summary>
  /// Operator impact: same as the null case above but for the empty/whitespace string a
  /// misbehaving or stubbed store might send — must degrade the same way, not throw.
  /// </summary>
  [Test]
  public async Task ParseProjection_WhitespaceJson_ReturnsEmptyAsync() {
    var parsed = InboxRedeliveryObservation.ParseProjection("   ");

    await Assert.That(parsed.Count).IsEqualTo(0)
      .Because("whitespace-only JSON is treated the same as absent JSON, not parsed and thrown on");
  }

  /// <summary>
  /// Operator impact: a malformed projection (root is an object or scalar, not an array) must
  /// degrade to "no redeliveries observed" rather than crash the poison-detection poll — a single
  /// bad row must never take down the whole quarantine pass.
  /// </summary>
  [Test]
  public async Task ParseProjection_NonArrayRoot_ReturnsEmptyAsync() {
    var parsed = InboxRedeliveryObservation.ParseProjection("""{"m":"not-an-array"}""");

    await Assert.That(parsed.Count).IsEqualTo(0)
      .Because("a JSON object at the root is not the documented '[{...}, ...]' shape — the parser "
             + "must recognise that and return empty rather than throw or misread it");
  }
}
