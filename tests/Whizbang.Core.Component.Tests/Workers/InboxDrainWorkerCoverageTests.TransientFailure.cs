// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// That the batch guard names what it caught. The loop already survived every failure; what its one
/// line could not say was whether an operator was looking at a deadlock that had already passed or
/// at a defect worth a page.
/// </summary>
public partial class InboxDrainWorkerCoverageTests {
  /// <summary>
  /// Drains a batch whose fetch deadlocks, then a batch that drains, and returns what the worker logged. The waits are
  /// the worker's own report, the inbox write of the second batch, and, when the run escalated, the recovery line the
  /// second batch ends it with.
  /// </summary>
  private static async Task<EventIdSignalingLogger<InboxDrainWorker>> _runBatchAfterADeadlockAsync(
      DatabaseFailureEscalationOptions? escalation, Guid nextStream) {
    var deadlockedStream = (Guid)TrackedGuid.New();
    var nextMessage = (Guid)TrackedGuid.New();

    var coord = new ScriptedWorkCoordinator();
    coord.Enqueue(_ => throw FakeDbException.WithSqlState("40P01", message: "deadlock detected"));
    coord.Enqueue(streamIds => [.. streamIds.Select(sid => _row(nextMessage, sid))]);

    var drain = new FakeInboxDrainChannel();
    var inbox = new CapturingInboxChannel { TargetCount = 1 };
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new EventIdSignalingLogger<InboxDrainWorker>();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeServiceInstanceProvider(), drain, inbox, gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100, MaxPerStreamCeiling = 1000 }),
      _jsonOpts,
      logger,
      databaseFailureEscalation: escalation);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    _ = drain.TryWrite(deadlockedStream);
    await logger.WhenLoggedAsync(
      InboxDrainWorker.TRANSIENT_BATCH_DRAIN_FAILURE_EVENT_ID, TimeSpan.FromSeconds(10));
    _ = drain.TryWrite(nextStream);
    await inbox.ReachedCount.Task.WaitAsync(TimeSpan.FromSeconds(10));
    if (escalation is not null) {
      await logger.WhenLoggedAsync(DatabaseFailureEscalation.RECOVERED_EVENT_ID, TimeSpan.FromSeconds(10));
    }

    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("the batch the database lost must not take the worker, and the host, with it");
    await Assert.That(inbox.Written).Count().IsEqualTo(1);
    await Assert.That(inbox.Written[0].StreamId).IsEqualTo(nextStream)
      .Because("the batch after the failure drains as if nothing had happened");

    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }
    return logger;
  }

  [Test]
  public async Task DrainBatch_TransientDatabaseFailure_IsNamedAsSuchAndTheNextBatchDrainsAsync() {
    var logger = await _runBatchAfterADeadlockAsync(escalation: null, (Guid)TrackedGuid.New());

    var reported = logger.LinesWith(InboxDrainWorker.TRANSIENT_BATCH_DRAIN_FAILURE_EVENT_ID);
    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Level).IsEqualTo(LogLevel.Warning)
      .Because("#1986: a transient database failure is retried and loses nothing, so each one is a Warning");
    await Assert.That(reported[0].Message).Contains(TransientDatabaseFailure.DEADLOCK, StringComparison.Ordinal);
    await Assert.That(reported[0].Message).Contains("40P01", StringComparison.Ordinal);
    await Assert.That(logger.Lines.Where(l => l.Level >= LogLevel.Error)).IsEmpty()
      .Because("#1986: one deadlock is far below the escalation threshold, so it pages nobody");
  }

  [Test]
  public async Task DrainBatch_TransientFailurePastTheThreshold_EscalatesOnceAndSaysWhenItRecoversAsync() {
    var logger = await _runBatchAfterADeadlockAsync(
      new DatabaseFailureEscalationOptions { ConsecutiveFailures = 1, FailingFor = TimeSpan.Zero }, (Guid)TrackedGuid.New());

    var escalated = logger.LinesWith(DatabaseFailureEscalation.ESCALATED_EVENT_ID);
    await Assert.That(escalated).Count().IsEqualTo(1)
      .Because("#1986: the worker reports its transient failures to the escalation, which logs one Error past the threshold");
    await Assert.That(escalated[0].Level).IsEqualTo(LogLevel.Error);
    await Assert.That(escalated[0].Message).Contains(nameof(InboxDrainWorker), StringComparison.Ordinal);
    var recovered = logger.LinesWith(DatabaseFailureEscalation.RECOVERED_EVENT_ID);
    await Assert.That(recovered).Count().IsEqualTo(1)
      .Because("#1986: the batch that drains after the failure tells the escalation the database is answering");
    await Assert.That(recovered[0].Level).IsEqualTo(LogLevel.Information);
  }
}
