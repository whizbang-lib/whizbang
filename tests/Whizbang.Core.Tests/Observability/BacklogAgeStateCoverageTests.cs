using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Coverage round 23 tail: <see cref="BacklogAgeState.UnknownAgeSurfaces"/>. Existing tests only
/// read <see cref="BacklogAgeState.HasUnknownAgeSurface"/> (the boolean flag); the sorted-name
/// projection itself has never been read by any test.
/// </summary>
[Category("Observability")]
public class BacklogAgeStateCoverageTests {

  /// <summary>
  /// Operator impact: <c>HasUnknownAgeSurface</c> only says "something can't report an age" — an
  /// operator diagnosing a degraded backlog-age signal needs to know WHICH transport/entity pairs,
  /// and needs them in a stable order so a repeated read doesn't reshuffle the list under them.
  /// </summary>
  [Test]
  public async Task UnknownAgeSurfaces_ReturnsOrdinallySortedDistinctSurfacesAsync() {
    var state = new BacklogAgeState();

    state.ReportUnknownAge("servicebus", "zebra-queue");
    state.ReportUnknownAge("rabbitmq", "alpha-queue");
    state.ReportUnknownAge("servicebus", "zebra-queue"); // duplicate report, must not double-list

    await Assert.That(state.UnknownAgeSurfaces).IsEquivalentTo(
      ["rabbitmq/alpha-queue", "servicebus/zebra-queue"])
      .Because("the surfaces must be reported by transport/entity pair, deduplicated, and stable "
             + "regardless of report order");
    await Assert.That(state.UnknownAgeSurfaces[0]).IsEqualTo("rabbitmq/alpha-queue")
      .Because("ordinal sort must place 'rabbitmq/...' before 'servicebus/...' regardless of the "
             + "order the transports happened to report in this tick");
  }
}
