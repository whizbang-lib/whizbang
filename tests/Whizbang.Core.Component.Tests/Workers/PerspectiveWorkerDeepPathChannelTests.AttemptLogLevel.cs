// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
using Whizbang.Core.Tracing;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The level a failed apply on the standard (per-event) path logs at (#1976). The failure parks the leased row
/// through the failure channel, so it is retried and dead-lettered at the bound: the attempt is a Warning carrying
/// its exception (EventId 14), and Error is kept for the dead-letter record.
/// </summary>
public partial class PerspectiveWorkerDeepPathChannelTests {
  private const int APPLY_FAILURE_EVENT_ID = 14;

  [Test]
  [Timeout(30000)]
  public async Task Worker_RunnerThrows_LogsTheAttemptAtWarningWithItsExceptionAsync(CancellationToken cancellationToken) {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var workId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.FailingPerspective";

    var coordinator = new RecordingWorkCoordinator();
    var instanceProvider = new FakeInstanceProvider();
    var runner = new RecordingRunner { RunException = new InvalidOperationException("apply failed") };
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    var eventStore = new SequencedEventStore();
    eventStore.EnqueueResponse([_envelope(eventId, new DeepChannelEvent("failing"))]);
    var logger = new FakeLogger<PerspectiveWorker>();

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
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: logger,
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(new PerspectiveRewindOptions()),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: harness.DrainChannel,
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
    await coordinator.FirstFailure.WaitAsync(cancellationToken);
    await harness.FailureCapture.WaitForCountAsync(1, cancellationToken);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    var records = logger.Collector.GetSnapshot();
    var attempts = records.Where(r => r.Id.Id == APPLY_FAILURE_EVENT_ID).ToList();
    await Assert.That(attempts.Count).IsEqualTo(1);
    await Assert.That(attempts[0].Level).IsEqualTo(LogLevel.Warning)
      .Because("the parked row is retried and dead-lettered at the bound, so the attempt is a Warning");
    await Assert.That(attempts[0].Exception).IsTypeOf<InvalidOperationException>()
      .Because("the attempt's exception is attached, so the Warning says what failed");
    await Assert.That(records.Any(r => r.Level >= LogLevel.Error)).IsFalse()
      .Because("nothing was dead-lettered, so nothing logs at Error");

    var (_, parked) = harness.FailureCapture.Items.Single();
    await Assert.That(parked.MessageId).IsEqualTo(workId);
  }
}
