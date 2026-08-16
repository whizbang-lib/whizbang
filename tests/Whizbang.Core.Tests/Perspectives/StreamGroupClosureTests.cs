#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The eviction closure's graph semantics — the regression locks from the proposal. Five
/// perspectives over the same streams: a and b share group g1; b, d and e share g2; c joined
/// nothing. b's dual membership is the case the dials exist for: its OWN evictions announce to
/// both groups, but an eviction RECEIVED through one group crosses into the other only when its
/// membership bridges.
/// </summary>
/// <docs>proposals/pre-destruction-seam</docs>
public class StreamGroupClosureTests {

  private sealed class A;
  private sealed class B;
  private sealed class C;
  private sealed class D;
  private sealed class E;

  private static StreamGroupMembership _m(string key, bool announce = true, bool follow = true, bool bridge = false) =>
    new(key, announce, follow, bridge);

  private static Dictionary<Type, IReadOnlyList<StreamGroupMembership>> _scenario(bool bBridgesG2 = false, bool bBridgesG1 = false) =>
    new Dictionary<Type, IReadOnlyList<StreamGroupMembership>> {
      [typeof(A)] = [_m("g1")],
      [typeof(B)] = [_m("g1", bridge: bBridgesG1), _m("g2", bridge: bBridgesG2)],
      [typeof(D)] = [_m("g2")],
      [typeof(E)] = [_m("g2")],
      // c deliberately absent — no membership, untouchable.
    };

  [Test]
  public async Task OwnOrigin_AnnouncesToAllMemberships_NoBridgingInvolvedAsync() {
    var row = Guid.NewGuid();

    var cascade = StreamGroupClosure.Compute([(typeof(B), row)], _scenario());

    await Assert.That(cascade).Contains((typeof(A), row))
      .Because("b's own eviction announces to g1 — a follows");
    await Assert.That(cascade).Contains((typeof(D), row));
    await Assert.That(cascade).Contains((typeof(E), row))
      .Because("own-origin announces to BOTH groups; that is not bridging");
    await Assert.That(cascade).Count().IsEqualTo(3);
  }

  [Test]
  public async Task Received_CrossesGroupsOnlyWhenTheMembershipBridgesAsync() {
    var row = Guid.NewGuid();

    var withoutBridge = StreamGroupClosure.Compute([(typeof(A), row)], _scenario(bBridgesG2: false));
    await Assert.That(withoutBridge).Contains((typeof(B), row))
      .Because("a's eviction announces to g1; b follows");
    await Assert.That(withoutBridge.Select(c => c.Model)).DoesNotContain(typeof(D))
      .Because("b RECEIVED the eviction — without Bridge on its g2 membership it goes no further; "
             + "two groups sharing a member must never silently weld into one transitive graph");

    var withBridge = StreamGroupClosure.Compute([(typeof(A), row)], _scenario(bBridgesG2: true));
    await Assert.That(withBridge).Contains((typeof(D), row));
    await Assert.That(withBridge).Contains((typeof(E), row))
      .Because("Bridge is the explicit opt-in that lets a received eviction cross into g2");
  }

  [Test]
  public async Task NonMember_IsNeverCascaded_RegardlessOfSharedStreamsAsync() {
    var row = Guid.NewGuid();

    var cascade = StreamGroupClosure.Compute([(typeof(B), row)], _scenario(bBridgesG2: true, bBridgesG1: true));

    await Assert.That(cascade.Select(c => c.Model)).DoesNotContain(typeof(C))
      .Because("c joined no group — deliberately long-retention perspectives are untouchable by cascades");
  }

  [Test]
  public async Task CyclicBridgedGroups_Converge_EachPairEntersOnceAsync() {
    // a↔b in g1, b↔d in g2, d↔a in g3 — every membership bridges, forming a cycle.
    var row = Guid.NewGuid();
    var cyclic = new Dictionary<Type, IReadOnlyList<StreamGroupMembership>> {
      [typeof(A)] = [_m("g1", bridge: true), _m("g3", bridge: true)],
      [typeof(B)] = [_m("g1", bridge: true), _m("g2", bridge: true)],
      [typeof(D)] = [_m("g2", bridge: true), _m("g3", bridge: true)],
    };

    var cascade = StreamGroupClosure.Compute([(typeof(A), row)], cyclic);

    await Assert.That(cascade).Count().IsEqualTo(2)
      .Because("the fixpoint terminates on a cyclic graph: b and d each enter exactly once, and the "
             + "seed a never re-enters through the cycle");
    await Assert.That(cascade).Contains((typeof(B), row));
    await Assert.That(cascade).Contains((typeof(D), row));
  }

  [Test]
  public async Task FollowOff_MemberHearsAnnouncementsButNeverEvictsAsync() {
    var row = Guid.NewGuid();
    var memberships = new Dictionary<Type, IReadOnlyList<StreamGroupMembership>> {
      [typeof(A)] = [_m("g1")],
      [typeof(B)] = [_m("g1", follow: false)],
    };

    var cascade = StreamGroupClosure.Compute([(typeof(A), row)], memberships);

    await Assert.That(cascade).Count().IsEqualTo(0)
      .Because("Follow off means announcements pass this member by — announce-only members exist "
             + "to trigger, not to be triggered");
  }

  [Test]
  public async Task AnnounceOff_SeedTriggersNothingAsync() {
    var row = Guid.NewGuid();
    var memberships = new Dictionary<Type, IReadOnlyList<StreamGroupMembership>> {
      [typeof(A)] = [_m("g1", announce: false)],
      [typeof(B)] = [_m("g1")],
    };

    var cascade = StreamGroupClosure.Compute([(typeof(A), row)], memberships);

    await Assert.That(cascade).Count().IsEqualTo(0)
      .Because("a listen-only membership never announces its own evictions");
  }

  [Test]
  public async Task Seeds_AreNeverInTheCascade_TheyAreAlreadyDestroyedAsync() {
    var row = Guid.NewGuid();

    var cascade = StreamGroupClosure.Compute(
      [(typeof(A), row), (typeof(B), row)], _scenario());

    await Assert.That(cascade.Select(c => c.Model)).DoesNotContain(typeof(A));
    await Assert.That(cascade.Select(c => c.Model)).DoesNotContain(typeof(B))
      .Because("a seed came from the journal — its row is already gone; the cascade is the receivers only");
  }
}
