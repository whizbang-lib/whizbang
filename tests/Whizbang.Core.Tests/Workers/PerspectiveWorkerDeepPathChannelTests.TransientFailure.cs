using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// That a database failure inside one channel batch costs that batch and nothing more. The consumer
/// loop reports it once, naming the classification and the streams it was carrying, hands the
/// batch's leases back so a sibling can take them, waits out a backoff on its own clock, and takes
/// the next batch.
/// </summary>
/// <remarks>
/// The loop used to log and rethrow. The rethrow left <c>ExecuteAsync</c>, and the host's default
/// <c>BackgroundServiceExceptionBehavior</c> (<c>StopHost</c>) stopped the process over a deadlock
/// that the next attempt would have won — an instance out of a fleet for the length of a restart,
/// in the middle of a load.
/// </remarks>
public partial class PerspectiveWorkerDeepPathChannelTests {
  [Test]
  public async Task Worker_TransientDatabaseFailureInAChannelBatch_IsReportedOnceAndTheLoopTakesTheNextBatchAsync() {
    // Arrange — the cursor read of the first batch deadlocks; every later read succeeds.
    var deadlockedStreamId = Guid.CreateVersion7();
    var nextStreamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.DeadlockedPerspective";

    var coordinator = new RecordingWorkCoordinator {
      NextCursorException = FakeDbException.WithSqlState("40P01", message: "deadlock detected")
    };
    var instanceProvider = new FakeInstanceProvider();
    var runner = new RecordingRunner();
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    var eventStore = new SequencedEventStore();
    eventStore.EnqueueResponse([_envelope(Guid.CreateVersion7(), new DeepChannelEvent("after-the-deadlock"))]);
    var logger = new EventIdSignalingLogger<PerspectiveWorker>();
    var time = new FakeTimeProvider();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 50,
        // One consumer loop so the batch that fails and the batch that follows are the same loop's.
        MaxConcurrentDrainConsumers = 1
      }),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      eventTypeProvider: new ListEventTypeProvider([typeof(DeepChannelEvent)]),
      logger: logger,
      timeProvider: time,
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Act — the deadlocked batch.
    await harness.EnqueueWorkAsync(new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = deadlockedStreamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = null,
      PartitionNumber = 1
    }, cts.Token);
    await logger.WhenLoggedAsync(PerspectiveWorker.TRANSIENT_BATCH_FAILURE_EVENT_ID, TimeSpan.FromSeconds(10));

    // Assert — the loop is alive, waiting out its backoff on the injected clock.
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("a failure inside one batch must never end the loop, because the loop ending stops "
             + "the host");

    var reported = logger.LinesWith(PerspectiveWorker.TRANSIENT_BATCH_FAILURE_EVENT_ID);
    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Level).IsEqualTo(LogLevel.Error);
    await Assert.That(reported[0].Message).Contains(TransientDatabaseFailure.DEADLOCK, StringComparison.Ordinal);
    await Assert.That(reported[0].Message).Contains("40P01", StringComparison.Ordinal);
    await Assert.That(reported[0].Message).Contains(deadlockedStreamId.ToString(), StringComparison.Ordinal)
      .Because("an operator reads which streams the lost batch was carrying from the one line");
    await Assert.That(reported[0].Exception).IsTypeOf<FakeDbException>();
    await Assert.That(logger.LinesWith(PerspectiveWorker.UNEXPECTED_BATCH_FAILURE_EVENT_ID)).IsEmpty()
      .Because("a deadlock is the database's business, not a defect");
    await Assert.That(coordinator.PerspectiveLeaseReleases.Single()).IsEquivalentTo([deadlockedStreamId])
      .Because("the batch's rows must go back so a sibling can take them rather than wait out the "
             + "lease");

    // Act — the next batch, once the backoff has elapsed on the worker's clock.
    await harness.EnqueueWorkAsync(new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = nextStreamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = null,
      PartitionNumber = 1
    }, cts.Token);
    await FakeClockPump.StepUntilAsync(time, coordinator.WaitForCompletionsAsync(1, TimeSpan.FromSeconds(10)));

    // Assert — processed as if nothing had happened, and still exactly one report.
    await Assert.That(runner.RunCallCount).IsEqualTo(1);
    await Assert.That(logger.LinesWith(PerspectiveWorker.TRANSIENT_BATCH_FAILURE_EVENT_ID)).Count().IsEqualTo(1);
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse();

    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
  }
}
