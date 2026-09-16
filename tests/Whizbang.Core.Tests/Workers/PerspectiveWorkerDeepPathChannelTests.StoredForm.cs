using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Perspectives;
using Whizbang.Core.Workers;

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
    // The standard path rethrows after reporting, and the rethrow surfaces as an unobserved task
    // exception on the consumer loop; observe exactly that one.
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
        tracingOptions: null,
        completionStrategy: new InstantCompletionStrategy(),
        eventTypeProvider: new ListEventTypeProvider([typeof(DeepChannelEvent)]),
        logger: logger,
        perspectiveChannelWriter: harness.ChannelWriter,
        perspectiveCompletionChannel: harness.CompletionCapture,
        failureChannel: harness.FailureCapture,
        perspectiveDrainChannel: harness.DrainChannel,
        storedFormFailures: failures);

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
      cts.Cancel();
      try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { } catch (JsonException) { }

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
