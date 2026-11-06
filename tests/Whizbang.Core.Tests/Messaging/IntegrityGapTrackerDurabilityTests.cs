// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2104: the gap tracker's known origins and pending gaps survive a restart. A new process loads what the store kept
/// (<see cref="IWorkCoordinator.IntegrityLoadGapTrackerStateAsync"/>) once, before it is read, so the audit knows its
/// origins, liveness keeps its last-seen times, and a gap seen before the restart is confirmed by the next checkpoint.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityGapTracker.cs</code-under-test>
[Category("Messaging")]
public class IntegrityGapTrackerDurabilityTests {
  private static readonly DateTimeOffset _t0 = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

  private static IntegrityGapTracker.PendingGap _gap(Guid origin, string type = "Contracts.TypeX") => new() {
    OriginServiceId = origin,
    OriginServiceName = "origin-svc",
    TenantScope = "tenant-a",
    EventType = type,
    FromCommitSequence = 10,
    ToCommitSequence = 20,
    ExpectedCount = 3,
  };

  private sealed class StoreCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public IntegrityGapTrackerState? State { get; set; }
    public int Loads { get; private set; }
    public Exception? FailFirst { get; set; }

    public Task<IntegrityGapTrackerState?> IntegrityLoadGapTrackerStateAsync(CancellationToken cancellationToken = default) {
      Loads++;
      if (FailFirst is { } failure) {
        FailFirst = null;
        throw failure;
      }
      return Task.FromResult(State);
    }
  }

  [Test]
  public async Task Hydrate_LoadsTheKeptOriginsAndPendingGaps_OnceAsync() {
    var origin = TrackedGuid.New().Value;
    var coordinator = new StoreCoordinator {
      State = new IntegrityGapTrackerState(
        [new IntegrityKnownOrigin(origin, "origin-svc", "origin.requests", _t0)],
        [_gap(origin)]),
    };
    var tracker = new IntegrityGapTracker();

    await tracker.EnsureHydratedAsync(coordinator);
    await tracker.EnsureHydratedAsync(coordinator);

    await Assert.That(coordinator.Loads).IsEqualTo(1).Because("one read per service at startup");
    await Assert.That(tracker.GetOrigins()).IsEquivalentTo([(origin, "origin-svc", (string?)"origin.requests")])
      .Because("the audit knows its origins before any new checkpoint arrives");
    await Assert.That(tracker.GetStaleOrigins(TimeSpan.FromMinutes(3), _t0.AddMinutes(10)).Single().LastSeen).IsEqualTo(_t0)
      .Because("liveness keeps the last-seen time from before the restart");
    await Assert.That(tracker.TakePending(origin).Single()).IsEqualTo(_gap(origin))
      .Because("a gap seen before the restart waits for the next checkpoint to confirm it");
  }

  [Test]
  public async Task Hydrate_NeverOverwritesWhatThisProcessAlreadyLearnedAsync() {
    var origin = TrackedGuid.New().Value;
    var coordinator = new StoreCoordinator {
      State = new IntegrityGapTrackerState(
        [new IntegrityKnownOrigin(origin, "old-name", "old.topic", _t0)],
        [_gap(origin)]),
    };
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(origin, "origin-svc", _t0.AddMinutes(5), "origin.requests");
    tracker.AddPending(_gap(origin));

    await tracker.EnsureHydratedAsync(coordinator);

    await Assert.That(tracker.GetRequestTopic(origin)).IsEqualTo("origin.requests").Because("the live checkpoint is newer");
    await Assert.That(tracker.TakePending(origin).Count).IsEqualTo(1).Because("a pending gap is not added twice");
  }

  [Test]
  public async Task Hydrate_AFailedLoad_IsRetriedByTheNextCaller_NotLatchedAsync() {
    var origin = TrackedGuid.New().Value;
    var coordinator = new StoreCoordinator {
      FailFirst = new InvalidOperationException("store unavailable"),
      State = new IntegrityGapTrackerState([new IntegrityKnownOrigin(origin, "origin-svc", null, _t0)], []),
    };
    var tracker = new IntegrityGapTracker();

    await Assert.That(async () => await tracker.EnsureHydratedAsync(coordinator)).Throws<InvalidOperationException>();
    await tracker.EnsureHydratedAsync(coordinator);

    await Assert.That(tracker.GetOrigins().Single().OriginServiceId).IsEqualTo(origin)
      .Because("a failed load must not mark the tracker loaded, or every later read trusts an empty state");
  }

  [Test]
  public async Task Hydrate_AStoreThatKeepsNothing_LeavesTheTrackerAsItIsAsync() {
    var tracker = new IntegrityGapTracker();

    await tracker.EnsureHydratedAsync(new StoreCoordinator { State = null });

    await Assert.That(tracker.GetOrigins()).IsEmpty();
  }

  [Test]
  public async Task PeekPending_ReturnsAnOriginsPendingGaps_WithoutTakingThemAsync() {
    var origin = TrackedGuid.New().Value;
    var tracker = new IntegrityGapTracker();
    tracker.AddPending(_gap(origin));
    tracker.AddPending(_gap(TrackedGuid.New().Value));

    await Assert.That(tracker.PeekPending(origin).Single()).IsEqualTo(_gap(origin));
    await Assert.That(tracker.TakePending(origin).Count).IsEqualTo(1).Because("peeking takes nothing");
  }

  [Test]
  public async Task EnsureHydrated_RefusesANullCoordinatorAsync() {
    await Assert.That(async () => await new IntegrityGapTracker().EnsureHydratedAsync(null!)).Throws<ArgumentNullException>();
  }
}
