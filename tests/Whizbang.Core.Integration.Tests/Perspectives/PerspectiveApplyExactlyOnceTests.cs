using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Integration.Tests.Perspectives;

/// <summary>
/// Lock-in integration tests for the Apply-exactly-once contract at the perspective
/// dispatch layer. The contract: <c>Apply(TModel, TEvent)</c> is invoked exactly once
/// per event per perspective per stream. The projection's <c>Apply</c> methods are NOT
/// required to be idempotent — the framework guarantees single dispatch.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Context — why these tests exist.</strong> A bug surfaced in JDNext where
/// <c>DraftJobEssentialFunctionRowAddedEvent</c>s produced twice as many rows as events
/// (5 events → 10 rows, duplicate RowIds). Investigation showed the event store was
/// correct and no receptor double-fired; the doubling was purely at the
/// <c>Apply(TModel, TEvent)</c> dispatch layer in the generated
/// <c>IPerspectiveRunner</c>. ~35 % of dev streams were affected with 196 exactly-doubled
/// essential-function jobs.
/// </para>
/// <para>
/// Plan: <c>plans/receptor-chaos-scenarios-deferred.md</c> and
/// <c>~/.claude/plans/ok-we-have-an-quirky-newt.md</c>.
/// </para>
/// <para>
/// Four suspect root causes. Tests below target each.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/apply-exactly-once</docs>
[Category("Integration")]
[NotInParallel("PerspectiveApplyExactlyOnceIntegration")]
public class PerspectiveApplyExactlyOnceTests {

  // ==================== Scenario 1: drain-mode + standard-mode do NOT co-fire ====================

  /// <summary>
  /// Suspect #1 in the plan: a stream appears in BOTH <c>WorkBatch.PerspectiveStreamIds</c>
  /// (drain mode) AND <c>WorkBatch.PerspectiveWork</c> (standard mode). Drain mode processes
  /// the stream, then standard mode's <c>Parallel.ForEachAsync</c> loop processes it again.
  /// </summary>
  /// <remarks>
  /// Expected behavior: the runner is invoked AT MOST ONCE per (stream, perspective) per
  /// cycle. Either drain-mode's <c>RunWithEventsAsync</c> OR standard-mode's <c>RunAsync</c>
  /// fires — never both. The guard at <c>PerspectiveWorker.cs:566</c> clears
  /// <c>groupedWork</c> only when drain mode populated <c>batchProcessedEvents</c>; if that
  /// gate has any gap, both paths fire and every event is applied twice.
  /// </remarks>
  [Test]
  [Skip("RED — scenario setup WIP. Expected to fail against current code once the coordinator is wired to return BOTH PerspectiveStreamIds and PerspectiveWork for the same stream.")]
  public async Task DrainModeAndStandardMode_DoNotBothFireForSameStream_Async() {
    // Arrange — coordinator returns a WorkBatch where the same streamId appears in BOTH
    // PerspectiveStreamIds (drain path) AND PerspectiveWork (standard path).
    //
    // TODO: The existing _cursorAwareCoordinator returns new WorkBatch { ..., PerspectiveWork = work }
    // but DOES NOT populate PerspectiveStreamIds. Need a new coordinator that returns BOTH.
    //
    // TODO: Wire _pathTrackingRunner (below) into _singleRunnerRegistry.
    //
    // TODO: Drive PerspectiveWorker.StartAsync for 2 cycles.
    //
    // Assert — the runner should record AT MOST ONE invocation per (streamId, perspectiveName)
    // across (RunWithEventsAsync, RunAsync, RewindAndRunAsync). If BOTH fire, the contract is broken.
    await Assert.That(false).IsTrue().Because("scaffold only — implementation pending");
  }

  // ==================== Scenario 2: IPerspectiveRunner not double-registered ====================

  /// <summary>
  /// Suspect #2 in the plan: the generator or consumer DI code registers the same
  /// <c>IPerspectiveRunner&lt;T&gt;</c> twice, so every <c>ProcessWorkBatchAsync</c> cycle
  /// invokes both registrations.
  /// </summary>
  /// <remarks>
  /// Expected behavior: when the DI container registers the same runner type twice
  /// (possible via <c>services.AddSingleton</c> without <c>TryAdd</c>),
  /// <c>PerspectiveRunnerRegistry</c> resolves ONE instance for a given perspective name.
  /// </remarks>
  [Test]
  [Skip("RED — scenario setup WIP. Needs a double-registration scenario that constructs two IPerspectiveRunner instances for the same PerspectiveType and asserts only one is invoked.")]
  public async Task DoubleRegisteredPerspectiveRunner_OnlyOneInstanceInvoked_Async() {
    // TODO: Register two _pathTrackingRunner instances for the same PerspectiveType.
    // TODO: Drive the worker. Assert only ONE of the two sees invocations.
    await Assert.That(false).IsTrue().Because("scaffold only — implementation pending");
  }

  // ==================== Scenario 3: crash between model save and cursor advance ====================

  /// <summary>
  /// Suspect #3 in the plan: if the model save and cursor advance happen in separate
  /// transactions, a crash between them leaves the model persisted but the cursor
  /// un-advanced. The next cycle re-applies the same events.
  /// </summary>
  /// <remarks>
  /// Uses <see cref="IChaosInjector"/> (Phase 3b primitive) to throw after the model save
  /// but before the cursor commits — if such a checkpoint is added to PerspectiveWorker
  /// at <c>ChaosCheckpoints.PERSPECTIVE_WORKER_BEFORE_COMPLETION_FIRE</c>. That checkpoint
  /// wiring is not yet in place; this test is gated until it is.
  /// </remarks>
  [Test]
  [Skip("RED — requires IChaosInjector checkpoints to be wired into PerspectiveWorker first (see receptor-chaos-scenarios-deferred.md). Crash between model save and cursor advance must NOT cause re-apply on restart.")]
  public async Task CrashBetweenSaveAndCursorAdvance_DoesNotReApplyEvents_Async() {
    // TODO: Register IChaosInjector that throws at PERSPECTIVE_WORKER_BEFORE_COMPLETION_FIRE.
    // TODO: Drive the worker once to trigger the crash mid-commit.
    // TODO: Remove the injector, drive again, assert the runner's RunAsync / RunWithEventsAsync
    // is NOT invoked for events the model already has.
    await Assert.That(false).IsTrue().Because("scaffold only — implementation pending");
  }

  // ==================== Scenario 4: rewind on populated initialModel ====================

  /// <summary>
  /// Suspect #4 in the plan: <c>RewindAndRunAsync</c> → <c>RunFromModelAsync</c> called
  /// with a populated <c>initialModel</c> AND an incorrect <c>replayFromEventId</c>
  /// re-reads already-applied events and dispatches them through <c>ApplyEvent</c>.
  /// </summary>
  /// <remarks>
  /// Uses <see cref="IPerspectiveReplayReader"/> test double (exists per the rewind plan
  /// <c>7c0c336d</c>) to return <c>IsNew=false</c> for already-processed events. Asserts
  /// <c>Apply</c> only fires for the <c>IsNew=true</c> subset.
  /// </remarks>
  [Test]
  [Skip("RED — needs a real Apply hook (not a runner-level hook) to count per-event Apply dispatches. IPerspectiveApplyObserver is proposed in the plan but not yet added to the generated runner template.")]
  public async Task RewindWithPopulatedModel_AppliesOnlyNewEvents_Async() {
    // TODO: Configure IPerspectiveReplayReader test double to mark events 1-5 as IsNew=false
    // and events 6-10 as IsNew=true. Trigger a rewind.
    // TODO: Observe IPerspectiveApplyObserver callbacks (once added to the runner template).
    // TODO: Assert Apply fires exactly once for events 6-10 and NOT at all for events 1-5.
    await Assert.That(false).IsTrue().Because("scaffold only — implementation pending");
  }

  // ==================== Shared test-double infrastructure ====================

  /// <summary>
  /// Runner that records every path invocation — <c>RunAsync</c>, <c>RunWithEventsAsync</c>,
  /// <c>RewindAndRunAsync</c> — so assertions can verify exactly which path dispatched
  /// which events. Extension of the pattern in <c>_rewindTrackingRunner</c>
  /// (RewindScenarioTests.cs) that also tracks drain-mode <c>RunWithEventsAsync</c>.
  /// </summary>
  private sealed class _pathTrackingRunner : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object);
    private int _runAsyncCount;
    private int _runWithEventsAsyncCount;
    private int _rewindAsyncCount;
    private readonly ConcurrentBag<(string Path, Guid StreamId, Guid EventId)> _invocations = [];

    public int RunAsyncCount => _runAsyncCount;
    public int RunWithEventsAsyncCount => _runWithEventsAsyncCount;
    public int RewindAsyncCount => _rewindAsyncCount;
    public IReadOnlyCollection<(string Path, Guid StreamId, Guid EventId)> Invocations => [.. _invocations];

    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _runAsyncCount);
      _invocations.Add(("RunAsync", streamId, lastProcessedEventId ?? Guid.Empty));
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = lastProcessedEventId ?? Guid.Empty,
        Status = PerspectiveProcessingStatus.None
      });
    }

    public Task<PerspectiveCursorCompletion> RunWithEventsAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
        IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _runWithEventsAsyncCount);
      foreach (var envelope in events) {
        _invocations.Add(("RunWithEventsAsync", streamId, envelope.MessageId.Value));
      }
      var lastId = events.Count > 0 ? events[^1].MessageId.Value : lastProcessedEventId ?? Guid.Empty;
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = lastId,
        Status = events.Count > 0 ? PerspectiveProcessingStatus.Completed : PerspectiveProcessingStatus.None,
        EventsProcessed = events.Count
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
        Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _rewindAsyncCount);
      _invocations.Add(("RewindAndRunAsync", streamId, triggeringEventId));
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = triggeringEventId,
        Status = PerspectiveProcessingStatus.Completed,
        EventsProcessed = 1
      });
    }

    public Task BootstrapSnapshotAsync(
        Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }
}
