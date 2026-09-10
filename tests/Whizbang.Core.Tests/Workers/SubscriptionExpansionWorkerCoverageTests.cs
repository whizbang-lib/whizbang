using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for the three early-exit branches of <see cref="SubscriptionExpansionWorker.RunOnceAsync"/>
/// the primary suite never reaches: a schema-only/diagnostic host with no coordinator or type
/// provider registered, a service that consumes no event types at all, and a boot where the
/// registry already accounts for everything (nothing pending to request). One-shot startup
/// reconciler: skipping these branches wrongly would mean a schema-only host throws on boot, or a
/// steady-state host re-broadcasts a redelivery request every single boot for nothing.
/// </summary>
public class SubscriptionExpansionWorkerCoverageTests {

  public sealed record ProbeEvent : IEvent {
    [StreamId]
    public Guid Sid { get; init; }
  }

  private static readonly string _probeType = TypeNameFormatter.FormatClrTypeName(typeof(ProbeEvent));

  private sealed class _emptyTypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class _oneTypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(ProbeEvent)];
  }

  /// <summary>In-memory consumed-type registry mirroring production status semantics.</summary>
  private sealed class _registryCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public Dictionary<string, ConsumedTypeBackfillStatus> Registry { get; } = [];

    public Task<IReadOnlyList<ConsumedTypeRegistration>> GetConsumedTypeRegistrationsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<ConsumedTypeRegistration>>(
        [.. Registry.Select(kv => new ConsumedTypeRegistration { EventType = kv.Key, Status = kv.Value })]);

    public Task RegisterConsumedTypesAsync(IReadOnlyList<string> eventTypes, bool asBaseline, CancellationToken cancellationToken = default) {
      foreach (var type in eventTypes) {
        Registry.TryAdd(type, asBaseline ? ConsumedTypeBackfillStatus.Baseline : ConsumedTypeBackfillStatus.Pending);
      }
      return Task.CompletedTask;
    }

    public Task MarkConsumedTypeBackfillRequestedAsync(IReadOnlyList<string> eventTypes, CancellationToken cancellationToken = default) {
      foreach (var type in eventTypes) {
        if (Registry.TryGetValue(type, out var status) && status == ConsumedTypeBackfillStatus.Pending) {
          Registry[type] = ConsumedTypeBackfillStatus.Requested;
        }
      }
      return Task.CompletedTask;
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
    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private static SubscriptionExpansionWorker _build(IServiceCollection services) =>
    _build(services, new StreamIntegrityOptions(), NullLogger<SubscriptionExpansionWorker>.Instance);

  private static SubscriptionExpansionWorker _build(
      IServiceCollection services, StreamIntegrityOptions options, ILogger<SubscriptionExpansionWorker> logger) {
    var sp = services.BuildServiceProvider();
    var gate = new Whizbang.Core.Workers.SchemaReadyGate();
    gate.MarkReady();
    return new SubscriptionExpansionWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(options),
      logger);
  }

  /// <summary>What breaks: a schema-only/diagnostic host has no work coordinator or event-type
  /// provider registered by design. If this reconciler assumed they exist, that host would crash
  /// on every boot instead of simply having nothing to reconcile with.</summary>
  [Test]
  public async Task RunOnceAsync_NoCoordinatorOrTypeProviderRegistered_ReturnsWithoutThrowingAsync() {
    var transport = new _captureTransport();
    var logger = new _capturingLogger();
    var services = new ServiceCollection();
    // Deliberately no IWorkCoordinator and no IEventTypeProvider — schema-only / diagnostic
    // composition. A transport IS present, so "nothing was broadcast" is a fact about the
    // reconciler rather than about there being nowhere to broadcast to.
    services.AddSingleton<ITransport>(transport);
    var worker = _build(services, new StreamIntegrityOptions(), logger);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty()
      .Because("with nothing to reconcile against, a redelivery request would be broadcast on "
             + "behalf of a catalog the host cannot even enumerate");
    await Assert.That(logger.Entries).IsEmpty()
      .Because("the return happens before any reconciliation decision — a baseline or "
             + "'backfill disabled' entry here would mean the pass actually ran");
  }

  /// <summary>What breaks: a service that consumes zero event types (a pure command-only or
  /// publish-only service) must not attempt to write an empty baseline or crash reasoning about a
  /// registry it has nothing to compare against.</summary>
  [Test]
  public async Task RunOnceAsync_NoConsumedEventTypes_ReturnsWithoutTouchingTheRegistryAsync() {
    var coordinator = new _registryCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IEventTypeProvider>(new _emptyTypeProvider());
    var worker = _build(services);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Registry).IsEmpty()
      .Because("an empty catalog has nothing to baseline or compare — writing anything here would fabricate a registry entry for a type that doesn't exist");
  }

  /// <summary>What breaks: on a steady-state boot where the registry already accounts for every
  /// catalog type (nothing new, nothing left Pending from a prior partial run), the reconciler must
  /// stop instead of re-broadcasting a redelivery request — a redelivery storm on every ordinary
  /// restart of an otherwise healthy fleet.</summary>
  [Test]
  public async Task RunOnceAsync_EverythingAlreadyAccountedFor_DoesNotRebroadcastAsync() {
    var coordinator = new _registryCoordinator();
    // Already registered and already requested in a prior boot — nothing pending.
    coordinator.Registry[_probeType] = ConsumedTypeBackfillStatus.Requested;
    var transport = new _captureTransport();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IEventTypeProvider>(new _oneTypeProvider());
    services.AddSingleton<ITransport>(transport);
    var worker = _build(services);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty()
      .Because("nothing is Pending, so there is nothing to request — a broadcast here would be a redelivery storm on every ordinary restart of a healthy fleet");
    await Assert.That(coordinator.Registry[_probeType]).IsEqualTo(ConsumedTypeBackfillStatus.Requested)
      .Because("the early return must leave the already-settled registration untouched");
  }

  // Target: src/Whizbang.Core/Workers/SubscriptionExpansionWorker.cs:134-136 — the
  // "missing infrastructure" arm of _sendBackfillRequestAsync.
  //
  // Repair is ENABLED here (BackfillOnSubscriptionGrowth default true AND
  // RepairMode = AutoRepairCapped), so the reconciler genuinely tries to send. What is absent is
  // the transport and serializer — the shape of a host composed without a broker (a migration
  // runner, a diagnostics sidecar, a misconfigured deployment).
  //
  // The distinguishing evidence is the LOG EVENT, not the registry status: "backfill disabled"
  // (event 73) and "request skipped" (event 74) BOTH leave the type Pending, so asserting Pending
  // alone is satisfied by either path and proves neither. Asserting event 74 — and the absence of
  // 73 — pins it to the send attempt.
  //
  // What breaks if this regressed to throwing or to marking Requested anyway: throwing takes down
  // a boot that had nothing wrong with it beyond having no broker; marking Requested burns the
  // one-shot retry, so the expansion is recorded as repaired, never re-detected, and the missing
  // history stays missing forever.
  [Test]
  public async Task RunOnceAsync_RepairEnabledButNoTransport_SkipsTheRequestAndLeavesTypesPendingAsync() {
    var coordinator = new _registryCoordinator();
    // A prior boot's baseline: this is NOT first boot, so the new type reads as an expansion.
    coordinator.Registry["Contracts.PriorType"] = ConsumedTypeBackfillStatus.Baseline;
    var logger = new _capturingLogger();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IEventTypeProvider>(new _oneTypeProvider());
    // No ITransport, no IEnvelopeSerializer, no IServiceInstanceProvider — nothing to send with.
    var worker = _build(services,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped }, logger);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(logger.Entries.Any(e => e.EventId == 74)).IsTrue()
      .Because("the reconciler must report that it TRIED and could not — missing infrastructure "
             + "and a disabled repair leave the same registry state, and only this log tells an "
             + "operator which one they are looking at");
    await Assert.That(logger.Entries.Any(e => e.EventId == 73)).IsFalse()
      .Because("repair is enabled in this composition; reporting 'backfill disabled' would send an "
             + "operator to a setting that is already correct");
    await Assert.That(coordinator.Registry[_probeType]).IsEqualTo(ConsumedTypeBackfillStatus.Pending)
      .Because("a request that was never sent must not be recorded as Requested — that would burn "
             + "the retry and leave the missing history missing for good");
  }

  private sealed class _capturingLogger : ILogger<SubscriptionExpansionWorker> {
    public List<(int EventId, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
      => Entries.Add((eventId.Id, formatter(state, exception)));
  }
}
