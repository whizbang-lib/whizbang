using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Locks the A1 increment-4 wiring: <see cref="StreamCloser"/> fires the E2 <see cref="IDestructionHook"/>
/// around a "close the books" truncate — <c>OnBeforeDestructionAsync</c> awaited BEFORE the close (so a
/// receptor commits the carry-forward / archive on the critical path), then the gated truncate, then
/// <c>OnAfterDestructionAsync</c> after. Cancel / Defer veto the close; a throwing pre-hook ABORTS it (durable
/// detail is never truncated when the preserve-work failed); a throwing post-hook is non-fatal. Inert
/// pass-through with no hook.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class StreamCloserTests {
  private sealed class RecordingHook : IDestructionHook {
    private readonly List<string> _log;
    private readonly DestructionResult _result;
    private readonly bool _throwOnBefore;
    private readonly bool _throwOnAfter;
    public DestructionReason LastReason { get; private set; }
    public DestructionGranularity LastGranularity { get; private set; }

    public RecordingHook(List<string> log, DestructionResult? result = null,
        bool throwOnBefore = false, bool throwOnAfter = false) {
      _log = log; _result = result ?? DestructionResult.Proceed();
      _throwOnBefore = throwOnBefore; _throwOnAfter = throwOnAfter;
    }

    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      _log.Add("before");
      LastReason = context.Reason;
      LastGranularity = context.Granularity;
      if (_throwOnBefore) {
        throw new InvalidOperationException("carry-forward failed");
      }
      return ValueTask.FromResult(_result);
    }

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      _log.Add("after");
      if (_throwOnAfter) {
        throw new InvalidOperationException("notify failed");
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeCloseCoordinator : IWorkCoordinator {
    private readonly List<string> _log;
    private readonly StreamCloseResult _result;
    public int CloseCalls { get; private set; }
    public (Guid StreamId, long Through, bool Archive)? LastCall { get; private set; }

    public IReadOnlyList<string> ConsumingNames { get; init; } = [];

    public FakeCloseCoordinator(List<string> log, StreamCloseResult? result = null) {
      _log = log; _result = result ?? new StreamCloseResult("closed", 3);
    }

    public Task<StreamCloseResult> CloseStreamAsync(Guid streamId, long throughVersion, bool archive = false, CancellationToken cancellationToken = default) {
      CloseCalls++;
      LastCall = (streamId, throughVersion, archive);
      _log.Add("close");
      return Task.FromResult(_result);
    }

    public Task<IReadOnlyList<string>> GetConsumingPerspectiveNamesAsync(Guid streamId, long throughVersion, CancellationToken cancellationToken = default) =>
      Task.FromResult(ConsumingNames);

    // Unused IWorkCoordinator surface.
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }

  private static StreamCloser _closer(FakeCloseCoordinator coord, IDestructionHook? hook) =>
    new(coord, NullLogger<StreamCloser>.Instance, hook);

  [Test]
  public async Task Close_NoHook_PassesThroughToCoordinatorAsync() {
    var log = new List<string>();
    var coord = new FakeCloseCoordinator(log);
    var streamId = Guid.NewGuid();

    var result = await _closer(coord, hook: null).CloseAsync(streamId, throughVersion: 42, archive: true);

    await Assert.That(result.Status).IsEqualTo("closed");
    await Assert.That(coord.CloseCalls).IsEqualTo(1);
    await Assert.That(coord.LastCall!.Value.StreamId).IsEqualTo(streamId);
    await Assert.That(coord.LastCall!.Value.Through).IsEqualTo(42L);
    await Assert.That(coord.LastCall!.Value.Archive).IsTrue()
      .Because("With no hook the closer is a thin pass-through carrying the args through unchanged.");
  }

  [Test]
  public async Task Close_HookProceeds_FiresBeforeThenCloseThenAfterAsync() {
    var log = new List<string>();
    var coord = new FakeCloseCoordinator(log);
    var hook = new RecordingHook(log);

    var result = await _closer(coord, hook).CloseAsync(Guid.NewGuid(), throughVersion: 10);

    await Assert.That(result.Status).IsEqualTo("closed");
    await Assert.That(string.Join(",", log)).IsEqualTo("before,close,after")
      .Because("The pre-hook is awaited BEFORE the truncate (carry-forward on the critical path); the post-hook runs after.");
    await Assert.That(hook.LastReason).IsEqualTo(DestructionReason.PeriodClose);
    await Assert.That(hook.LastGranularity).IsEqualTo(DestructionGranularity.Stream)
      .Because("A close is a stream-granularity PeriodClose destruction.");
  }

  [Test]
  public async Task Close_HookCancels_DoesNotTruncateAsync() {
    var log = new List<string>();
    var coord = new FakeCloseCoordinator(log);
    var hook = new RecordingHook(log, DestructionResult.Cancelled);

    var result = await _closer(coord, hook).CloseAsync(Guid.NewGuid(), throughVersion: 10);

    await Assert.That(result.Status).IsEqualTo("cancelled");
    await Assert.That(coord.CloseCalls).IsEqualTo(0)
      .Because("A hook that cancels vetoes the close — nothing is truncated.");
    await Assert.That(string.Join(",", log)).IsEqualTo("before")
      .Because("No truncate, and no post-hook when the close didn't happen.");
  }

  [Test]
  public async Task Close_HookDefers_DoesNotTruncateAsync() {
    var log = new List<string>();
    var coord = new FakeCloseCoordinator(log);
    var hook = new RecordingHook(log, DestructionResult.Defer(DateTimeOffset.UtcNow.AddHours(1)));

    var result = await _closer(coord, hook).CloseAsync(Guid.NewGuid(), throughVersion: 10);

    await Assert.That(result.Status).IsEqualTo("deferred");
    await Assert.That(coord.CloseCalls).IsEqualTo(0)
      .Because("A hook that defers postpones the close — nothing is truncated this call.");
  }

  [Test]
  public async Task Close_PreHookThrows_AbortsWithoutTruncatingAsync() {
    var log = new List<string>();
    var coord = new FakeCloseCoordinator(log);
    var hook = new RecordingHook(log, throwOnBefore: true);
    var closer = _closer(coord, hook);

    await Assert.That(async () => await closer.CloseAsync(Guid.NewGuid(), throughVersion: 10))
      .Throws<InvalidOperationException>()
      .Because("A throwing pre-hook aborts the close — durable Sourced detail must NEVER be truncated when the preserve-work failed (no fail-open).");
    await Assert.That(coord.CloseCalls).IsEqualTo(0);
  }

  [Test]
  public async Task Close_DiscardWithFullHistoryConsumer_IsRefusedAsync() {
    // A [FullHistory] projection consumes the stream and a discard-close would truncate detail it can never
    // rebuild from — refuse it (require an archiving close instead).
    var fullHistoryName = "FullHistoryPerspective_" + Guid.NewGuid().ToString("N");
    Whizbang.Core.Perspectives.FullHistoryPerspectiveRegistry.Register(fullHistoryName);
    var log = new List<string>();
    var coord = new FakeCloseCoordinator(log) { ConsumingNames = [fullHistoryName] };

    var result = await _closer(coord, hook: null).CloseAsync(Guid.NewGuid(), throughVersion: 10, archive: false);

    await Assert.That(result.Status).IsEqualTo("full_history_blocked");
    await Assert.That(coord.CloseCalls).IsEqualTo(0)
      .Because("A discard-close that would strand a full-history projection must be refused, not truncated.");
  }

  [Test]
  public async Task Close_ArchiveWithFullHistoryConsumer_IsAllowedAsync() {
    // The same full-history projection, but an ARCHIVING close — always safe (the detail is retrievable), so
    // the guard is skipped and the close proceeds.
    var fullHistoryName = "FullHistoryPerspective_" + Guid.NewGuid().ToString("N");
    Whizbang.Core.Perspectives.FullHistoryPerspectiveRegistry.Register(fullHistoryName);
    var log = new List<string>();
    var coord = new FakeCloseCoordinator(log) { ConsumingNames = [fullHistoryName] };

    var result = await _closer(coord, hook: null).CloseAsync(Guid.NewGuid(), throughVersion: 10, archive: true);

    await Assert.That(result.Status).IsEqualTo("closed");
    await Assert.That(coord.CloseCalls).IsEqualTo(1)
      .Because("An archiving close preserves the detail, so a full-history projection can still rehydrate — allowed.");
  }

  [Test]
  public async Task Close_DiscardWithResumableConsumerOnly_ProceedsAsync() {
    // The only consumer is a resumable (unmarked) projection — a discard-close is safe (it resumes from the
    // closing event forward).
    var log = new List<string>();
    var coord = new FakeCloseCoordinator(log) { ConsumingNames = ["BalancePerspective_" + Guid.NewGuid().ToString("N")] };

    var result = await _closer(coord, hook: null).CloseAsync(Guid.NewGuid(), throughVersion: 10, archive: false);

    await Assert.That(result.Status).IsEqualTo("closed");
    await Assert.That(coord.CloseCalls).IsEqualTo(1)
      .Because("An unmarked projection resumes from the carry-forward, so discard-closing its stream is safe.");
  }

  [Test]
  public async Task Close_PostHookThrows_CloseStillSucceedsAsync() {
    var log = new List<string>();
    var coord = new FakeCloseCoordinator(log);
    var hook = new RecordingHook(log, throwOnAfter: true);

    var result = await _closer(coord, hook).CloseAsync(Guid.NewGuid(), throughVersion: 10);

    await Assert.That(result.Status).IsEqualTo("closed")
      .Because("The truncate already committed; a throwing post-hook (notify/metrics) is non-fatal.");
    await Assert.That(coord.CloseCalls).IsEqualTo(1);
  }
}
