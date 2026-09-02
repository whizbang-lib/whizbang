using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Digest-epoch closure runs on the maintenance cadence.
///
/// <para>
/// The epoch substrate (migration 092) is inert without a caller: nothing else advances the
/// closure frontier, so manifests would keep re-aggregating live history forever — the exact
/// unbounded-work failure the epochs exist to end. Riding the maintenance cycle follows the
/// ledger-gauge precedent: no new periodic connection, one extra call on a scope that already
/// exists.
/// </para>
///
/// <para>
/// The settle window passed to closure MUST be the audit's settle window — the two predicates
/// have to agree, or an epoch could seal an event the audit still considers in flight (or hold
/// forever what the audit already trusts).
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public class MaintenanceWorkerEpochClosureTests {

  private sealed class ClosureCoordinator : IWorkCoordinator {
    public bool Throw { get; init; }
    /// <summary>Thrown in place of the generic failure, for the cancellation contract.</summary>
    public Exception? ThrowSpecific { get; init; }
    public int SettleSecondsSeen { get; private set; } = -1;
    public int MaxEpochsSeen { get; private set; } = -1;
    public int Calls { get; private set; }

    public Task<int> CloseDigestEpochsAsync(
        int settleSeconds, int maxEpochs, CancellationToken cancellationToken = default) {
      Calls++;
      SettleSecondsSeen = settleSeconds;
      MaxEpochsSeen = maxEpochs;
      if (ThrowSpecific is not null) {
        return Task.FromException<int>(ThrowSpecific);
      }
      return Throw
        ? Task.FromException<int>(new InvalidOperationException("closure unavailable"))
        : Task.FromResult(1);
    }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static MaintenanceWorker _build(ClosureCoordinator coord, StreamIntegrityOptions? integrity) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    if (integrity is not null) {
      services.AddSingleton(Options.Create(integrity));
    }
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, StuckRowSentinelEnabled = false }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  [Test]
  public async Task MaintenanceCycle_ClosesEpochs_WithTheAuditSettleWindowAndConfiguredCapAsync() {
    var coord = new ClosureCoordinator();
    var worker = _build(coord, new StreamIntegrityOptions {
      AuditSettleWindowMinutes = 45,
      MaxEpochClosuresPerMaintenanceCycle = 12,
    });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Calls).IsEqualTo(1)
      .Because("the substrate is inert without a caller — nothing else advances the frontier");
    await Assert.That(coord.SettleSecondsSeen).IsEqualTo(45 * 60)
      .Because("closure and the audit must settle on the SAME window, or a seal can disagree with a manifest");
    await Assert.That(coord.MaxEpochsSeen).IsEqualTo(12)
      .Because("closure work per cycle is bounded by the operator's cap, not by backlog size");
  }

  [Test]
  public async Task EpochClosureDisabled_DoesNotCallTheCoordinatorAsync() {
    var coord = new ClosureCoordinator();
    var worker = _build(coord, new StreamIntegrityOptions { EpochClosureEnabled = false });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Calls).IsEqualTo(0)
      .Because("the opt-out must actually opt out — a disabled feature that still runs is a lie in the config");
  }

  [Test]
  public async Task NoIntegrityOptionsRegistered_SkipsClosureQuietlyAsync() {
    // A host without the stream-integrity subsystem has no epochs to close; the cycle must not
    // manufacture defaults and start closure work the host never asked for.
    var coord = new ClosureCoordinator();
    var worker = _build(coord, integrity: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task ClosureFailure_DoesNotFailTheMaintenanceCycleAsync() {
    // Closure is convergence bookkeeping; the reaper and destruction hooks are correctness work.
    // A closure error must never stop them — the frontier just advances on a later cycle.
    var coord = new ClosureCoordinator { Throw = true };
    var worker = _build(coord, new StreamIntegrityOptions());

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Calls).IsEqualTo(1)
      .Because("the cycle attempted closure and completed despite the failure");
  }

  [Test]
  public async Task ClosureCancelled_StopsTheCycleRatherThanAdvancingToTheReaperAsync() {
    // The companion to ClosureFailure_DoesNotFailTheMaintenanceCycle, and the opposite answer.
    // Convergence bookkeeping must never stop the reaper when it FAILS — but a cancelled closure
    // is a stopping host, and what follows it in the cycle is the reap and the sweep, which take
    // the locks the completion path needs.
    var coord = new ClosureCoordinator { ThrowSpecific = new OperationCanceledException() };
    var worker = _build(coord, new StreamIntegrityOptions());

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("the frontier advancing on a later cycle is fine; reaping rows on a host that "
             + "asked to stop is not");
  }
}
