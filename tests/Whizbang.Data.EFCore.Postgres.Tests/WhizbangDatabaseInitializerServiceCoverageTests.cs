// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage-round tests for <see cref="WhizbangDatabaseInitializerService"/> paths the existing
/// behavioral suite (<see cref="WhizbangDatabaseInitializerServiceTests"/>) never drives: disposing
/// the background retry loop's <see cref="CancellationTokenSource"/>, and the log line emitted when
/// the best-effort partition recompute actually changed rows.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/WhizbangDatabaseInitializerService.cs</code-under-test>
[Category("Shard1")]
public class WhizbangDatabaseInitializerServiceCoverageTests {

  // A database initializer decides whether a host may serve. If Dispose silently no-ops instead
  // of tearing down the stop-loop's CancellationTokenSource, a supervised restart of this service
  // in place (config reload, pod recycle without a fresh process) accumulates live token sources
  // across cycles, and a caller can no longer tell "disposed" from "never was" -- exactly the
  // ambiguity Dispose exists to remove.
  [Test]
  public async Task Dispose_DisposesTheStopLoopCancellationTokenSourceAsync() {
    var service = _create();

    service.Dispose();

    // StopAsync cancels that SAME CancellationTokenSource; canceling a disposed one throws
    // ObjectDisposedException. That is the only externally observable proof Dispose tore down
    // the real object instead of doing nothing.
    await Assert.That(() => service.StopAsync(CancellationToken.None))
      .Throws<ObjectDisposedException>()
      .Because("Dispose must actually dispose the stop-loop's CancellationTokenSource, not no-op");
  }

  /// <summary>
  /// A background initialization whose attempt fails while the host is being disposed ends quietly, rather than
  /// faulting on the disposed stop-loop token and surfacing as an unobserved task exception.
  /// </summary>
  /// <remarks>
  /// The retry loop read <c>_stopCts.Token</c> after each failed attempt. A host stopped and disposed while an attempt
  /// was in flight (its database gone, its provider disposed) made that read throw <see cref="ObjectDisposedException"/>,
  /// which no one awaited: the process-wide unobserved-task hook then reported it into whichever host had registered
  /// the hook, failing an unrelated test that asserts its host logged no errors.
  /// </remarks>
  [Test]
  public async Task BackgroundInit_AnAttemptFailingAfterDispose_EndsTheLoopWithoutFaultingAsync() {
    WhizbangDatabaseInitializerService? service = null;
    var runner = new CallbackRunner(() => {
      // The host is disposed while this attempt is in flight; the attempt then fails, as it would on a dropped database.
      service!.Dispose();
      throw new InvalidOperationException("the database went away");
    });
    service = _create(runner: runner, nonBlocking: true);

    await service.StartAsync(CancellationToken.None);
    var loop = service.BackgroundInitTask!;
    await Assert.That(async () => await loop).ThrowsNothing()
      .Because("disposing the service ends its retry loop; a fault here is never observed and escapes on the finalizer");
    await Assert.That(runner.Calls).IsEqualTo(1)
      .Because("a disposed service makes no further attempt");
  }

  /// <summary>Disposing twice is harmless: the second call does not cancel a source the first disposed.</summary>
  [Test]
  public async Task Dispose_Twice_DoesNotThrowAsync() {
    var service = _create();
    service.Dispose();

    await Assert.That(service.Dispose).ThrowsNothing()
      .Because("a host may dispose a service more than once, and Dispose cancels before it disposes");
  }

  // Partition recompute is best-effort self-healing: when a redeploy crossed a PartitionCount
  // boundary and rows were ACTUALLY fixed, an operator needs that visible in the logs. A recompute
  // that silently fixes real drift is indistinguishable from one that found nothing to do, and a
  // partition map that is genuinely still stuck would look identical to a healthy one without this
  // line.
  [Test]
  public async Task TryRecomputePartitionsAsync_RowsRecomputed_LogsThePartitionRecomputeAsync() {
    var logger = new CapturingLogger();
    var coordinator = new SucceedingCoordinator(new PartitionRecomputeResult {
      InboxRowsRecomputed = 3,
      OutboxRowsRecomputed = 0,
      ActiveStreamsRowsRecomputed = 5,
    });
    var service = _create(coordinator: coordinator, logger: logger, partitionCount: 4);

    await service.TryRecomputePartitionsAsync(CancellationToken.None);

    await Assert.That(logger.Entries.Count).IsEqualTo(1)
      .Because("a recompute that changed rows must log exactly once -- silence here would hide a real fix from an operator");
    var message = logger.Entries[0];
    await Assert.That(message).Contains("PartitionCount=4");
    await Assert.That(message).Contains("inbox=3");
    await Assert.That(message).Contains("outbox=0");
    await Assert.That(message).Contains("activeStreams=5");
  }

  // ---------- helpers ----------

  private static WhizbangDatabaseInitializerService _create(
      IWorkCoordinator? coordinator = null,
      ILogger<WhizbangDatabaseInitializerService>? logger = null,
      int partitionCount = 10_000,
      ISchemaInitializationRunner? runner = null,
      bool nonBlocking = false) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    if (coordinator is not null) {
      services.AddSingleton(coordinator);
    }
    var provider = services.BuildServiceProvider();
    return new WhizbangDatabaseInitializerService(
      provider,
      [runner ?? new NoOpRunner()],
      new SchemaReadyGate(),
      Options.Create(new ClaimWorkerOptions { PartitionCount = partitionCount }),
      Options.Create(new SchemaInitializationOptions { NonBlockingSchemaInit = nonBlocking, InitRetryDelay = TimeSpan.FromHours(1) }),
      TimeProvider.System,
      logger ?? NullLogger<WhizbangDatabaseInitializerService>.Instance,
      []);
  }

  /// <summary>Runner that completes immediately; StartAsync/StopAsync are not under test here.</summary>
  private sealed class NoOpRunner : ISchemaInitializationRunner {
    public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  }

  /// <summary>Runner that runs a callback per attempt and counts the attempts.</summary>
  private sealed class CallbackRunner(Action onAttempt) : ISchemaInitializationRunner {
    public int Calls { get; private set; }

    public Task RunAsync(CancellationToken cancellationToken) {
      Calls++;
      onAttempt();
      return Task.CompletedTask;
    }
  }

  /// <summary>Captures the fully-formatted messages emitted through the source-generated LoggerMessage methods.</summary>
  private sealed class CapturingLogger : ILogger<WhizbangDatabaseInitializerService> {
    public List<string> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      Entries.Add(formatter(state, exception));
    }
  }

  /// <summary>Coordinator whose partition recompute returns a supplied result; the rest is unused here.</summary>
  private sealed class SucceedingCoordinator(PartitionRecomputeResult result) : IWorkCoordinator {
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(
        int partitionCount, CancellationToken cancellationToken = default)
      => Task.FromResult(result);

    public Task ReportPerspectiveCompletionAsync(
        PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(
        PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(
        InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
