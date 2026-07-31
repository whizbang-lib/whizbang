using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Stream-integrity Phase B: the origin-side checkpoint publisher advances the watermark through
/// the coordinator (one winner per window) and publishes one <see cref="IntegrityCheckpoint"/>
/// carrying the origin's identity and the window's per-(tenant, type) counts — INCLUDING empty
/// windows, because a missing checkpoint is the consumer's liveness alarm.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityCheckpointWorker.cs</code-under-test>
public class IntegrityCheckpointWorkerTests {

  [Test]
  public async Task RunCheckpointOnce_PublishesWindowWithOriginIdentityAsync() {
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow {
        FromCommitSequence = 5,
        ToCommitSequence = 9,
        Buckets = [
          new CheckpointBucket { TenantScope = "tenant-a", EventType = "Contracts.ThingCreated", Count = 3 },
          new CheckpointBucket { TenantScope = null, EventType = "Contracts.ProbeHappened", Count = 1 },
        ]
      }
    };
    var dispatcher = new _captureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, serviceName: "origin-svc");

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    var checkpoint = (IntegrityCheckpoint)dispatcher.Published.Single();
    await Assert.That(checkpoint.OriginServiceId).IsEqualTo(coordinator.LocalServiceId);
    await Assert.That(checkpoint.CheckpointStreamId).IsEqualTo(coordinator.LocalServiceId)
      .Because("one homogeneous ephemeral checkpoint stream per origin — the stream IS the origin.");
    await Assert.That(checkpoint.OriginServiceName).IsEqualTo("origin-svc")
      .Because("the service NAME is the directed-message Target a consumer repairs through.");
    await Assert.That(checkpoint.FromCommitSequence).IsEqualTo(5L);
    await Assert.That(checkpoint.ToCommitSequence).IsEqualTo(9L);
    await Assert.That(checkpoint.Buckets.Count).IsEqualTo(2);
    await Assert.That(checkpoint.Buckets[0].Count).IsEqualTo(3);
  }

  [Test]
  public async Task RunCheckpointOnce_EmptyWindow_StillPublishesAsync() {
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 9, ToCommitSequence = 9 }
    };
    var dispatcher = new _captureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, serviceName: "origin-svc");

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    var checkpoint = (IntegrityCheckpoint)dispatcher.Published.Single();
    await Assert.That(checkpoint.Buckets).IsEmpty()
      .Because("a quiet window still checkpoints — ABSENCE is the liveness alarm, so silence " +
               "must always be abnormal.");
  }

  [Test]
  public async Task RunCheckpointOnce_NullWindow_PublishesNothingAsync() {
    var coordinator = new _checkpointCoordinator { Window = null };
    var dispatcher = new _captureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, serviceName: "origin-svc");

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("null = unsupported engine OR another instance won this window's advance — " +
               "publishing would double-checkpoint the window.");
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static IntegrityCheckpointWorker _buildWorker(
      _checkpointCoordinator coordinator, _captureDispatcher dispatcher, string serviceName) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider(serviceName));
    var sp = services.BuildServiceProvider();
    return new IntegrityCheckpointWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),
      Options.Create(new StreamIntegrityOptions()),
      NullLogger<IntegrityCheckpointWorker>.Instance);
  }

  private sealed class _checkpointCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public IntegrityCheckpointWindow? Window { get; init; }
    public Guid LocalServiceId { get; } = TrackedGuid.NewMedo().Value;

    public Task<IntegrityCheckpointWindow?> AdvanceIntegrityCheckpointAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(Window);

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(LocalServiceId);
  }

  private sealed class _captureDispatcher : FakeDispatcher, IDispatcher {
    public List<object> Published { get; } = [];

    public new Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      Published.Add(eventData!);
      return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
    }
  }

  private sealed class _instanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo().Value;
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
