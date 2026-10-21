// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Component.Tests.Workers;

/// <summary>
/// The claim records how long it took, as <c>whizbang.perspective.claim.duration</c> (#1535): the instrument
/// was declared and documented and nothing recorded it once <see cref="ClaimWorker"/> took over claiming.
/// The clock is fake and the coordinator advances it inside the claim, so the duration is exact; the test
/// waits on the duration's own recording.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ClaimWorker.cs</code-under-test>
[Category("Component")]
public class ClaimWorkerMetricsTests {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan _claimTakes = TimeSpan.FromMilliseconds(40);

  private sealed class Instance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  /// <summary>Every claim takes 40 ms on the fake clock and finds nothing.</summary>
  private sealed class Coordinator(FakeTimeProvider clock) : IWorkCoordinator {
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      clock.Advance(_claimTakes);
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>Hands each meter out once and keeps it, so the listener hears only this test's instruments.</summary>
  private sealed class MeterFactory : IMeterFactory {
    private readonly List<Meter> _meters = [];
    public IReadOnlyList<Meter> CreatedMeters => _meters;

    public Meter Create(MeterOptions options) {
      var meter = new Meter(options);
      _meters.Add(meter);
      return meter;
    }

    public void Dispose() {
      foreach (var meter in _meters) {
        meter.Dispose();
      }
    }
  }

  [Test]
  public async Task AClaim_RecordsItsDurationAsync() {
    using var factory = new MeterFactory();
    var perspectiveMetrics = new PerspectiveMetrics(new WhizbangMetrics(factory));
    var meters = factory.CreatedMeters.ToHashSet();

    // The first claim's duration; its recording is the signal.
    var first = new ConcurrentDictionary<string, double>();
    var claimRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (meters.Contains(instrument.Meter)) {
          l.EnableMeasurementEvents(instrument);
        }
      }
    };
    listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => {
      first.TryAdd(instrument.Name, value);
      if (instrument.Name == "whizbang.perspective.claim.duration") {
        claimRecorded.TrySetResult();
      }
    });
    listener.Start();

    var clock = new FakeTimeProvider();
    var worker = _worker(new Coordinator(clock), clock, perspectiveMetrics);
    await worker.StartAsync(CancellationToken.None);
    try {
      await claimRecorded.Task.WaitAsync(_deadline);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(first["whizbang.perspective.claim.duration"]).IsEqualTo(_claimTakes.TotalMilliseconds)
      .Because("the claim's duration is recorded where the claim now happens");
  }

  private static ClaimWorker _worker(
      IWorkCoordinator coordinator, TimeProvider clock, PerspectiveMetrics perspectiveMetrics) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new ClaimWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new Instance(),
      notificationListener: new NoOpWorkNotificationListener(),
      schemaReadyGate: gate,
      options: Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 10, PollingMaxIntervalMilliseconds = 50 }),
      logger: NullLogger<ClaimWorker>.Instance,
      outboxChannel: new WorkChannelWriter(),
      inboxChannel: new InboxChannelWriter(),
      perspectiveChannel: new PerspectiveChannelWriter(),
      perspectiveDrainChannel: new PerspectiveDrainChannel(),
      outboxDrainChannel: new OutboxDrainChannel(),
      inboxDrainChannel: new InboxDrainChannel(),
      signalingGate: NullNotifySignalingGate.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      signalBus: NullSignalBus.Instance,
      timeProvider: clock,
      perspectiveMetrics: perspectiveMetrics);
  }
}
