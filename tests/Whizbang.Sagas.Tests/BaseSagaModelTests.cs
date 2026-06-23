using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks the saga-level state machine on <see cref="BaseSagaModel"/> —
/// Pending → Running transition, terminal-state transitions
/// (Completed / CompletedWithFailures / Failed), and the
/// CompletionEventDispatched flag that gates duplicate-completion
/// emission. Every consumer projection inherits these transitions; a
/// silent regression here cascades into every saga.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class BaseSagaModelTests {

  // ── Defaults ─────────────────────────────────────────────────────────

  [Test]
  public async Task Defaults_StatusPendingAndCountsZeroAsync() {
    var saga = new BaseSagaModel();

    await Assert.That(saga.Status).IsEqualTo(SagaStatus.Pending);
    await Assert.That(saga.TotalItems).IsEqualTo(0);
    await Assert.That(saga.CompletedItems).IsEqualTo(0);
    await Assert.That(saga.FailedItems).IsEqualTo(0);
    await Assert.That(saga.CompletionEventDispatched).IsFalse();
    await Assert.That(saga.CompletedByItemIdentifier).IsNull();
    await Assert.That(saga.StartedAt).IsNull();
    await Assert.That(saga.CompletedAt).IsNull();
  }

  [Test]
  public async Task Hooks_LazyInitToEmptyListAsync() {
    var saga = new BaseSagaModel();

    await Assert.That(saga.Hooks).IsNotNull()
      .Because("Lazy init prevents NullReferenceException when consumer code touches Hooks on a pre-Rule-17 row that has no JSONB key for it.");
    await Assert.That(saga.Hooks.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetItems_DefaultsToEmptyAsync() {
    var saga = new BaseSagaModel();

    await Assert.That(saga.GetItems()).IsEmpty()
      .Because("Sagas that track only counters (no per-item embed) must return [] without overriding — TryComplete uses counts, not items.");
  }

  // ── MarkRunningIfPending ─────────────────────────────────────────────

  [Test]
  public async Task MarkRunningIfPending_FromPending_TransitionsToRunningAsync() {
    var saga = new BaseSagaModel();
    var ts = DateTimeOffset.Parse("2026-06-22T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    saga.MarkRunningIfPending(ts);

    await Assert.That(saga.Status).IsEqualTo(SagaStatus.Running);
    await Assert.That(saga.StartedAt).IsEqualTo(ts);
  }

  [Test]
  public async Task MarkRunningIfPending_FromRunning_IsNoopAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Running, StartedAt = DateTimeOffset.Parse("2026-06-22T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture) };
    var laterTs = DateTimeOffset.Parse("2026-06-22T11:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    saga.MarkRunningIfPending(laterTs);

    await Assert.That(saga.Status).IsEqualTo(SagaStatus.Running);
    await Assert.That(saga.StartedAt).IsEqualTo(DateTimeOffset.Parse("2026-06-22T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture))
      .Because("StartedAt records when the saga first transitioned to Running. Subsequent MarkRunningIfPending calls must not move it forward — otherwise out-of-order item events would drift StartedAt monotonically.");
  }

  [Test]
  public async Task MarkRunningIfPending_FromTerminal_IsNoopAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Completed };

    saga.MarkRunningIfPending(DateTimeOffset.UtcNow);

    await Assert.That(saga.Status).IsEqualTo(SagaStatus.Completed)
      .Because("A completed saga must not re-enter Running — the transition is monotonic except via SagaResetEvent.");
  }

  // ── TryComplete ──────────────────────────────────────────────────────

  [Test]
  public async Task TryComplete_StatusNotRunning_ReturnsFalseAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Pending, TotalItems = 1, CompletedItems = 1 };

    var result = saga.TryComplete("item-1", DateTimeOffset.UtcNow);

    await Assert.That(result).IsFalse();
    await Assert.That(saga.Status).IsEqualTo(SagaStatus.Pending);
  }

  [Test]
  public async Task TryComplete_TotalItemsZero_ReturnsFalseAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 0 };

    var result = saga.TryComplete("item-1", DateTimeOffset.UtcNow);

    await Assert.That(result).IsFalse()
      .Because("A downstream saga with TotalItems still unknown (0) must not prematurely complete — TotalItems is filled later via UpdateTotalItems.");
  }

  [Test]
  public async Task TryComplete_NotAllItemsTerminal_ReturnsFalseAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 10, CompletedItems = 7, FailedItems = 2 };

    var result = saga.TryComplete("item-9", DateTimeOffset.UtcNow);

    await Assert.That(result).IsFalse()
      .Because("Completed + Failed (= 9) is still less than Total (10); one item is still in flight.");
  }

  [Test]
  public async Task TryComplete_AllCompletedNoFailures_TransitionsToCompletedAsync() {
    var ts = DateTimeOffset.Parse("2026-06-22T10:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 5, CompletedItems = 5 };

    var result = saga.TryComplete("item-5", ts);

    await Assert.That(result).IsTrue();
    await Assert.That(saga.Status).IsEqualTo(SagaStatus.Completed);
    await Assert.That(saga.CompletedAt).IsEqualTo(ts);
    await Assert.That(saga.CompletedByItemIdentifier).IsEqualTo("item-5");
  }

  [Test]
  public async Task TryComplete_AllTerminalWithFailures_TransitionsToCompletedWithFailuresAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 10, CompletedItems = 7, FailedItems = 3 };

    var result = saga.TryComplete("item-10", DateTimeOffset.UtcNow);

    await Assert.That(result).IsTrue();
    await Assert.That(saga.Status).IsEqualTo(SagaStatus.CompletedWithFailures)
      .Because("Distinguishing CompletedWithFailures from Completed lets UI surface partial-success outcomes without parsing failure counts.");
  }

  // ── TryFailFast ──────────────────────────────────────────────────────

  [Test]
  public async Task TryFailFast_StatusNotRunning_ReturnsFalseAsync() {
    var saga = new BaseSagaModel { Status = SagaStatus.Pending };

    var result = saga.TryFailFast("item-1", DateTimeOffset.UtcNow);

    await Assert.That(result).IsFalse();
    await Assert.That(saga.Status).IsEqualTo(SagaStatus.Pending);
  }

  [Test]
  public async Task TryFailFast_FromRunning_TransitionsToFailedAsync() {
    var ts = DateTimeOffset.Parse("2026-06-22T10:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
    var saga = new BaseSagaModel { Status = SagaStatus.Running, TotalItems = 100, CompletedItems = 3 };

    var result = saga.TryFailFast("item-4", ts);

    await Assert.That(result).IsTrue();
    await Assert.That(saga.Status).IsEqualTo(SagaStatus.Failed)
      .Because("Fail-fast aborts at the first item failure without waiting for remaining items — used by sagas where partial state is unrecoverable (e.g. embeddings).");
    await Assert.That(saga.CompletedAt).IsEqualTo(ts);
    await Assert.That(saga.CompletedByItemIdentifier).IsEqualTo("item-4");
  }

  // ── UpdateTotalItems ─────────────────────────────────────────────────

  [Test]
  public async Task UpdateTotalItems_AssignsCountAndUpdatedAtAsync() {
    var ts = DateTimeOffset.Parse("2026-06-22T10:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
    var saga = new BaseSagaModel { TotalItems = 0 };

    saga.UpdateTotalItems(42, ts);

    await Assert.That(saga.TotalItems).IsEqualTo(42);
    await Assert.That(saga.UpdatedAt).IsEqualTo(ts);
  }
}
