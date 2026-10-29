// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1788: the running dispatch worker keeps the leases of the rows it holds, at once and on every tick, and its
/// keeper ends with the worker. Driven by a fake clock; each wait is on the renewal it asserts.
/// </summary>
/// <docs>fundamentals/work-coordinator/inbox-dispatch#held-rows</docs>
[Category("Workers")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public sealed class InboxDispatchWorkerLeaseKeeperTests {
  private static readonly TimeSpan _halfLease = TimeSpan.FromSeconds(150);

  private sealed class RenewSignalingCoordinator : IWorkCoordinator, IDisposable {
    private readonly SemaphoreSlim _renewed = new(0);
    private int _renewals;
    public int Renewals => Volatile.Read(ref _renewals);

    public async Task WaitForRenewalAsync() {
      if (!await _renewed.WaitAsync(TimeSpan.FromSeconds(30))) {
        throw new TimeoutException("the keeper did not renew");
      }
    }

    public void Dispose() => _renewed.Dispose();

    /// <summary>The ids each renewal asked for, in order.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<Guid[]> Renewed { get; } = new();

    /// <summary>Rows another instance has re-claimed: a renewal reports them instead of renewing them (#1852).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> HeldElsewhere { get; } = new();

    public Task<IReadOnlyList<Guid>> RenewHeldLeasesAsync(WorkCategory category, Guid instanceId, IReadOnlyList<Guid> ids,
        int leaseSeconds = 300, CancellationToken cancellationToken = default) {
      Renewed.Enqueue([.. ids]);
      Interlocked.Increment(ref _renewals);
      _renewed.Release();
      return Task.FromResult<IReadOnlyList<Guid>>([.. ids.Where(HeldElsewhere.ContainsKey)]);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [], SyncInquiryResults = null });
    public Task<IReadOnlyList<Whizbang.Core.Perspectives.Sync.SyncInquiryResult>> ResolveSyncInquiriesAsync(
      IReadOnlyList<Whizbang.Core.Perspectives.Sync.SyncInquiry> inquiries, CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<Whizbang.Core.Perspectives.Sync.SyncInquiryResult>>([]);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class NullCommitChannel : IInboxHandlerCommitChannel {
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class NullFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "consumer-svc";
    public string HostName => "consumer-host";
    public int ProcessId => 12;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private static InboxDispatchWorker _worker(InboxChannelWriter writer, IWorkCoordinator coordinator, FakeTimeProvider time, bool renewalEnabled) {
    var sp = new ServiceCollection().AddSingleton(coordinator).BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new InboxDispatchWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new FakeInstanceProvider(),
      inboxChannelWriter: writer,
      handlerCommitChannel: new NullCommitChannel(),
      failureChannel: new NullFailureChannel(),
      schemaReadyGate: gate,
      options: Options.Create(new InboxDispatchWorkerOptions { MaxInboxAttempts = 10, MaxConcurrentDispatch = 1 }),
      coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
      logger: NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { Enabled = renewalEnabled }),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance,
        new System.Diagnostics.Metrics.Meter("test"), Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      timeProvider: time);
  }

  /// <summary>A control-plane row past its attempts: its drop hands a commit on, so the row stays held whatever the
  /// consumer has done with it by the time the keeper looks.</summary>
  private static InboxWork _heldRow() {
    var id = (Guid)TrackedGuid.New();
    return new InboxWork {
      MessageId = id,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(id),
        Payload = JsonElement.Parse("{}"),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      },
      MessageType = TypeNameFormatter.Format(typeof(IntegrityCheckpoint)),
      StreamId = (Guid)TrackedGuid.New(),
      Attempts = 11,
    };
  }

  [Test]
  public async Task Keeper_RenewsAtStartAndOnEachTick_AndEndsWithTheWorkerAsync() {
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var writer = new InboxChannelWriter(time);
    using var coordinator = new RenewSignalingCoordinator();
    await writer.WriteAsync(_heldRow());
    time.Advance(_halfLease);
    var worker = _worker(writer, coordinator, time, renewalEnabled: true);

    await worker.StartAsync(CancellationToken.None);
    await coordinator.WaitForRenewalAsync();
    // The keeper's timer exists before its first pass, so this tick is not lost.
    time.Advance(_halfLease);
    await coordinator.WaitForRenewalAsync();

    // Completing the channel ends dispatch without a stop signal; the keeper ends because its timer is disposed.
    writer.Complete();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.Renewals).IsEqualTo(2);
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse();
  }

  [Test]
  public async Task Keeper_ARowItsRenewalDidNotCover_IsNoLongerHeld_OrRenewedAgainAsync() {
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var writer = new InboxChannelWriter(time);
    using var coordinator = new RenewSignalingCoordinator();
    var lost = _heldRow();
    var kept = _heldRow();
    coordinator.HeldElsewhere[lost.MessageId] = true;
    await writer.WriteAsync(lost);
    await writer.WriteAsync(kept);
    time.Advance(_halfLease);
    var worker = _worker(writer, coordinator, time, renewalEnabled: true);

    await worker.StartAsync(CancellationToken.None);
    await coordinator.WaitForRenewalAsync();
    // The keeper's passes run one after another, so the second renewal starts after the first pass acted on its result.
    time.Advance(_halfLease);
    await coordinator.WaitForRenewalAsync();
    await worker.StopAsync(CancellationToken.None);

    var renewals = coordinator.Renewed.ToArray();
    await Assert.That(renewals[0]).IsEquivalentTo([lost.MessageId, kept.MessageId]);
    await Assert.That(renewals[1]).IsEquivalentTo([kept.MessageId])
      .Because("another instance re-claimed the row, so this instance stopped holding it and does not renew it again (#1852)");
    await Assert.That(writer.IsInFlight(lost.MessageId)).IsFalse();
    await Assert.That(writer.IsInFlight(kept.MessageId)).IsTrue();
  }

  [Test]
  public async Task Keeper_StopsOnShutdown_WithoutFaultingTheWorkerAsync() {
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var writer = new InboxChannelWriter(time);
    using var coordinator = new RenewSignalingCoordinator();
    await writer.WriteAsync(_heldRow());
    time.Advance(_halfLease);
    var worker = _worker(writer, coordinator, time, renewalEnabled: true);

    await worker.StartAsync(CancellationToken.None);
    await coordinator.WaitForRenewalAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("stopping is the normal way out of the keeper's wait, not a failure");
  }

  [Test]
  public async Task Keeper_WithRenewalDisabled_RenewsNothingAsync() {
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var writer = new InboxChannelWriter(time);
    using var coordinator = new RenewSignalingCoordinator();
    await writer.WriteAsync(_heldRow());
    time.Advance(_halfLease);
    var worker = _worker(writer, coordinator, time, renewalEnabled: false);

    await worker.StartAsync(CancellationToken.None);
    writer.Complete();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(coordinator.Renewals).IsEqualTo(0)
      .Because("the lease renewal killswitch covers inbox rows as it does every other category");
  }
}
