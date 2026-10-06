// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Branch coverage for <see cref="StreamCloser.CloseAsync"/>'s post-truncate hook gate: the
/// PostDestruction hook fires only when the coordinator actually closed the stream. A close the
/// coordinator declined (gate-blocked, nothing to carry forward, debug-skipped) destroyed nothing,
/// so announcing a destruction would trigger notifications and cascades for data that still exists.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Lifecycle/StreamCloser.cs</code-under-test>
public class StreamCloserBranchCoverageTests {

  private sealed class RecordingHook(List<string> log) : IDestructionHook {
    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      log.Add("before");
      return ValueTask.FromResult(DestructionResult.Proceed());
    }

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      log.Add("after");
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FixedResultCoordinator(List<string> log, StreamCloseResult result) : IWorkCoordinator {
    public Task<StreamCloseResult> CloseStreamAsync(Guid streamId, long throughVersion, bool archive = false, CancellationToken cancellationToken = default) {
      log.Add("close");
      return Task.FromResult(result);
    }

    public Task<IReadOnlyList<string>> GetConsumingPerspectiveNamesAsync(Guid streamId, long throughVersion, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<string>>([]);

    // Unused IWorkCoordinator surface.
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }

  [Test]
  public async Task Close_CoordinatorDeclinesTheClose_DoesNotFireThePostDestructionHookAsync() {
    var log = new List<string>();
    var coordinator = new FixedResultCoordinator(log, new StreamCloseResult("gate_blocked", 0));
    var closer = new StreamCloser(coordinator, NullLogger<StreamCloser>.Instance, new RecordingHook(log));

    var result = await closer.CloseAsync(Guid.NewGuid(), throughVersion: 10, archive: true);

    await Assert.That(result.Status).IsEqualTo("gate_blocked")
      .Because("the coordinator's outcome is returned to the caller unchanged");
    await Assert.That(string.Join(",", log)).IsEqualTo("before,close")
      .Because("nothing was truncated, so there is no destruction to announce after the fact");
  }

  /// <summary>A post-destruction hook that genuinely suspends before it finishes its work.</summary>
  private sealed class SuspendingAfterHook(List<string> log) : IDestructionHook {
    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      log.Add("before");
      return ValueTask.FromResult(DestructionResult.Proceed());
    }

    public async ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      log.Add("after-started");
      await Task.Yield();
      log.Add("after-finished");
    }
  }

  // A real post-destruction hook (a notifier, a cascade) awaits I/O, so the close must survive the
  // hook suspending: it resumes, lets the hook finish, and only then returns the coordinator's result.
  [Test]
  public async Task Close_PostDestructionHookSuspends_WaitsForTheHookThenReturnsTheClosedResultAsync() {
    var log = new List<string>();
    var coordinator = new FixedResultCoordinator(log, new StreamCloseResult("closed", 3));
    var closer = new StreamCloser(coordinator, NullLogger<StreamCloser>.Instance, new SuspendingAfterHook(log));

    var result = await closer.CloseAsync(Guid.NewGuid(), throughVersion: 10, archive: true);

    await Assert.That(result.Status).IsEqualTo("closed");
    await Assert.That(string.Join(",", log)).IsEqualTo("before,close,after-started,after-finished")
      .Because("the close awaits the post-destruction hook to completion before returning, even when the hook yields");
  }
}
