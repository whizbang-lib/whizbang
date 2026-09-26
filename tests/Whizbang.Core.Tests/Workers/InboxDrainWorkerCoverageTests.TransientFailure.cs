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
  [Test]
  public async Task DrainBatch_TransientDatabaseFailure_IsNamedAsSuchAndTheNextBatchDrainsAsync() {
    var deadlockedStream = (Guid)TrackedGuid.New();
    var nextStream = (Guid)TrackedGuid.New();
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
      logger);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    _ = drain.TryWrite(deadlockedStream);
    await logger.WhenLoggedAsync(
      InboxDrainWorker.TRANSIENT_BATCH_DRAIN_FAILURE_EVENT_ID, TimeSpan.FromSeconds(10));
    _ = drain.TryWrite(nextStream);
    await inbox.ReachedCount.Task.WaitAsync(TimeSpan.FromSeconds(10));

    var reported = logger.LinesWith(InboxDrainWorker.TRANSIENT_BATCH_DRAIN_FAILURE_EVENT_ID);
    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Level).IsEqualTo(LogLevel.Error);
    await Assert.That(reported[0].Message).Contains(TransientDatabaseFailure.DEADLOCK, StringComparison.Ordinal);
    await Assert.That(reported[0].Message).Contains("40P01", StringComparison.Ordinal);
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("the batch the database lost must not take the worker, and the host, with it");
    await Assert.That(inbox.Written).Count().IsEqualTo(1);
    await Assert.That(inbox.Written[0].StreamId).IsEqualTo(nextStream)
      .Because("the batch after the failure drains as if nothing had happened");

    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }
  }
}
