using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The paced repair drain's tick, deterministically: token pacing gates the claim budget,
/// claimed rows dispatch grouped per (origin, tenant, type) with the union of their stamped
/// windows, pre-stamp rows widen to whole history, and unlearned origins are never claimed —
/// no attempt budget burns on a request that could not leave the process.
/// </summary>
/// <docs>proposals/paced-repair-drain</docs>
/// <remarks>
/// Serialized alongside the other worker timing tests. The ExecuteAsync tests below start a real
/// BackgroundService and drive its loop through many drain iterations in quick succession; run in
/// parallel that saturates the host, and the doorbell liveness tests next door explicitly depend
/// on an unsaturated host to distinguish a doorbell-driven claim from a poll-driven one. Leaving
/// this class parallel made ClaimWorkerDoorbellLivenessTests fail reproducibly.
/// </remarks>
[NotInParallel(Order = 106)]
public class RepairDrainWorkerTests {

  [Test]
  public async Task DrainTick_DispatchesGroupedPerType_WithTheUnionWindowAsync() {
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: true);
    var s1 = TrackedGuid.NewMedo().Value;
    var s2 = TrackedGuid.NewMedo().Value;
    var s3 = TrackedGuid.NewMedo().Value;
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", s1, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", s2, 200, 600),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeB", s3, 300, 700),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(2)
      .Because("three rows in two (origin, tenant, type) groups → two directed requests");
    var typeA = transport.Published
      .Select(p => _deserializeRedelivery(p.Envelope))
      .Single(r => r.EventTypes!.Contains("Contracts.TypeA"));
    await Assert.That(typeA.StreamIds!.Count).IsEqualTo(2)
      .Because("one request carries the whole group's stream set");
    await Assert.That(typeA.FromCommitSequence).IsEqualTo(99L)
      .Because("the group's union window, exclusive floor — exactly the burst path's conversion");
    await Assert.That(typeA.ToCommitSequence).IsEqualTo(599L)
      .Because("inclusive ceiling of the union window");
  }

  [Test]
  public async Task DrainTick_OneFailedGroupSend_StillDispatchesTheRemainingGroupsAsync() {
    // Observed live: one throttled broker send threw out of the tick and killed every LATER
    // group's dispatch — their claimed rows had already stamped an attempt, so the failure
    // burned backoff budget across buckets that never even reached the wire.
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: true);
    transport.FailFirst = 1;
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeB", TrackedGuid.NewMedo().Value, 300, 700),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("the second group still dispatches after the first group's send fails — a per-group " +
               "failure costs that group's attempt, never the whole tick.");
  }

  [Test]
  public async Task DrainTick_ASendCanceledByShutdown_StopsTheTickAsync() {
    // The companion to OneFailedGroupSend_StillDispatchesTheRemainingGroups, and the opposite
    // answer. A throttled send costs THAT group's attempt and no more, because the later groups
    // already burned backoff budget on their claims and deserve their shot at the wire. A
    // canceled send is a stopping host: the later groups' claims are already stamped, so their
    // budget is spent either way, and continuing only puts more traffic on a broker the process
    // is disconnecting from.
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: true);
    transport.FailFirst = 1;
    transport.FailFirstWith = new OperationCanceledException();
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeB", TrackedGuid.NewMedo().Value, 300, 700),
    ]);

    await Assert.That(async () =>
        await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("shutdown ends the tick rather than being absorbed as one group's bad luck");
    await Assert.That(transport.Published).IsEmpty()
      .Because("the second group never reaches the wire — the ladder re-offers both after backoff");
  }

  [Test]
  public async Task DrainTick_ReportOnlyMode_NeverClaimsOrDispatchesAsync() {
    // RepairMode.ReportOnly is the operator's explicit opt-DOWN from auto-repair — and the drain
    // is a repair dispatcher. A ReportOnly service whose drain kept claiming and sending
    // redelivery requests would repair anyway (and burn transport quota doing it), turning the
    // opt-down into a dead knob at exactly the moment an operator reaches for it.
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairDrainRatePerSecond = 10,
      RepairMode = IntegrityRepairMode.ReportOnly,
    }, origin, learnTopic: true);
    coordinator.Eligible.Add(
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10));

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(coordinator.ClaimCalls.Count).IsEqualTo(0)
      .Because("ReportOnly must not even claim — a claim stamps an attempt and burns the row's backoff");
    await Assert.That(transport.Published.Count).IsEqualTo(0)
      .Because("ReportOnly never dispatches a repair request");
  }

  [Test]
  public async Task DrainTick_TokensGateTheClaimBudget_AndSpendOnClaimAsync() {
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, _, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 2,
    }, origin, learnTopic: true);
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);
    await Assert.That(coordinator.ClaimCalls.Count).IsEqualTo(1);
    await Assert.That(coordinator.ClaimCalls[0].Limit).IsEqualTo(2)
      .Because("one second at two rows per second buys exactly two claims");

    await worker.DrainTickAsync(0.0, DateTimeOffset.UtcNow, CancellationToken.None);
    await Assert.That(coordinator.ClaimCalls.Count).IsEqualTo(1)
      .Because("the tokens were spent on the claim — zero elapsed time refills nothing");
  }

  [Test]
  public async Task DrainTick_UnlearnedOrigins_AreNeverClaimedAsync() {
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: false);
    coordinator.Eligible.Add(
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10));

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(coordinator.ClaimCalls).IsEmpty()
      .Because("no learned request topic → nothing could be sent → the claim must not burn attempts");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task DrainTick_PreStampRows_WidenTheAskToWholeHistoryAsync() {
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: true);
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, null, null),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    var request = _deserializeRedelivery(transport.Published.Single().Envelope);
    await Assert.That(request.FromCommitSequence).IsNull()
      .Because("a pre-stamp row in the group means the compared range is unknown — whole history, the legacy semantics");
    await Assert.That(request.ToCommitSequence).IsNull();
  }


  // ── ExecuteAsync: the paced loop itself ───────────────────────────────────

  private static (RepairDrainWorker Worker, _drainCoordinator Coordinator) _buildForLoop(
      StreamIntegrityOptions options, TimeProvider clock, ISchemaReadyGate gate) {
    var coordinator = new _drainCoordinator();
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    // The drain only claims for origins whose request topic it has learned; without one the
    // tick is inert by design, and the loop would look broken when it is merely idle.
    tracker.RecordCheckpoint(Guid.NewGuid(), "origin-svc", clock.GetUtcNow(), "origin.requests");
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(tracker);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("drainer-svc"));
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var worker = new RepairDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(options),
      NullLogger<RepairDrainWorker>.Instance,
      clock);
    return (worker, coordinator);
  }

  [Test]
  public async Task ExecuteAsync_WhenDisabled_ParksInsteadOfExitingAsync() {
    // A BackgroundService that returns from ExecuteAsync reads to the host as a crashed worker.
    // Parking keeps a deliberately disabled drain distinguishable from one that fell over --
    // which matters here because the drain being off is a supported configuration.
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var (worker, _) = _buildForLoop(
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, RepairDrainEnabled = false },
      clock,
      SchemaReadyGate.AlreadyReady());

    await worker.StartAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask is not null).IsTrue();
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("a disabled drain stays parked rather than completing, which would look like a crash");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task ExecuteAsync_WithANonPositiveRate_ParksAsync(double rate) {
    // A rate of zero or below cannot pace anything, so it is treated as disabled rather than
    // spinning a loop that can never grant a token.
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var (worker, _) = _buildForLoop(
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, RepairDrainEnabled = true, RepairDrainRatePerSecond = rate },
      clock,
      SchemaReadyGate.AlreadyReady());

    await worker.StartAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse();

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_ShutdownBeforeTheSchemaIsReady_ExitsQuietlyAsync() {
    // The worker parks on the schema gate at startup. A pod stopped while still waiting has no
    // schema to drain against, so the exit must be silent rather than an error on every fast
    // restart.
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var (worker, _) = _buildForLoop(
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, RepairDrainEnabled = true, RepairDrainRatePerSecond = 1 },
      clock,
      new SchemaReadyGate());   // never marked ready

    await worker.StartAsync(CancellationToken.None);

    await Assert.That(async () => await worker.StopAsync(CancellationToken.None)).ThrowsNothing();
  }

  [Test]
  public async Task ExecuteAsync_TicksOnceASecondOfVirtualTimeAsync() {
    // The pace is the point of this worker: it exists to bleed repair requests out slowly rather
    // than flood a recovering system. Driving it on a fake clock proves the cadence without a
    // real delay, so the test cannot flake on a loaded machine.
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var (worker, coordinator) = _buildForLoop(
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, RepairDrainEnabled = true, RepairDrainRatePerSecond = 1 },
      clock,
      SchemaReadyGate.AlreadyReady());

    await worker.StartAsync(CancellationToken.None);

    // Advance virtual time until the loop's interval elapses, waiting on the claim itself
    // rather than on wall-clock. The short pause between advances is pacing, not the
    // assertion -- a tight spin here starves the thread pool and destabilises tests running
    // alongside this one.
    for (var attempt = 0; attempt < 50 && !coordinator.FirstClaim.Task.IsCompleted; attempt++) {
      clock.Advance(TimeSpan.FromSeconds(1));
      await Task.Delay(10);
    }

    await Assert.That(async () => await coordinator.FirstClaim.Task.WaitAsync(TimeSpan.FromSeconds(5)))
      .ThrowsNothing()
      .Because("the loop has to reach the drain tick once its interval elapses");

    await worker.StopAsync(CancellationToken.None);
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static (RepairDrainWorker Worker, _drainCoordinator Coordinator, _captureTransport Transport, IntegrityGapTracker Tracker) _build(
      StreamIntegrityOptions options, Guid origin, bool learnTopic) {
    var coordinator = new _drainCoordinator();
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    if (learnTopic) {
      tracker.RecordCheckpoint(origin, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    } else {
      tracker.RecordCheckpoint(origin, "origin-svc", DateTimeOffset.UtcNow, requestTopic: null);
    }
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(tracker);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("drainer-svc"));
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var worker = new RepairDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),
      Options.Create(options),
      NullLogger<RepairDrainWorker>.Instance);
    return (worker, coordinator, transport, tracker);
  }

  private static RequestRedeliveryCommand _deserializeRedelivery(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestRedeliveryCommand)))!;
  }

  private sealed class _drainCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<IntegrityRepairDrainItem> Eligible { get; } = [];
    public List<(IReadOnlyList<Guid> Origins, int Limit)> ClaimCalls { get; } = [];
    /// <summary>Completes on the first claim so a test can wait on the effect, not on a clock.</summary>
    public TaskCompletionSource FirstClaim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyList<IntegrityRepairDrainItem>> IntegrityClaimRepairDrainAsync(
        IReadOnlyList<Guid> originIds, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
        int limit, CancellationToken cancellationToken = default) {
      ClaimCalls.Add((originIds, limit));
      FirstClaim.TrySetResult();
      var take = Eligible.Where(e => originIds.Contains(e.OriginServiceId)).Take(limit).ToList();
      foreach (var item in take) {
        Eligible.Remove(item);
      }
      return Task.FromResult<IReadOnlyList<IntegrityRepairDrainItem>>(take);
    }
  }

  private sealed class _captureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public int FailFirst { get; set; }
    /// <summary>Thrown in place of the throttle timeout, for the cancellation contract.</summary>
    public Exception? FailFirstWith { get; set; }
    public int Attempts { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) {
        Attempts++;
        if (Attempts <= FailFirst) {
          throw FailFirstWith ?? new TimeoutException("SendMessageAsync timed out (simulated broker throttle)");
        }
        Published.Add((envelope, destination, envelopeType));
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
