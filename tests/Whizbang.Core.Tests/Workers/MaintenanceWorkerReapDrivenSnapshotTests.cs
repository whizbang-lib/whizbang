// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the reap-driven ephemeral snapshot wiring (E1 (2-reap)): each maintenance tick, BEFORE
/// perform_maintenance reaps, the worker asks the coordinator for the (stream, perspective) pairs whose
/// consumed, aged ephemeral bodies are about to be reaped uncovered, and drives a bootstrap snapshot for
/// each via the runner — so the rewind floor survives the reap. Inert without a perspective runner registry.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class MaintenanceWorkerReapDrivenSnapshotTests {
  private sealed class FakeRunner : IPerspectiveRunner {
    public List<(Guid StreamId, string Perspective, Guid LastEventId)> BootstrapCalls { get; } = [];
    public Type PerspectiveType => typeof(object);
    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) {
      BootstrapCalls.Add((streamId, perspectiveName, lastProcessedEventId));
      return Task.CompletedTask;
    }
    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  private sealed class FakeRegistry(IPerspectiveRunner runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors => new HashSet<LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class FakeCoordinator : IWorkCoordinator {
    public List<EphemeralSnapshotTarget> Pairs { get; init; } = [];
    public int QueryCallCount { get; private set; }
    public Task<IReadOnlyList<EphemeralSnapshotTarget>> GetEphemeralPairsNeedingSnapshotAsync(CancellationToken cancellationToken = default) {
      QueryCallCount++;
      return Task.FromResult<IReadOnlyList<EphemeralSnapshotTarget>>(Pairs);
    }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);

    // Unused IWorkCoordinator surface for this test.
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>A runner that reports a chosen bootstrap outcome (#2294).</summary>
  private sealed class OutcomeRunner(SnapshotBootstrapOutcome outcome) : IPerspectiveRunner {
    public int Calls { get; private set; }
    public Type PerspectiveType => typeof(object);
    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("the worker asks for the outcome");
    public Task<SnapshotBootstrapOutcome> TryBootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) {
      Calls++;
      return Task.FromResult(outcome);
    }
    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  /// <summary>Records each log entry's event id and message.</summary>
  private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<MaintenanceWorker> {
    public List<(int EventId, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (Entries) {
        Entries.Add((eventId.Id, formatter(state, exception)));
      }
    }
  }

  // The worker's reap-driven log events: 20 is the snapshot written, 71 the stream had no row, 72 nothing written for
  // another reason.
  private const int SNAPSHOT_WRITTEN = 20;
  private const int ROW_MISSING = 71;
  private const int NOT_WRITTEN = 72;

  private static MaintenanceWorker _buildWorker(FakeCoordinator coord, FakeRegistry? registry,
      Microsoft.Extensions.Logging.ILogger<MaintenanceWorker>? logger = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    if (registry is not null) {
      services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    }
    var sp = services.BuildServiceProvider();
    return new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, StuckRowSentinelEnabled = false }),
      logger ?? NullLogger<MaintenanceWorker>.Instance);
  }

  [Test]
  public async Task RunMaintenanceOnce_ASnapshotWritten_IsLoggedAsWrittenAsync() {
    var streamId = Guid.NewGuid();
    var coord = new FakeCoordinator { Pairs = [new EphemeralSnapshotTarget(streamId, "MyPerspective", Guid.NewGuid())] };
    var runner = new OutcomeRunner(SnapshotBootstrapOutcome.Written);
    var logger = new RecordingLogger();

    await _buildWorker(coord, new FakeRegistry(runner), logger).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(runner.Calls).IsEqualTo(1).Because("the pass asks the runner for the bootstrap's outcome");
    await Assert.That(logger.Entries.Count(e => e.EventId == SNAPSHOT_WRITTEN && e.Message.Contains(streamId.ToString(), StringComparison.Ordinal)))
      .IsEqualTo(1).Because("the positive control: a written snapshot is reported as written");
  }

  [Test]
  public async Task RunMaintenanceOnce_AStreamWithNoRow_IsNotLoggedAsASnapshot_ButAsAMissingRowAsync() {
    var streamId = Guid.NewGuid();
    var coord = new FakeCoordinator { Pairs = [new EphemeralSnapshotTarget(streamId, "MyPerspective", Guid.NewGuid())] };
    var logger = new RecordingLogger();

    await _buildWorker(coord, new FakeRegistry(new OutcomeRunner(SnapshotBootstrapOutcome.RowMissing)), logger)
      .RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Entries.Any(e => e.EventId == SNAPSHOT_WRITTEN)).IsFalse()
      .Because("#2294: success was logged for a snapshot that was never taken");
    await Assert.That(logger.Entries.Any(e => e.EventId == ROW_MISSING && e.Message.Contains(streamId.ToString(), StringComparison.Ordinal)))
      .IsTrue().Because("the missing row is reported, naming the stream");
  }

  [Test]
  [Arguments(SnapshotBootstrapOutcome.Deferred)]
  [Arguments(SnapshotBootstrapOutcome.SnapshotsOff)]
  [Arguments(SnapshotBootstrapOutcome.NotReported)]
  public async Task RunMaintenanceOnce_ABootstrapThatWroteNothing_IsNotLoggedAsASnapshotAsync(SnapshotBootstrapOutcome outcome) {
    var coord = new FakeCoordinator { Pairs = [new EphemeralSnapshotTarget(Guid.NewGuid(), "MyPerspective", Guid.NewGuid())] };
    var logger = new RecordingLogger();

    await _buildWorker(coord, new FakeRegistry(new OutcomeRunner(outcome)), logger).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Entries.Any(e => e.EventId == SNAPSHOT_WRITTEN)).IsFalse();
    await Assert.That(logger.Entries.Any(e => e.EventId == NOT_WRITTEN && e.Message.Contains(outcome.ToString(), StringComparison.Ordinal)))
      .IsTrue().Because("a bootstrap that wrote nothing says why");
  }

  [Test]
  public async Task RunMaintenanceOnce_ReapDrivenSnapshot_BootstrapsEachTargetPairAsync() {
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var coord = new FakeCoordinator { Pairs = [new EphemeralSnapshotTarget(streamId, "MyPerspective", eventId)] };
    var runner = new FakeRunner();

    await _buildWorker(coord, new FakeRegistry(runner)).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(runner.BootstrapCalls).Contains((streamId, "MyPerspective", eventId))
      .Because("The reap-driven step bootstraps a snapshot for each (stream, perspective) whose bodies are about to be reaped.");
  }

  [Test]
  public async Task RunMaintenanceOnce_NoRunnerRegistry_IsInertAsync() {
    // A non-perspective host has no runner registry — the reap-driven step must short-circuit BEFORE the
    // query (and not throw), so nothing runs against a missing registry.
    var coord = new FakeCoordinator { Pairs = [new EphemeralSnapshotTarget(Guid.NewGuid(), "P", Guid.NewGuid())] };
    await _buildWorker(coord, registry: null).RunMaintenanceOnceAsync(CancellationToken.None);
    await Assert.That(coord.QueryCallCount).IsEqualTo(0)
      .Because("Without a runner registry the reap-driven step short-circuits before querying — inert, not fatal.");
  }
}
