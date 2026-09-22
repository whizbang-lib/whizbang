using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
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
// Mutates the process-wide BaseUpsertStrategy.PathOnePersistenceOptionsProvider — serialize against the
// other persistence tests so it can't flip the provider mid-seed (the cross-test static race).
[NotInParallel("EFCorePostgresTests")]
[Category("Shard2")]
public class VersionedApplyTargetTests : EFCoreTestBase {
  [After(Test)]
  public Task ClearPathOneProviderAsync() {
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;
    return Task.CompletedTask;
  }

  private static void EnableAtomicPath() {
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () =>
      PerspectivePersistenceJsonContext.CreateOptions(
        MessageJsonContext.Default,
        global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);
  }

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
    EnableAtomicPath();
    var id = TrackedGuid.NewMedo().Value;
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
    EnableAtomicPath();
    var id = TrackedGuid.NewMedo().Value;
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

  // ── RED 3: idempotent re-apply of same EventId is a no-op (does not bump version) ─────

  [Test]
  public async Task IdempotentReapply_OnVersionedTarget_DoesNotBumpVersionAsync() {
    EnableAtomicPath();
    var id = TrackedGuid.NewMedo().Value;
    var strategy = new PostgresUpsertStrategy();
    var scope = new PerspectiveScope();

    var eventId = TrackedGuid.NewMedo().Value;
    var meta = Meta("SagaItemCompletedEvent", eventId);

    await using (var ctxA = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        ctxA, "wh_per_versioned_item", id,
        new VersionedItem { Id = id, State = "Completed", Value = 2 },
        meta, scope);
    }
    // Re-apply the same event (transport redelivery / consumer retry).
    await using (var ctxB = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        ctxB, "wh_per_versioned_item", id,
        new VersionedItem { Id = id, State = "Completed", Value = 2 },
        meta, scope);
    }

    await using var read = CreateDbContext();
    var row = await read.Set<PerspectiveRow<VersionedItem>>().AsNoTracking()
      .FirstAsync(r => r.Id == id);

    await Assert.That(row.Version).IsEqualTo(1)
      .Because("Strict-greater EventId comparison: same EventId means same event being re-applied — " +
               "the UPSERT WHERE filter MUST skip the redundant UPDATE so the version stays at 1, " +
               "matching the at-most-once Apply contract.");
  }
}
