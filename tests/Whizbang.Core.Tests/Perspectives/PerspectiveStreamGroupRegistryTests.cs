#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The stream-group registry: generated module initializers register one membership per
/// [StreamGroup] declaration; the maintenance cascade reads them to compute the eviction closure.
/// Locks multiplicity (a perspective in two groups holds two memberships with independent dials),
/// idempotent re-registration per (type, key), and the empty default for ungrouped models —
/// untouchable by cascades.
/// </summary>
/// <docs>proposals/pre-destruction-seam</docs>
public class PerspectiveStreamGroupRegistryTests {

  private sealed class ListModel;
  private sealed class BridgingModel;
  private sealed class UngroupedModel;

  [Test]
  public async Task Register_MultipleGroups_EachMembershipKeepsItsOwnDialsAsync() {
    PerspectiveStreamGroupRegistry.Register(typeof(BridgingModel), "g1", announce: true, follow: true, bridge: false);
    PerspectiveStreamGroupRegistry.Register(typeof(BridgingModel), "g2", announce: true, follow: true, bridge: true);

    var memberships = PerspectiveStreamGroupRegistry.Resolve(typeof(BridgingModel));

    await Assert.That(memberships).Count().IsEqualTo(2)
      .Because("a perspective in two groups is the case the dials exist for — each membership is independent");
    await Assert.That(memberships.Single(m => m.Key == "g1").Bridge).IsFalse();
    await Assert.That(memberships.Single(m => m.Key == "g2").Bridge).IsTrue()
      .Because("bridging is per-membership: received evictions cross into g2 but not out of g1");
  }

  [Test]
  public async Task Register_SameKeyTwice_LastWriteWins_IdempotentAsync() {
    PerspectiveStreamGroupRegistry.Register(typeof(ListModel), "chat", announce: true, follow: true, bridge: false);
    PerspectiveStreamGroupRegistry.Register(typeof(ListModel), "chat", announce: true, follow: false, bridge: false);

    var memberships = PerspectiveStreamGroupRegistry.Resolve(typeof(ListModel));

    await Assert.That(memberships.Where(m => m.Key == "chat")).Count().IsEqualTo(1)
      .Because("module initializers may re-run in test hosts — re-registration must not duplicate");
    await Assert.That(memberships.Single(m => m.Key == "chat").Follow).IsFalse();
  }

  [Test]
  public async Task Resolve_UngroupedModel_IsEmpty_UntouchableByCascadesAsync() {
    await Assert.That(PerspectiveStreamGroupRegistry.Resolve(typeof(UngroupedModel))).Count().IsEqualTo(0)
      .Because("a perspective that joined no group is immune to cascades regardless of shared streams — "
             + "deliberate long-retention perspectives simply don't join");
  }
}
