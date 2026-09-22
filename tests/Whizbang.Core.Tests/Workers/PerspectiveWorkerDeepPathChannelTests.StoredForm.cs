using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Execution;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Perspectives;
using Whizbang.Core.Tracing;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// That the standard (per-event) path classifies an unreadable stored document the way the drain
/// path does: announced once, its leased row parked through the failure channel.
/// </summary>
/// <remarks>
/// The two paths share the classification helper but not the catch, and a stored form that cannot
/// be read is the same failure whichever path read it. See the drain-path tests for the full
/// account.
/// </remarks>
public partial class PerspectiveWorkerDeepPathChannelTests {
  private const int STORED_FORM_UNREADABLE_EVENT_ID = 65;

  private static JsonException _storedFormRefusal() {
    try {
      JsonSerializer.Deserialize("{\"At\":true}", HolderContext.Default.Holder);
    } catch (JsonException ex) {
      return ex;
    }
    throw new InvalidOperationException("the fixture did not fail");
  }

  [Test]
  public async Task Worker_RunnerRefusesTheStoredForm_AnnouncesOnceAndParksTheRowAsync() {
    // The standard path rethrows after reporting. The consumer loop now contains that rethrow (it
    // reports the batch as a defect and carries on), so nothing should reach here any more; the
    // handler stays as a belt so a regression of the containment cannot fail an unrelated test.
    static void handler(object? s, UnobservedTaskExceptionEventArgs e) {
      if (e.Exception.InnerException is JsonException) {
        e.SetObserved();
      }
    }
    TaskScheduler.UnobservedTaskException += handler;
    try {
      var streamId = Guid.CreateVersion7();
      var eventId = Guid.CreateVersion7();
      var workId = Guid.CreateVersion7();
      const string perspectiveName = "Deep.UnreadablePerspective";

      var coordinator = new RecordingWorkCoordinator();
      var instanceProvider = new FakeInstanceProvider();
      var runner = new RecordingRunner { RunException = _storedFormRefusal() };
      var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
      var eventStore = new SequencedEventStore();
      eventStore.EnqueueResponse([_envelope(eventId, new DeepChannelEvent("unreadable"))]);
      var logger = new FakeLogger<PerspectiveWorker>();
      var failures = new StoredFormFailureRegistry();

      var services = new ServiceCollection();
      services.TryAddWhizbangDefaults();
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
        options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
        schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
        tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
        completionStrategy: new InstantCompletionStrategy(logger: NullLogger<InstantCompletionStrategy>.Instance),
        eventTypeProvider: new ListEventTypeProvider([typeof(DeepChannelEvent)]),
        logger: logger,
        perspectiveChannelWriter: harness.ChannelWriter,
        perspectiveCompletionChannel: harness.CompletionCapture,
        failureChannel: harness.FailureCapture,
        perspectiveDrainChannel: harness.DrainChannel,
        storedFormFailures: failures,
        syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
        syncEventTracker: new SyncEventTracker(),
        snapshotStore: NullPerspectiveSnapshotStore.Instance,
        streamLocker: NullPerspectiveStreamLocker.Instance,
        streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
        streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
        processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
        workChannelWriter: new WorkChannelWriter(),
        rewindOptions: Options.Create(new PerspectiveRewindOptions()),
        leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
        leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
        leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
        deadLetterStore: NullDeadLetterStore.Instance,
        generationProvider: new DefaultGenerationProvider(),
        perspectiveNotificationListener: new NoOpWorkNotificationListener(),
        governor: PerspectiveWorker.CreateDefaultGovernor((Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 })).Value));

      using var cts = new CancellationTokenSource();
      await worker.StartAsync(cts.Token);
      await harness.EnqueueWorkAsync(new PerspectiveWork {
        WorkId = workId,
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastProcessedEventId = null,
        PartitionNumber = 1
      }, cts.Token);
      await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
      await harness.FailureCapture.WaitForCountAsync(1, TimeSpan.FromSeconds(10));
      await cts.CancelAsync();
      try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ } catch (JsonException) { /* stopping is teardown; its outcome is not what this test asserts */ }

      var announced = logger.Collector.GetSnapshot().Where(r => r.Id.Id == STORED_FORM_UNREADABLE_EVENT_ID).ToList();
      await Assert.That(announced).Count().IsEqualTo(1);
      await Assert.That(announced[0].Level).IsEqualTo(LogLevel.Error);
      await Assert.That(announced[0].Message).Contains("$.At", StringComparison.Ordinal);

      var (category, parked) = harness.FailureCapture.Items.Single();
      await Assert.That(category).IsEqualTo(WorkCategory.PerspectiveEvent);
      await Assert.That(parked.MessageId).IsEqualTo(workId)
        .Because("the standard path leases per event, and the leased row is what the database parks");
      await Assert.That(parked.Reason).IsEqualTo(MessageFailureReason.SerializationError);

      coordinator.Failures.TryPeek(out var failure);
      await Assert.That(failure?.Error).Contains("Stored form unreadable at $.At", StringComparison.Ordinal);
      await Assert.That(failures.Count).IsEqualTo(1);
    } finally {
      TaskScheduler.UnobservedTaskException -= handler;
    }
  }
}
