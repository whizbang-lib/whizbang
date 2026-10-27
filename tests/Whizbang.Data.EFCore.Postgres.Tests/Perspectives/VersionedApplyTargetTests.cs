// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Locks the opt-in strict-ordering contract for <see cref="IVersionedApplyTarget"/>: a model that
/// implements the marker gets a stricter UPSERT <c>WHERE</c> clause so a stale concurrent write
/// with an older <c>metadata.EventId</c> (UUIDv7 lexicographic ordering) cannot overwrite a row
/// that was already advanced by a newer event — even when both writes ship a null
/// <c>metadata.CommitSequence</c>.
///
/// <para>The default (non-opted-in) contract remains last-writer-wins under null CommitSequence
/// — see <c>CrossPodStaleReadRegressionRaceTests</c>. Models opt in by implementing the marker;
/// this is what closed a production cross-pod strand for <c>SagaItemModel</c>
/// without breaking consumers that depend on stamper-lag forwarding.</para>
/// </summary>
[Category("Integration")]
[Category("Storage")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard2")]
public class VersionedApplyTargetTests : EFCoreTestBase {
  // Test model lives in sibling VersionedItemPerspective.cs so the source generator
  // registers PerspectiveRow<VersionedItem> with WorkCoordinationDbContext.

  private static PerspectiveMetadata Meta(string eventType, Guid eventId) =>
    new() {
      EventType = eventType,
      EventId = eventId.ToString("D"),
      Timestamp = DateTime.UtcNow,
      CommitSequence = null,
    };

  // Deterministic UUIDv7-shaped event IDs. Lexicographic comparison: the first bytes
  // encode the emission timestamp, so the "older" id sorts strictly less than the "newer".
  // Hardcoded to avoid Task.Delay-driven nondeterminism (see feedback_no_timing_tests).
  private static readonly Guid _olderEventId = Guid.Parse("019f0000-0000-7aaa-8000-000000000001");
  private static readonly Guid _newerEventId = Guid.Parse("019f0001-0000-7aaa-8000-000000000001");

  // ── RED 1: opt-in stale write must NOT overwrite a newer-EventId row ─────────────────

  [Test]
  public async Task StaleEventWrite_OnVersionedTarget_DoesNotRegressTerminalRowAsync() {
    var id = TrackedGuid.New().Value;
    var strategy = new PostgresUpsertStrategy();
    var scope = new PerspectiveScope();

    await using (var ctxA = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        ctxA, "wh_per_versioned_item", id,
        new VersionedItem { Id = id, State = "Completed", Value = 2 },
        Meta("SagaItemCompletedEvent", _newerEventId),
        scope);
    }

    // Stale (older) event arrives second (Pod B's stale-read Started write).
    await using (var ctxB = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        ctxB, "wh_per_versioned_item", id,
        new VersionedItem { Id = id, State = "Running", Value = 1 },
        Meta("SagaItemStartedEvent", _olderEventId),
        scope);
    }

    await using var read = CreateDbContext();
    var row = await read.Set<PerspectiveRow<VersionedItem>>().AsNoTracking()
      .FirstOrDefaultAsync(r => r.Id == id);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.State).IsEqualTo("Completed")
      .Because("IVersionedApplyTarget opts into UUIDv7 strict-greater ordering at the UPSERT level. " +
               "A stale concurrent write whose metadata.EventId is lexicographically older than the " +
               "persisted row's EventId must be rejected — closes the production cross-pod strand on " +
               "SagaItemModel that survived even with v0.740's stream-affinity gate.");
    await Assert.That(row.Data.Value).IsEqualTo(2)
      .Because("Forward write's payload preserved end-to-end (Value tracks State to surface partial-revert bugs).");
  }

  // ── RED 2: opt-in newer write progresses normally ─────────────────────────────────────

  [Test]
  public async Task NewerEventWrite_OnVersionedTarget_AdvancesRowAsync() {
    var id = TrackedGuid.New().Value;
    var strategy = new PostgresUpsertStrategy();
    var scope = new PerspectiveScope();

    // First write (Running) with the older event id.
    await using (var ctxA = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        ctxA, "wh_per_versioned_item", id,
        new VersionedItem { Id = id, State = "Running", Value = 1 },
        Meta("SagaItemStartedEvent", _olderEventId),
        scope);
    }

    // Second write (Completed) with a strictly newer UUIDv7 EventId.
    await using (var ctxB = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        ctxB, "wh_per_versioned_item", id,
        new VersionedItem { Id = id, State = "Completed", Value = 2 },
        Meta("SagaItemCompletedEvent", _newerEventId),
        scope);
    }

    await using var read = CreateDbContext();
    var row = await read.Set<PerspectiveRow<VersionedItem>>().AsNoTracking()
      .FirstAsync(r => r.Id == id);

    await Assert.That(row.Data.State).IsEqualTo("Completed")
      .Because("Forward progress with strictly-newer EventId MUST be applied — the opt-in must not " +
               "over-correct and reject legitimate advancement.");
  }

  // ── A write at the stored position lands (#1608) ─────────────────────────────────────

  [Test]
  public async Task WriteAtTheStoredEventId_OnVersionedTarget_LandsAsync() {
    var id = TrackedGuid.New().Value;
    var strategy = new PostgresUpsertStrategy();
    var scope = new PerspectiveScope();

    var eventId = TrackedGuid.New().Value;
    var meta = Meta("SagaItemCompletedEvent", eventId);

    await using (var ctxA = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        ctxA, "wh_per_versioned_item", id,
        new VersionedItem { Id = id, State = "Completed", Value = 2 },
        meta, scope);
    }
    // A re-fold of the stream (a rewind or a rebuild) records the stream's frontier, which is the event the row
    // already holds when the late event sits below the newest one, and carries the corrected state.
    await using (var ctxB = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        ctxB, "wh_per_versioned_item", id,
        new VersionedItem { Id = id, State = "Completed", Value = 3 },
        meta, scope);
    }

    await using var read = CreateDbContext();
    var row = await read.Set<PerspectiveRow<VersionedItem>>().AsNoTracking()
      .FirstAsync(r => r.Id == id);

    await Assert.That(row.Data.Value).IsEqualTo(3)
      .Because("a write at the stored event id is a re-fold of the same stream position: refusing it refused every "
             + "rewind of a row that had applied its newest event first, and the late event it carried was lost. A "
             + "redelivery of the row's own event never reaches the store: the runner's idempotency filter drops it.");
    await Assert.That(row.Version).IsEqualTo(2);
  }

  // ── A rebuild's write is not ordered on the event id (#1819) ─────────────────────────

  private const string VERSIONED_TABLE = "wh_per_versioned_item";

  private async Task<bool> _writeAsync(Guid id, string state, Guid eventId, PerspectiveRowVersion expected) {
    await using var ctx = CreateDbContext();
    return await new EFCorePostgresPerspectiveStore<VersionedItem>(ctx, VERSIONED_TABLE).TryUpsertAsync(
      id, new VersionedItem { Id = id, State = state, Value = 1 }, new PerspectiveScope(), false,
      Meta("SagaItemEvent", eventId), expected);
  }

  private async Task<PerspectiveRowVersion> _versionAsync(Guid id) {
    await using var ctx = CreateDbContext();
    return (await new EFCorePostgresPerspectiveStore<VersionedItem>(ctx, VERSIONED_TABLE).ReadForApplyAsync(id)).Version;
  }

  private async Task<string> _stateAsync(Guid id) {
    await using var read = CreateDbContext();
    return (await read.Set<PerspectiveRow<VersionedItem>>().AsNoTracking().FirstAsync(r => r.Id == id)).Data.State;
  }

  private static async Task<T> _rebuildingAsync<T>(Func<Task<T>> write) {
    var previous = ProcessingModeAccessor.Current;
    ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
    try {
      return await write();
    } finally {
      ProcessingModeAccessor.Current = previous;
    }
  }

  /// <summary>The live apply keeps its event-id ordering, on a checked write and an unchecked one alike.</summary>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task LiveWriteWithAnEarlierEventId_OnVersionedTarget_IsRefusedAsync(bool checkedWrite) {
    var id = TrackedGuid.New().Value;
    await _writeAsync(id, "Later", _newerEventId, PerspectiveRowVersion.Unchecked);
    var version = checkedWrite ? await _versionAsync(id) : PerspectiveRowVersion.Unchecked;

    var written = await _writeAsync(id, "Earlier", _olderEventId, version);

    await Assert.That(written).IsFalse();
    await Assert.That(await _stateAsync(id)).IsEqualTo("Later")
      .Because("a live event the row has already moved past is skipped, not applied backwards");
  }

  /// <summary>
  /// A rebuild's write lands though its event id sorts below the stored one: the rebuild re-derived the row from
  /// every event, so the event id of the last one it folded by stream version is not a position the row can be past.
  /// </summary>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task RebuildWriteWithAnEarlierEventId_OnVersionedTarget_LandsAsync(bool checkedWrite) {
    var id = TrackedGuid.New().Value;
    await _writeAsync(id, "Later", _newerEventId, PerspectiveRowVersion.Unchecked);
    var version = checkedWrite ? await _versionAsync(id) : PerspectiveRowVersion.Unchecked;

    var written = await _rebuildingAsync(() => _writeAsync(id, "Rebuilt", _olderEventId, version));

    await Assert.That(written).IsTrue();
    await Assert.That(await _stateAsync(id)).IsEqualTo("Rebuilt");
  }

  /// <summary>A rebuild's write on a version something else has moved past is still refused on the row version.</summary>
  [Test]
  public async Task RebuildWriteOnAStaleVersion_OnVersionedTarget_IsStillRefusedAsync() {
    var id = TrackedGuid.New().Value;
    await _writeAsync(id, "First", _olderEventId, PerspectiveRowVersion.Unchecked);
    var stale = await _versionAsync(id);
    await _writeAsync(id, "Collective", _newerEventId, PerspectiveRowVersion.Unchecked);

    await Assert.That(async () => await _rebuildingAsync(() => _writeAsync(id, "Stale", _newerEventId, stale)))
      .Throws<PerspectiveRowConflictException>()
      .Because("the rebuild folded onto a row that has since moved, which the row version refuses whatever the mode");
    await Assert.That(await _stateAsync(id)).IsEqualTo("Collective");
  }
}
