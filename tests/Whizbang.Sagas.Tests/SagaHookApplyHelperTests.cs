using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks the find-or-create + idempotency + late-Started-synthesis
/// patterns on <see cref="SagaHookApplyHelper"/>. Hook bookend events
/// can arrive out of order (consumer's outbox + transport don't enforce
/// per-hook ordering), so the helper must accept either a Started → Completed
/// happy path OR a Completed-arrives-without-Started synthesis path
/// without surfacing inconsistent state.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaHookApplyHelperTests {

  private static readonly DateTimeOffset _ts = DateTimeOffset.Parse("2026-06-22T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

  // ── DeclareHooks ─────────────────────────────────────────────────────

  [Test]
  public async Task DeclareHooks_NewNames_AddsPendingRowsAsync() {
    var saga = new BaseSagaModel();

    SagaHookApplyHelper.DeclareHooks(saga, ["pre-archive", "post-notify"], _ts);

    await Assert.That(saga.Hooks.Count).IsEqualTo(2);
    await Assert.That(saga.Hooks[0].HookName).IsEqualTo("pre-archive");
    await Assert.That(saga.Hooks[0].Status).IsEqualTo(SagaItemState.Pending);
    await Assert.That(saga.Hooks[0].CreatedAt).IsEqualTo(_ts);
  }

  [Test]
  public async Task DeclareHooks_ExistingName_DoesNotDuplicateAsync() {
    var saga = new BaseSagaModel();
    saga.Hooks.Add(new SagaHookExecution { HookName = "pre-archive", Status = SagaItemState.Running, CreatedAt = _ts.AddMinutes(-5) });

    SagaHookApplyHelper.DeclareHooks(saga, ["pre-archive", "post-notify"], _ts);

    await Assert.That(saga.Hooks.Count).IsEqualTo(2)
      .Because("Replay of SagaInitiatedEvent must not duplicate existing hook rows — DeclareHooks is idempotent.");
    await Assert.That(saga.Hooks[0].Status).IsEqualTo(SagaItemState.Running)
      .Because("Existing rows must not be overwritten — preserves in-flight state across rewind.");
  }

  // ── TrackHookStarted ─────────────────────────────────────────────────

  [Test]
  public async Task TrackHookStarted_NewHook_CreatesRunningRowAsync() {
    var saga = new BaseSagaModel();

    SagaHookApplyHelper.TrackHookStarted(saga, hookName: "pre-archive", displayName: "Pre-Archive", timestamp: _ts);

    await Assert.That(saga.Hooks.Count).IsEqualTo(1);
    await Assert.That(saga.Hooks[0].Status).IsEqualTo(SagaItemState.Running);
    await Assert.That(saga.Hooks[0].StartedAt).IsEqualTo(_ts);
    await Assert.That(saga.Hooks[0].DisplayName).IsEqualTo("Pre-Archive")
      .Because("DisplayName arrives on the Started event; carrying it onto the projection row enables UI render without consumer wiring.");
  }

  [Test]
  public async Task TrackHookStarted_ExistingPendingHook_TransitionsToRunningAsync() {
    var saga = new BaseSagaModel();
    saga.Hooks.Add(new SagaHookExecution { HookName = "pre-archive", Status = SagaItemState.Pending });

    SagaHookApplyHelper.TrackHookStarted(saga, hookName: "pre-archive", displayName: null, timestamp: _ts);

    await Assert.That(saga.Hooks[0].Status).IsEqualTo(SagaItemState.Running);
    await Assert.That(saga.Hooks[0].StartedAt).IsEqualTo(_ts);
  }

  [Test]
  public async Task TrackHookStarted_DoesNotMoveStartedAtForwardOnReplayAsync() {
    var saga = new BaseSagaModel();
    saga.Hooks.Add(new SagaHookExecution { HookName = "pre-archive", Status = SagaItemState.Running, StartedAt = _ts.AddMinutes(-5) });

    SagaHookApplyHelper.TrackHookStarted(saga, hookName: "pre-archive", displayName: null, timestamp: _ts);

    await Assert.That(saga.Hooks[0].StartedAt).IsEqualTo(_ts.AddMinutes(-5))
      .Because("StartedAt records the first transition. Out-of-order or replayed events must not silently rewrite it.");
  }

  [Test]
  public async Task TrackHookStarted_TerminalHook_IsNoopAsync() {
    var saga = new BaseSagaModel();
    saga.Hooks.Add(new SagaHookExecution { HookName = "pre-archive", Status = SagaItemState.Completed });

    SagaHookApplyHelper.TrackHookStarted(saga, hookName: "pre-archive", displayName: null, timestamp: _ts);

    await Assert.That(saga.Hooks[0].Status).IsEqualTo(SagaItemState.Completed)
      .Because("A late Started after Completed must not regress the terminal state.");
  }

  // ── TrackHookCompleted ───────────────────────────────────────────────

  [Test]
  public async Task TrackHookCompleted_ExistingRunningHook_TransitionsAsync() {
    var saga = new BaseSagaModel();
    saga.Hooks.Add(new SagaHookExecution { HookName = "pre-archive", Status = SagaItemState.Running });

    SagaHookApplyHelper.TrackHookCompleted(saga, hookName: "pre-archive", finalStatus: SagaItemState.Completed, errorMessage: null, errorDetails: null, timestamp: _ts);

    await Assert.That(saga.Hooks[0].Status).IsEqualTo(SagaItemState.Completed);
    await Assert.That(saga.Hooks[0].CompletedAt).IsEqualTo(_ts);
  }

  [Test]
  public async Task TrackHookCompleted_MissingStartedEvent_SynthesizesRowAsync() {
    var saga = new BaseSagaModel();

    SagaHookApplyHelper.TrackHookCompleted(saga, hookName: "pre-archive", finalStatus: SagaItemState.Failed, errorMessage: "boom", errorDetails: "stack", timestamp: _ts);

    await Assert.That(saga.Hooks.Count).IsEqualTo(1)
      .Because("Late or missed Started events shouldn't lose the Completed signal — the helper synthesizes a row so completion always records.");
    await Assert.That(saga.Hooks[0].Status).IsEqualTo(SagaItemState.Failed);
    await Assert.That(saga.Hooks[0].ErrorMessage).IsEqualTo("boom");
    await Assert.That(saga.Hooks[0].ErrorDetails).IsEqualTo("stack");
  }

  [Test]
  public async Task TrackHookCompleted_AlreadyTerminal_IsNoopAsync() {
    var saga = new BaseSagaModel();
    saga.Hooks.Add(new SagaHookExecution { HookName = "pre-archive", Status = SagaItemState.Completed, ErrorMessage = null });

    SagaHookApplyHelper.TrackHookCompleted(saga, hookName: "pre-archive", finalStatus: SagaItemState.Failed, errorMessage: "rewrite", errorDetails: null, timestamp: _ts);

    await Assert.That(saga.Hooks[0].Status).IsEqualTo(SagaItemState.Completed)
      .Because("Replay/rewind of Completed must not rewrite an already-terminal row's status or error.");
    await Assert.That(saga.Hooks[0].ErrorMessage).IsNull();
  }
}
