using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
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
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Increment 6, option A: the read-model barrier releases when Migrate completes AND the
/// perspective startup scan has run — later than Migrate (a lens must not read perspectives a
/// migration may have left mid-repair), earlier than Ready (reads never needed the transports).
/// A host with no perspectives has no read models to repair: the schema gate alone releases it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ReadModelsReadyDriver.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/ReadModelsReadyGate.cs</code-under-test>
[Category("Startup")]
[NotInParallel(Order = 105)]
public class ReadModelsReadyDriverTests {

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private static async Task _eventuallyAsync(Func<bool> condition, string because) {
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (!condition() && DateTime.UtcNow < deadline) {
      await Task.Delay(10);
    }
    await Assert.That(condition()).IsTrue().Because(because);
  }

  [Test]
  public async Task WithoutAPerspectiveWorker_TheSchemaGateAloneReleasesTheBarrierAsync() {
    var schemaGate = new SchemaReadyGate();
    var readGate = new ReadModelsReadyGate();
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var driver = new ReadModelsReadyDriver(readModelsGate: readGate, schemaReadyGate: schemaGate, services: sp, logger: NullLogger<ReadModelsReadyDriver>.Instance);

    using var cts = new CancellationTokenSource();
    await driver.StartAsync(cts.Token);
    await Task.Delay(150);
    await Assert.That(readGate.IsReady).IsFalse()
      .Because("the schema is still migrating — reads must keep refusing");

    schemaGate.MarkReady();
    await _eventuallyAsync(() => readGate.IsReady,
      "no perspectives means no read models to repair — the schema gate alone releases");

    await cts.CancelAsync();
    await driver.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task WithAPerspectiveWorker_TheBarrierWaitsForItsStartupScanAsync() {
    var schemaGate = new SchemaReadyGate();
    var readGate = new ReadModelsReadyGate();

    var inner = new ServiceCollection().BuildServiceProvider();
    var worker = new PerspectiveWorker(
      instanceProvider: new StubInstanceProvider(),
      scopeFactory: inner.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions()),
      schemaReadyGate: schemaGate,
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: NullEventTypeProvider.Instance,
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: NullLogger<PerspectiveWorker>.Instance,
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(new PerspectiveRewindOptions()),
      perspectiveChannelWriter: new PerspectiveChannelWriter(),
      perspectiveCompletionChannel: new CapturingPerspectiveCompletionChannel(),
      failureChannel: new CapturingFailureChannel(),
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: new PerspectiveDrainChannel(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor((Options.Create(new PerspectiveWorkerOptions())).Value));
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton(worker);
    await using var sp = services.BuildServiceProvider();
    var driver = new ReadModelsReadyDriver(readModelsGate: readGate, schemaReadyGate: schemaGate, services: sp, logger: NullLogger<ReadModelsReadyDriver>.Instance);

    using var cts = new CancellationTokenSource();
    await driver.StartAsync(cts.Token);

    schemaGate.MarkReady();
    await Task.Delay(150);
    await Assert.That(readGate.IsReady).IsFalse()
      .Because("Migrate completed but the perspective startup scan has not run — the read "
             + "models may be mid-repair, and a lens must not read them yet");

    // The worker runs: gate already open, so its startup scan executes and completes.
    await worker.StartAsync(cts.Token);
    await _eventuallyAsync(() => readGate.IsReady,
      "the scan completing is exactly what makes the read models trustworthy");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
    await driver.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ReadModelsGuard_RefusesWhileClosed_AndOnlyWhileClosedAsync() {
    var readGate = new ReadModelsReadyGate();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IReadModelsReadyGate>(readGate);
    await using var sp = services.BuildServiceProvider();

    await Assert.ThrowsAsync<WhizbangNotReadyException>(async () => {
      ReadModelsGuard.ThrowIfNotReady(sp);
      await Task.CompletedTask;
    });

    readGate.MarkReady();
    ReadModelsGuard.ThrowIfNotReady(sp);   // no throw — reads resume

    await using var ungated = new ServiceCollection().BuildServiceProvider();
    ReadModelsGuard.ThrowIfNotReady(ungated);   // no barrier registered — ungated, as fixtures expect
  }
}
