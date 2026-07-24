using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks the find-or-create + IsTerminal-dedup + counter-bump pattern
/// that consumer Apply methods delegate to. Apply purity invariant: the
/// helpers only mutate the saga's own state — no I/O, no event
/// emission. Idempotency invariant: replaying the same terminal event
/// twice must not double-count.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaApplyHelperTests {

  private static readonly Guid _sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
  private const string SAGA_NAME = "TestSaga";
  private const string ITEM_ID = "item-1";
  private static readonly DateTimeOffset _ts = DateTimeOffset.Parse("2026-06-22T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

  // ── TrackCompleted ───────────────────────────────────────────────────

  [Test]
  public async Task TrackCompleted_NewItem_CreatesAndIncrementsCompletedAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 10 };
    var items = new List<SagaItemModel>();

    SagaApplyHelper.TrackCompleted(saga, items, _sagaId, SAGA_NAME, ITEM_ID, _ts);

    await Assert.That(items.Count).IsEqualTo(1);
    await Assert.That(items[0].ItemIdentifier).IsEqualTo(ITEM_ID);
    await Assert.That(items[0].State).IsEqualTo(SagaItemState.Completed);
    await Assert.That(items[0].CompletedAt).IsEqualTo(_ts);
    await Assert.That(items[0].StartedAt).IsEqualTo(_ts);
    await Assert.That(saga.CompletedItems).IsEqualTo(1);
    await Assert.That(saga.UpdatedAt).IsEqualTo(_ts);
  }

  [Test]
  public async Task TrackCompleted_ExistingNonTerminalItem_TransitionsAndIncrementsAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 10 };
    var items = new List<SagaItemModel> {
      new() { ItemIdentifier = ITEM_ID, State = SagaItemState.Running, StartedAt = _ts.AddMinutes(-5) },
    };

    SagaApplyHelper.TrackCompleted(saga, items, _sagaId, SAGA_NAME, ITEM_ID, _ts);

    await Assert.That(items.Count).IsEqualTo(1);
    await Assert.That(items[0].State).IsEqualTo(SagaItemState.Completed);
    await Assert.That(items[0].CompletedAt).IsEqualTo(_ts);
    await Assert.That(items[0].StartedAt).IsEqualTo(_ts.AddMinutes(-5))
      .Because("StartedAt was already set by an earlier Apply; it must not be overwritten by the Completed event.");
    await Assert.That(saga.CompletedItems).IsEqualTo(1);
  }

  [Test]
  public async Task TrackCompleted_AlreadyTerminalItem_DoesNotDoubleCountAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 10, CompletedItems = 1 };
    var items = new List<SagaItemModel> {
      new() { ItemIdentifier = ITEM_ID, State = SagaItemState.Completed, CompletedAt = _ts.AddMinutes(-5) },
    };

    SagaApplyHelper.TrackCompleted(saga, items, _sagaId, SAGA_NAME, ITEM_ID, _ts);

    await Assert.That(saga.CompletedItems).IsEqualTo(1)
      .Because("Replay of a terminal event must not double the count. IsTerminal guard is the idempotency primitive — without it, a single rewind would silently inflate every saga's metrics.");
  }

  [Test]
  public async Task TrackCompleted_LastItem_TriggersTryCompleteAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 2, CompletedItems = 1 };
    var items = new List<SagaItemModel>();

    SagaApplyHelper.TrackCompleted(saga, items, _sagaId, SAGA_NAME, ITEM_ID, _ts);

    await Assert.That(saga.Status).IsEqualTo(SagaStatus.Completed)
      .Because("After bumping CompletedItems to 2 (== TotalItems), TryComplete must fire from inside the helper — otherwise consumers would have to remember to call it after every Track* invocation.");
    await Assert.That(saga.CompletedByItemIdentifier).IsEqualTo(ITEM_ID);
  }

  // ── TrackFailed ──────────────────────────────────────────────────────

  [Test]
  public async Task TrackFailed_NewItem_CreatesAndIncrementsFailedAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 10 };
    var items = new List<SagaItemModel>();

    SagaApplyHelper.TrackFailed(saga, items, _sagaId, SAGA_NAME, ITEM_ID, "boom", _ts, errorDetails: "stack trace");

    await Assert.That(items.Count).IsEqualTo(1);
    await Assert.That(items[0].State).IsEqualTo(SagaItemState.Failed);
    await Assert.That(items[0].ErrorMessage).IsEqualTo("boom");
    await Assert.That(items[0].ErrorDetails).IsEqualTo("stack trace");
    await Assert.That(saga.FailedItems).IsEqualTo(1);
  }

  [Test]
  public async Task TrackFailed_AlreadyTerminalItem_DoesNotDoubleCountAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 10, FailedItems = 1 };
    var items = new List<SagaItemModel> {
      new() { ItemIdentifier = ITEM_ID, State = SagaItemState.Failed },
    };

    SagaApplyHelper.TrackFailed(saga, items, _sagaId, SAGA_NAME, ITEM_ID, "different error", _ts);

    await Assert.That(saga.FailedItems).IsEqualTo(1)
      .Because("Idempotent — replay must not double-count, even when the error message text differs.");
  }

  [Test]
  public async Task TrackFailed_LastItem_TriggersTryCompleteWithFailuresAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 2, CompletedItems = 1 };
    var items = new List<SagaItemModel>();

    SagaApplyHelper.TrackFailed(saga, items, _sagaId, SAGA_NAME, ITEM_ID, "boom", _ts);

    await Assert.That(saga.Status).IsEqualTo(SagaStatus.CompletedWithFailures)
      .Because("Continue-on-failure: when the last item fails, the saga still completes (with failures) so downstream consumers learn the saga finished.");
  }

  // ── TrackFailedFast ──────────────────────────────────────────────────

  [Test]
  public async Task TrackFailedFast_NewItem_CreatesAndAbortsImmediatelyAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 100, CompletedItems = 5 };
    var items = new List<SagaItemModel>();

    SagaApplyHelper.TrackFailedFast(saga, items, _sagaId, SAGA_NAME, ITEM_ID, "boom", _ts);

    await Assert.That(saga.Status).IsEqualTo(SagaStatus.Failed)
      .Because("Fail-fast aborts at the first failure without waiting for remaining 94 items.");
    await Assert.That(saga.FailedItems).IsEqualTo(1);
    await Assert.That(items[0].State).IsEqualTo(SagaItemState.Failed);
  }

  [Test]
  public async Task TrackFailedFast_AlreadyTerminalItem_DoesNotDoubleCountAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 100, FailedItems = 1 };
    var items = new List<SagaItemModel> {
      new() { ItemIdentifier = ITEM_ID, State = SagaItemState.Failed },
    };

    SagaApplyHelper.TrackFailedFast(saga, items, _sagaId, SAGA_NAME, ITEM_ID, "different error", _ts);

    await Assert.That(saga.FailedItems).IsEqualTo(1);
  }

  // ── Apply purity (no time/random/IO leakage) ─────────────────────────

  [Test]
  public async Task AllHelpers_UseCallerSuppliedTimestamp_NotDateTimeOffsetUtcNowAsync() {
    // Apply must be deterministic during replay — a helper that reaches
    // for DateTimeOffset.UtcNow would break replay. We assert this by
    // passing an arbitrary past timestamp and verifying it round-trips.
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 3 };
    var items = new List<SagaItemModel>();
    var pastTs = DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    SagaApplyHelper.TrackCompleted(saga, items, _sagaId, SAGA_NAME, ITEM_ID, pastTs);

    await Assert.That(items[0].CompletedAt).IsEqualTo(pastTs)
      .Because("Replay supplies historical timestamps; a helper using DateTimeOffset.UtcNow would silently rewrite history on every rebuild.");
    await Assert.That(saga.UpdatedAt).IsEqualTo(pastTs);
  }
}
