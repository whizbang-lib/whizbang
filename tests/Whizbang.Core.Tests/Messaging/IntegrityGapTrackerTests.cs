using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Stream-integrity Phase B: the consumer-side gap state — pending deficits are per-origin and
/// taken exactly once (two-cycle confirmation), and checkpoint liveness surfaces origins that
/// stopped checkpointing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityGapTracker.cs</code-under-test>
[Category("Messaging")]
public class IntegrityGapTrackerTests {

  [Test]
  public async Task TakePending_ReturnsAndRemovesOnlyThatOriginAsync() {
    var tracker = new IntegrityGapTracker();
    var originA = TrackedGuid.NewMedo().Value;
    var originB = TrackedGuid.NewMedo().Value;
    tracker.AddPending(_gap(originA, "Contracts.TypeX"));
    tracker.AddPending(_gap(originA, "Contracts.TypeY"));
    tracker.AddPending(_gap(originB, "Contracts.TypeX"));

    var taken = tracker.TakePending(originA);

    await Assert.That(taken.Count).IsEqualTo(2);
    await Assert.That(tracker.TakePending(originA)).IsEmpty()
      .Because("pendings are taken exactly once — the next checkpoint either confirms or clears them.");
    await Assert.That(tracker.TakePending(originB).Count).IsEqualTo(1)
      .Because("another origin's pendings are untouched.");
  }

  [Test]
  public async Task GetStaleOrigins_SurfacesOnlyOriginsPastTheThresholdAsync() {
    var tracker = new IntegrityGapTracker();
    var fresh = TrackedGuid.NewMedo().Value;
    var stale = TrackedGuid.NewMedo().Value;
    var now = DateTimeOffset.UtcNow;
    tracker.RecordCheckpoint(fresh, "fresh-svc", now - TimeSpan.FromSeconds(30));
    tracker.RecordCheckpoint(stale, "stale-svc", now - TimeSpan.FromMinutes(10));

    var staleOrigins = tracker.GetStaleOrigins(TimeSpan.FromMinutes(3), now);

    await Assert.That(staleOrigins.Count).IsEqualTo(1);
    await Assert.That(staleOrigins[0].OriginServiceId).IsEqualTo(stale)
      .Because("an origin silent past 3x the interval can no longer be verified — the liveness alarm.");
    await Assert.That(staleOrigins[0].OriginServiceName).IsEqualTo("stale-svc");
  }

  private static IntegrityGapTracker.PendingGap _gap(Guid origin, string eventType) => new() {
    OriginServiceId = origin,
    OriginServiceName = "origin-svc",
    TenantScope = "tenant-a",
    EventType = eventType,
    FromCommitSequence = 0,
    ToCommitSequence = 10,
    ExpectedCount = 3,
  };
  [Test]
  public async Task RecordCheckpoint_StoresOriginRequestTopic_LatestWinsAsync() {
    // Directed integrity requests (manifest / redelivery / drill-down) must publish to a topic
    // the ORIGIN actually consumes — the origin carries that address on its checkpoint, and the
    // tracker is where the audit looks it up. Guessing with the REQUESTER's own destination sent
    // requests to topics the origin never subscribed to (observed live: six requests, zero
    // origin receipts).
    var tracker = new IntegrityGapTracker();
    var origin = Guid.NewGuid();

    tracker.RecordCheckpoint(origin, "origin-svc", DateTimeOffset.UtcNow, "origin.requests.v1");
    tracker.RecordCheckpoint(origin, "origin-svc", DateTimeOffset.UtcNow, "origin.requests.v2");

    var origins = tracker.GetOrigins();
    await Assert.That(origins.Count).IsEqualTo(1);
    await Assert.That(origins[0].RequestTopic).IsEqualTo("origin.requests.v2")
      .Because("the newest checkpoint's address wins — origins may re-home across deploys.");
    await Assert.That(tracker.GetRequestTopic(origin)).IsEqualTo("origin.requests.v2");
    await Assert.That(tracker.GetRequestTopic(Guid.NewGuid())).IsNull()
      .Because("an unknown origin has no address — callers fall back.");
  }
}
