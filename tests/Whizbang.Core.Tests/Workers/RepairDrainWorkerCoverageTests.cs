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
/// Coverage for <see cref="RepairDrainWorker"/> paths the primary suite
/// (<see cref="RepairDrainWorkerTests"/>) doesn't reach: the disabled-park delay/return itself,
/// the tick loop's two failure catches (cancellation ends the loop, any other exception is logged
/// and the loop keeps going), the early return when dispatch infrastructure isn't wired up, and a
/// claimed group whose origin fell out of the in-memory learned-origin snapshot mid-tick.
/// </summary>
/// <docs>proposals/paced-repair-drain</docs>
/// <remarks>
/// Shares <see cref="RepairDrainWorkerTests"/>'s serialization group: several tests here start a
/// real <see cref="RepairDrainWorker"/> BackgroundService and drive its loop via a
/// <see cref="FakeTimeProvider"/>, same as that class's ExecuteAsync tests.
/// </remarks>
[NotInParallel(Order = 106)]
public class RepairDrainWorkerCoverageTests {

  // ── ExecuteAsync: the disabled-park guard ──────────────────────────────────

  [Test]
  public async Task ExecuteAsync_Disabled_NeverClaimsAndShutsDownCleanlyWhenStoppedAsync() {
    // If the disabled guard's delay/return ever got skipped (e.g. a refactor dropped the
    // try/catch around the infinite delay), a repair drain explicitly turned off would either
    // busy-loop burning CPU with nothing to wait on, or fall through into ticking anyway --
    // silently undoing the operator's opt-out and burning attempt budget it was told not to.
    var origin = TrackedGuid.NewMedo().Value;
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var (worker, coordinator, _) = _buildLoop(
      new StreamIntegrityOptions { RepairDrainEnabled = false },
      clock, SchemaReadyGate.AlreadyReady(), origin);
    coordinator.Eligible.Add(
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10));

    await worker.StartAsync(CancellationToken.None);

    // Advance virtual time well past several would-be tick intervals; a disabled drain must
    // never wake up and claim regardless of how much time passes.
    for (var i = 0; i < 5; i++) {
      clock.Advance(TimeSpan.FromSeconds(1));
      await Task.Delay(10);
    }
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("a disabled drain parks rather than exiting -- exiting would read to the host as a crashed worker");
    await Assert.That(coordinator.ClaimCalls).IsEmpty()
      .Because("disabled must never claim; a claim stamps an attempt and burns the row's backoff for nothing");

    await worker.StopAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue();
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("shutdown out of the disabled park must be a clean cancellation, never an escaped fault");
  }

  // ── ExecuteAsync: the tick loop's two catches ──────────────────────────────

  [Test]
  public async Task ExecuteAsync_TickThrowsOperationCanceled_EndsTheLoopWithoutFaultingAsync() {
    // A cancellation surfaced from inside the tick (not necessarily the host's own shutdown
    // token -- e.g. a downstream call with its own timeout-linked token) must end the loop
    // quietly. If this catch/break were ever removed, the exception would escape ExecuteAsync
    // and, under the host's default StopHost behavior, take the whole process down over what
    // should have been a routine, contained condition.
    var origin = TrackedGuid.NewMedo().Value;
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var (worker, coordinator, _) = _buildLoop(
      new StreamIntegrityOptions { RepairDrainEnabled = true, RepairDrainRatePerSecond = 10 },
      clock, SchemaReadyGate.AlreadyReady(), origin);
    coordinator.ThrowOnClaim.Enqueue(new OperationCanceledException("simulated"));
    coordinator.Eligible.Add(
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10));

    await worker.StartAsync(CancellationToken.None);
    for (var attempt = 0; attempt < 50 && coordinator.ClaimCalls.Count == 0; attempt++) {
      clock.Advance(TimeSpan.FromSeconds(1));
      await Task.Delay(10);
    }
    await Assert.That(coordinator.ClaimCalls.Count).IsGreaterThanOrEqualTo(1)
      .Because("the loop must actually reach the tick for the thrown cancellation to matter");

    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue()
      .Because("the loop must end on its own once the tick raises a cancellation, without needing an external stop");
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("the cancellation ends the loop cleanly rather than escaping as a fault");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_TickThrowsGenericException_LogsAndStillDispatchesNextTickAsync() {
    // Observed live: an unguarded per-tick exception would permanently stop the paced drain
    // until the process restarted, silently reverting to whatever discovery-side repair path
    // remains (or none). "Did not throw" is the weak version of this invariant -- the strong
    // one is that the SAME eligible row still gets dispatched once a later tick's claim works.
    var origin = TrackedGuid.NewMedo().Value;
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var (worker, coordinator, transport) = _buildLoop(
      new StreamIntegrityOptions { RepairDrainEnabled = true, RepairDrainRatePerSecond = 10 },
      clock, SchemaReadyGate.AlreadyReady(), origin);
    coordinator.ThrowOnClaim.Enqueue(new InvalidOperationException("simulated transient failure"));
    coordinator.Eligible.Add(
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10));

    await worker.StartAsync(CancellationToken.None);
    // First tick: the claim throws and must be swallowed. Second tick: the same eligible row is
    // still there (a thrown claim never stamped it), so a later, working claim should dispatch it.
    for (var attempt = 0; attempt < 100 && transport.Published.Count == 0; attempt++) {
      clock.Advance(TimeSpan.FromSeconds(1));
      await Task.Delay(10);
    }

    await Assert.That(coordinator.ClaimCalls.Count).IsGreaterThanOrEqualTo(2)
      .Because("the failed first tick must not stop the loop from reaching a second tick");
    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("the row survives the first tick's swallowed failure and dispatches on a later tick -- proof the loop kept going, not merely that nothing threw");

    await worker.StopAsync(CancellationToken.None);
  }

  // ── DrainTickAsync: missing dispatch infrastructure ────────────────────────

  [Test]
  public async Task DrainTick_TransportServiceMissing_NeverClaimsAsync() {
    // If this early return regressed, a repair drain running before dispatch infrastructure is
    // fully wired up (a misconfigured DI container, or a transport not yet registered during
    // startup) would claim rows it can never send -- burning their backoff budget for an attempt
    // that could not leave the process, while the ledger keeps the backlog durable either way.
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, _, tracker) = _buildTick(
      new StreamIntegrityOptions { RepairDrainRatePerSecond = 10 }, includeTransport: false);
    tracker.RecordCheckpoint(origin, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    coordinator.Eligible.Add(
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10));

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(coordinator.ClaimCalls).IsEmpty()
      .Because("missing dispatch infrastructure must short-circuit before the claim; claiming here " +
               "would burn the row's backoff for a request that can never leave the process");
  }

  // ── DrainTickAsync: origin fell out of the learned-origin snapshot mid-tick ─

  [Test]
  public async Task DrainTick_ClaimedGroupsOriginNoLongerLearned_SkipsThatGroupButDispatchesTheRestAsync() {
    // The origin snapshot for this tick is read once, early. If the ledger's claim -- a separate
    // round-trip -- returns a row for an origin that fell out of that snapshot in between (the
    // origin stopped checkpointing, or the tracker evicted it), dispatching it would reach for a
    // request topic that's no longer known. If this per-group skip regressed to a hard failure,
    // one stale row would also take down every OTHER group's dispatch in the same tick.
    var learnedOrigin = TrackedGuid.NewMedo().Value;
    var staleOrigin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, transport, tracker) = _buildTick(
      new StreamIntegrityOptions { RepairDrainRatePerSecond = 10 });
    tracker.RecordCheckpoint(learnedOrigin, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    // staleOrigin is deliberately never recorded on the tracker -- it models an origin the
    // in-memory snapshot no longer recognizes by the time the claim comes back.
    coordinator.ForcedClaim = [
      new IntegrityRepairDrainItem(learnedOrigin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10),
      new IntegrityRepairDrainItem(staleOrigin, "tenant-a", "Contracts.TypeB", TrackedGuid.NewMedo().Value, 1, 10),
    ];

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("the learned origin's group must still dispatch even though the other claimed group's origin fell out of the snapshot");
    var dispatched = _deserializeRedelivery(transport.Published.Single().Envelope);
    await Assert.That(dispatched.EventTypes!.Contains("Contracts.TypeA")).IsTrue()
      .Because("the dispatched request must be the learned origin's group, never the stale one's");
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static (RepairDrainWorker Worker, _scriptedCoordinator Coordinator, _captureTransport Transport) _buildLoop(
      StreamIntegrityOptions options, TimeProvider clock, ISchemaReadyGate gate, Guid origin) {
    var coordinator = new _scriptedCoordinator();
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(origin, "origin-svc", clock.GetUtcNow(), "origin.requests");
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
    return (worker, coordinator, transport);
  }

  private static (RepairDrainWorker Worker, _scriptedCoordinator Coordinator, _captureTransport Transport, IntegrityGapTracker Tracker) _buildTick(
      StreamIntegrityOptions options, bool includeTransport = true) {
    var coordinator = new _scriptedCoordinator();
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    if (includeTransport) {
      services.AddSingleton<ITransport>(transport);
    }
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

  /// <summary>
  /// Configurable claim behavior: normally serves from <see cref="Eligible"/> filtered to the
  /// requested origins (mirrors the real ledger), optionally throws a queued exception instead
  /// (for the tick-loop failure-catch tests), or -- when <see cref="ForcedClaim"/> is set --
  /// returns that verbatim regardless of the requested origins, modeling a claim whose rows no
  /// longer match the in-memory learned-origin snapshot taken earlier in the same tick.
  /// </summary>
  private sealed class _scriptedCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<IntegrityRepairDrainItem> Eligible { get; } = [];
    public List<IntegrityRepairDrainItem>? ForcedClaim { get; set; }
    public Queue<Exception> ThrowOnClaim { get; } = new();
    public List<(IReadOnlyList<Guid> Origins, int Limit)> ClaimCalls { get; } = [];

    public Task<IReadOnlyList<IntegrityRepairDrainItem>> IntegrityClaimRepairDrainAsync(
        IReadOnlyList<Guid> originIds, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
        int limit, CancellationToken cancellationToken = default) {
      ClaimCalls.Add((originIds, limit));
      if (ThrowOnClaim.Count > 0) {
        throw ThrowOnClaim.Dequeue();
      }
      if (ForcedClaim is { } forced) {
        return Task.FromResult<IReadOnlyList<IntegrityRepairDrainItem>>(forced);
      }
      var take = Eligible.Where(e => originIds.Contains(e.OriginServiceId)).Take(limit).ToList();
      foreach (var item in take) {
        Eligible.Remove(item);
      }
      return Task.FromResult<IReadOnlyList<IntegrityRepairDrainItem>>(take);
    }
  }

  private sealed class _captureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) {
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
