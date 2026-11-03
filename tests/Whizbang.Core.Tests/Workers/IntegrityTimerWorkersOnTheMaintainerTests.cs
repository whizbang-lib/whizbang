// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Core.Tests.Perspectives;
using Whizbang.Core.Tests.Startup;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The integrity timer workers run on the maintainer (#2001). The deep audit and the paced repair drain also ask the
/// housekeeping idle gate, so they wait while the service is busy and are forced through after MaxDeferral. Checkpoint
/// publishing runs on the maintainer but is never idle-gated: a consumer reads a missing checkpoint as its liveness
/// alarm, so the checkpoint must keep its cadence under load.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityAuditWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/RepairDrainWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityCheckpointWorker.cs</code-under-test>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
[NotInParallel(Order = 106)]
[Category("Workers")]
public class IntegrityTimerWorkersOnTheMaintainerTests {
  private static readonly DateTimeOffset _origin = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
  private static readonly ServiceBacklog _busy = new() { UnprocessedInboxRows = 500, ActiveLeasedRows = 2 };

  private sealed class IntegrityCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public ServiceBacklog? Backlog { get; set; } = new();
    public int AuditClaims { get; private set; }
    public int DrainClaims { get; private set; }
    public int CheckpointAdvances { get; private set; }
    public Guid LocalServiceId { get; } = TrackedGuid.New().Value;

    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(Backlog);

    public Task<bool> TryClaimIntegrityAuditCycleAsync(TimeSpan claimWindow, CancellationToken cancellationToken = default) {
      AuditClaims++;
      return Task.FromResult(false);   // the cycle stops at the claim; what matters is whether it was reached
    }

    public Task<IReadOnlyList<IntegrityRepairDrainItem>> IntegrityClaimRepairDrainAsync(
        IReadOnlyList<Guid> originIds, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
        int limit, CancellationToken cancellationToken = default) {
      DrainClaims++;
      return Task.FromResult<IReadOnlyList<IntegrityRepairDrainItem>>([]);
    }

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(LocalServiceId);

    public Task<IntegrityCheckpointWindow?> AdvanceIntegrityCheckpointAsync(CancellationToken cancellationToken = default) {
      CheckpointAdvances++;
      return Task.FromResult<IntegrityCheckpointWindow?>(new IntegrityCheckpointWindow { FromCommitSequence = 0, ToCommitSequence = 0 });
    }
  }

  private sealed class NoTransport : ITransport {
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class InstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New().Value;
    public string ServiceName => "drainer-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed record Host(ServiceProvider Services, IntegrityCoordinator Coordinator, HousekeepingCoordinator Housekeeping,
      FakeTimeProvider Clock, IntegrityGapTracker Tracker) : IAsyncDisposable {
    public ValueTask DisposeAsync() => Services.DisposeAsync();
  }

  private static async Task<Host> _hostAsync(bool? maintainer, Action<ServiceCollection>? extra = null) {
    var clock = new FakeTimeProvider(_origin);
    var coordinator = new IntegrityCoordinator();
    var housekeeping = new HousekeepingCoordinator(
      new HousekeepingCoordinator.Settings { SettledCooldown = TimeSpan.Zero, MaxDeferral = TimeSpan.FromHours(1) }, clock);
    var tracker = new IntegrityGapTracker();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton(housekeeping);
    services.AddSingleton<IDispatcher>(new RebuildEventDispatcherDouble());
    services.AddSingleton(tracker);
    if (maintainer is { } holds) {
      services.AddSingleton(await MaintainerDutyHolders.ElectedAsync(grants: holds));
    }
    extra?.Invoke(services);
    return new Host(services.BuildServiceProvider(), coordinator, housekeeping, clock, tracker);
  }

  // ── the deep audit ──────────────────────────────────────────────────────

  private static IntegrityAuditWorker _audit(Host host) => new(
    host.Services.GetRequiredService<IServiceScopeFactory>(),
    Options.Create(new StreamIntegrityOptions()), NullLogger<IntegrityAuditWorker>.Instance);

  [Test]
  public async Task TheAudit_OnAnInstanceThatDoesNotHoldTheMaintainerDuty_DoesNothingAsync() {
    await using var host = await _hostAsync(maintainer: false);

    await _audit(host).RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(host.Coordinator.AuditClaims).IsEqualTo(0);
  }

  [Test]
  public async Task TheAudit_OnTheMaintainerOfAQuietService_RunsAndReleasesTheSlotAsync() {
    await using var host = await _hostAsync(maintainer: true);

    await _audit(host).RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(host.Coordinator.AuditClaims).IsEqualTo(1);
    await Assert.That(host.Housekeeping.TryBegin(HousekeepingCoordinator.Activity.Integrity, new ServiceBacklog()).Granted)
      .IsTrue().Because("the audit releases the integrity slot when its cycle ends");
  }

  [Test]
  public async Task TheAudit_OnABusyService_WaitsUntilMaxDeferral_ThenIsForcedThroughAsync() {
    await using var host = await _hostAsync(maintainer: true);
    host.Coordinator.Backlog = _busy;
    var audit = _audit(host);

    await audit.RunAuditOnceAsync(CancellationToken.None);
    var claimsWhileBusy = host.Coordinator.AuditClaims;
    host.Clock.Advance(TimeSpan.FromHours(1));
    await audit.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(claimsWhileBusy).IsEqualTo(0).Because("the audit competes with live traffic for the same store");
    await Assert.That(host.Coordinator.AuditClaims).IsEqualTo(1).Because("a service that never goes quiet is still audited");
  }

  // ── the paced repair drain ──────────────────────────────────────────────

  private static async Task<(Host Host, RepairDrainWorker Drain)> _drainAsync(bool? maintainer) {
    var host = await _hostAsync(maintainer, services => {
      services.AddSingleton<ITransport>(new NoTransport());
      services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
      services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider());
    });
    host.Tracker.RecordCheckpoint(TrackedGuid.New().Value, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var drain = new RepairDrainWorker(
      host.Services.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new StreamIntegrityOptions {
        RepairMode = IntegrityRepairMode.AutoRepairCapped,
        RepairDrainRatePerSecond = 10,
        RepairTopic = "drainer.replies",
      }),
      NullLogger<RepairDrainWorker>.Instance);
    return (host, drain);
  }

  [Test]
  public async Task TheRepairDrain_OnAnInstanceThatDoesNotHoldTheMaintainerDuty_ClaimsNothingAsync() {
    var (host, drain) = await _drainAsync(maintainer: false);
    await using var _ = host;

    await drain.DrainTickAsync(1, _origin, CancellationToken.None);

    await Assert.That(host.Coordinator.DrainClaims).IsEqualTo(0);
  }

  [Test]
  public async Task TheRepairDrain_OnTheMaintainerOfAQuietService_ClaimsAsync() {
    var (host, drain) = await _drainAsync(maintainer: true);
    await using var _ = host;

    await drain.DrainTickAsync(1, _origin, CancellationToken.None);

    await Assert.That(host.Coordinator.DrainClaims).IsEqualTo(1);
    await Assert.That(host.Housekeeping.TryBegin(HousekeepingCoordinator.Activity.Integrity, new ServiceBacklog()).Granted)
      .IsTrue().Because("the drain tick releases the integrity slot when it ends");
  }

  [Test]
  public async Task TheRepairDrain_OnABusyService_WaitsUntilMaxDeferral_ThenIsForcedThroughAsync() {
    var (host, drain) = await _drainAsync(maintainer: true);
    await using var _ = host;
    host.Coordinator.Backlog = _busy;

    await drain.DrainTickAsync(1, _origin, CancellationToken.None);
    var claimsWhileBusy = host.Coordinator.DrainClaims;
    host.Clock.Advance(TimeSpan.FromHours(1));
    await drain.DrainTickAsync(1, _origin, CancellationToken.None);

    await Assert.That(claimsWhileBusy).IsEqualTo(0);
    await Assert.That(host.Coordinator.DrainClaims).IsEqualTo(1);
  }

  // ── checkpoint publishing: the maintainer, never the idle gate ─────────

  private static IntegrityCheckpointWorker _checkpoint(Host host, ILogger<IntegrityCheckpointWorker>? logger = null) => new(
    host.Services.GetRequiredService<IServiceScopeFactory>(),
    Options.Create(new StreamIntegrityOptions { CheckpointIntervalSeconds = 60 }),
    logger ?? NullLogger<IntegrityCheckpointWorker>.Instance);

  [Test]
  public async Task Checkpoints_OnAnInstanceThatDoesNotHoldTheMaintainerDuty_AreNotPublishedAsync() {
    await using var host = await _hostAsync(maintainer: false);

    await _checkpoint(host).RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(host.Coordinator.CheckpointAdvances).IsEqualTo(0);
  }

  [Test]
  public async Task Checkpoints_AreNotIdleGated_BecauseAMissingCheckpointIsTheConsumersLivenessAlarmAsync() {
    // Everything that could hold integrity work back is in force: the service is busy, and dead-letter recovery holds
    // the slot integrity work ranks below. The checkpoint still publishes. Gating it would make every busy service
    // look dead to its consumers, which read a missing checkpoint as their liveness alarm.
    await using var host = await _hostAsync(maintainer: true);
    host.Coordinator.Backlog = _busy;
    _ = host.Housekeeping.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, backlog: null);
    var integrity = host.Housekeeping.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy);

    await _checkpoint(host).RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(integrity.Granted).IsFalse().Because("the gate would refuse integrity work right now");
    await Assert.That(host.Coordinator.CheckpointAdvances).IsEqualTo(1)
      .Because("the checkpoint keeps its cadence under load");
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task TheLivenessCheck_AllowsForTheTimeReceivedCheckpointsWaitedInTheQueueAsync(bool drainDeferred) {
    // A received checkpoint waits in the integrity delivery queue while the idle gate defers the drain, so on a busy
    // consumer a live origin's checkpoints arrive late by as long as the deferral. The liveness check widens its bound
    // by that time rather than reporting a live origin as silent.
    var now = DateTimeOffset.UtcNow;
    var deliveryClock = new FakeTimeProvider(now - TimeSpan.FromMinutes(15));
    IntegrityDeliveryWorker? drain = null;
    await using var host = await _hostAsync(maintainer: true, services =>
      services.AddSingleton(sp => drain ??= new IntegrityDeliveryWorker(
        sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(new StreamIntegrityOptions()),
        new IntegrityDeliveryRegistry(), NullLogger<IntegrityDeliveryWorker>.Instance, deliveryClock)));
    var origin = TrackedGuid.New().Value;
    host.Tracker.RecordCheckpoint(origin, "quiet-origin", now - TimeSpan.FromMinutes(10));
    if (drainDeferred) {
      host.Coordinator.Backlog = _busy;
      var pass = await host.Services.GetRequiredService<IntegrityDeliveryWorker>().RunPassAsync(CancellationToken.None);
      await Assert.That(pass.Outcome).IsEqualTo(IntegrityDeliveryWorker.PassOutcome.Deferred);
      host.Coordinator.Backlog = new ServiceBacklog();
    }
    var logger = new StaleOriginLogger();

    await _checkpoint(host, logger).RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(logger.StaleOriginWarnings).IsEqualTo(drainDeferred ? 0 : 1)
      .Because(drainDeferred
        ? "ten minutes of silence inside a fifteen-minute deferral is the queue, not the origin"
        : "with the drain admitted, ten minutes of silence is more than three checkpoint intervals");
  }

  private sealed class StaleOriginLogger : ILogger<IntegrityCheckpointWorker> {
    public int StaleOriginWarnings { get; private set; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning && formatter(state, exception).Contains("quiet-origin", StringComparison.Ordinal)) {
        StaleOriginWarnings++;
      }
    }
  }
}
