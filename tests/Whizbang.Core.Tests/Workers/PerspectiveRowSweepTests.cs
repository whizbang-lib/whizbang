#pragma warning disable CA1707

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The maintenance worker's perspective-row retention step: the sweeps finally have a production
/// invocation, and registered guards front them. Locks the seam semantics — the guard is offered
/// the collected batch, Proceed releases holds, Defer/Cancel hold durably, an ABSENT decision
/// defers (fail-safe, never fail-open), a throwing guard gets the retry ladder, the cap sweep is
/// fleet-claimed, and OnAfterReap fires with the released set after the sweeps.
/// </summary>
/// <docs>proposals/pre-destruction-seam</docs>
public class PerspectiveRowSweepTests {

  private sealed class GuardedModel;

  private sealed class RowSweepCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<PerspectiveRowDestructionTarget> Collectable { get; init; } = [];
    public bool CapClaimResult { get; init; } = true;
    public bool SettledFoldClaimResult { get; init; } = true;
    public int SettledFoldCalls { get; private set; }
    public TimeSpan SettledFoldIdle { get; private set; }
    public int SettledFoldLimit { get; private set; }
    public int LastCapBatchSize { get; private set; }
    public IReadOnlyCollection<string>? CollectedFor { get; private set; }
    public List<(PerspectiveRowRef Row, DateTimeOffset Until)> Held { get; } = [];
    public List<PerspectiveRowRef> Released { get; } = [];
    public List<PerspectiveRowRef> FailureRecorded { get; } = [];
    public int TtlReapCalls { get; private set; }
    public int CapReapCalls { get; private set; }

    public Task<IReadOnlyList<PerspectiveRowDestructionTarget>> GetPerspectiveRowsAboutToReapAsync(
        IReadOnlyCollection<string> clrTypeNames, int perTableLimit = 500, CancellationToken cancellationToken = default) {
      CollectedFor = clrTypeNames;
      return Task.FromResult<IReadOnlyList<PerspectiveRowDestructionTarget>>(Collectable);
    }

    public Task HoldPerspectiveRowDestructionAsync(
        IReadOnlyCollection<PerspectiveRowRef> rows, DateTimeOffset holdUntil, CancellationToken cancellationToken = default) {
      Held.AddRange(rows.Select(r => (r, holdUntil)));
      return Task.CompletedTask;
    }

    public Task ReleasePerspectiveRowHoldsAsync(
        IReadOnlyCollection<PerspectiveRowRef> rows, CancellationToken cancellationToken = default) {
      Released.AddRange(rows);
      return Task.CompletedTask;
    }

    public Task<int> RecordPerspectiveRowDestructionFailureAsync(
        IReadOnlyCollection<PerspectiveRowRef> rows, TimeSpan retryBackoff, int maxRetries,
        OnDestroyFailure onDestroyFailure, CancellationToken cancellationToken = default) {
      FailureRecorded.AddRange(rows);
      return Task.FromResult(1);
    }

    public Task<PerspectiveRowReapResult> ReapEnrolledPerspectiveRowsAsync(
        int batchSize = 5000, CancellationToken cancellationToken = default) {
      TtlReapCalls++;
      return Task.FromResult(new PerspectiveRowReapResult(0, "ok"));
    }

    public Task<PerspectiveRowReapResult> ReapPerspectiveRowCapsAsync(int batchSize = 5000, CancellationToken cancellationToken = default) {
      CapReapCalls++;
      LastCapBatchSize = batchSize;
      return Task.FromResult(new PerspectiveRowReapResult(0, "ok"));
    }

    public Task<bool> TryClaimSettledFoldSweepAsync(TimeSpan claimWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult(SettledFoldClaimResult);

    public Task<int> FoldSettledApplyPathsAsync(TimeSpan idleWindow, int limit = 1000, CancellationToken cancellationToken = default) {
      SettledFoldCalls++;
      SettledFoldIdle = idleWindow;
      SettledFoldLimit = limit;
      return Task.FromResult(1);
    }

    public Task<bool> TryClaimRowCapSweepAsync(TimeSpan claimWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult(CapClaimResult);
  }

  private sealed class RecordingGuard : IPerspectiveRowDestructionGuard {
    public Func<IReadOnlyList<PerspectiveRowDestructionTarget>, IReadOnlyDictionary<Guid, PerspectiveRowDecision>>? Decide { get; init; }
    public bool Throw { get; init; }
    public List<PerspectiveRowDestructionTarget> Offered { get; } = [];
    public List<PerspectiveRowDestructionTarget> AfterReap { get; } = [];

    public IReadOnlyCollection<Type> GuardedModels => [typeof(GuardedModel)];

    public ValueTask<IReadOnlyDictionary<Guid, PerspectiveRowDecision>> OnBeforeReapAsync(
        IReadOnlyList<PerspectiveRowDestructionTarget> targets, CancellationToken cancellationToken = default) {
      Offered.AddRange(targets);
      if (Throw) {
        throw new InvalidOperationException("blob provider outage");
      }
      return ValueTask.FromResult(Decide?.Invoke(targets) ?? new Dictionary<Guid, PerspectiveRowDecision>());
    }

    public ValueTask OnAfterReapAsync(
        IReadOnlyList<PerspectiveRowDestructionTarget> released, CancellationToken cancellationToken = default) {
      AfterReap.AddRange(released);
      return ValueTask.CompletedTask;
    }
  }

  private static PerspectiveRowDestructionTarget _target(Guid rowId, string reason = "ttl") {
    using var doc = JsonDocument.Parse("""{"blobName":"b"}""");
    return new PerspectiveRowDestructionTarget(
      typeof(GuardedModel).FullName!, "wh_per_guarded", rowId, null, doc.RootElement.Clone(), reason);
  }

  private static MaintenanceWorker _buildWorker(
      RowSweepCoordinator coordinator, RecordingGuard? guard, MaintenanceWorkerOptions? options = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    if (guard is not null) {
      services.AddSingleton<IPerspectiveRowDestructionGuard>(guard);
    }
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(options ?? new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  [Test]
  public async Task Sweeps_RunEvenWithNoGuardRegistered_TheWiringThatWasMissingAsync() {
    var coordinator = new RowSweepCoordinator();

    await _buildWorker(coordinator, guard: null).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.TtlReapCalls).IsEqualTo(1)
      .Because("the sweep SQL shipped with no production caller — the maintenance cycle is that caller");
    await Assert.That(coordinator.CapReapCalls).IsEqualTo(1);
    await Assert.That(coordinator.CollectedFor).IsNull()
      .Because("no guard means no collect round-trip — unguarded perspectives keep the pure-SQL path");
  }

  [Test]
  public async Task CapSweep_IsFleetClaimed_LosersSkipAsync() {
    var coordinator = new RowSweepCoordinator { CapClaimResult = false };

    await _buildWorker(coordinator, guard: null).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.TtlReapCalls).IsEqualTo(1)
      .Because("the expiry ladder runs every cycle on every instance — it is cheap and batched");
    await Assert.That(coordinator.CapReapCalls).IsEqualTo(0)
      .Because("the ranking sweep is heavier, so only the watermark winner runs it");
  }

  [Test]
  public async Task Guard_ProceedReleasesHolds_AndOnAfterReapSeesTheReleasedSetAsync() {
    var row = Guid.NewGuid();
    var coordinator = new RowSweepCoordinator { Collectable = [_target(row)] };
    var guard = new RecordingGuard {
      Decide = targets => targets.ToDictionary(t => t.RowId, _ => PerspectiveRowDecision.Proceed())
    };

    await _buildWorker(coordinator, guard).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(guard.Offered).Count().IsEqualTo(1);
    await Assert.That(coordinator.Released).Contains(new PerspectiveRowRef("wh_per_guarded", row))
      .Because("Proceed after an earlier Defer must clear the hold so the sweep can take the row");
    await Assert.That(guard.AfterReap.Select(t => t.RowId)).Contains(row)
      .Because("OnAfterReap fires with the released set, after the sweeps ran");
  }

  [Test]
  public async Task Guard_AbsentDecision_DefersTheRow_FailSafeAsync() {
    var decided = Guid.NewGuid();
    var undecided = Guid.NewGuid();
    var coordinator = new RowSweepCoordinator { Collectable = [_target(decided), _target(undecided)] };
    var guard = new RecordingGuard {
      Decide = _ => new Dictionary<Guid, PerspectiveRowDecision> { [decided] = PerspectiveRowDecision.Proceed() }
    };

    await _buildWorker(coordinator, guard).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Held.Select(h => h.Row.RowId)).Contains(undecided)
      .Because("a row the guard was silent about is DEFERRED — the guard exists to prevent orphaned "
             + "resources, so silence must fail safe, never fail open");
    await Assert.That(coordinator.Released.Select(r => r.RowId)).DoesNotContain(undecided);
  }

  [Test]
  public async Task Guard_CancelHoldsForever_DeferHoldsUntilTheInstantAsync() {
    var cancelled = Guid.NewGuid();
    var deferred = Guid.NewGuid();
    var until = DateTimeOffset.UtcNow.AddHours(6);
    var coordinator = new RowSweepCoordinator { Collectable = [_target(cancelled), _target(deferred)] };
    var guard = new RecordingGuard {
      Decide = _ => new Dictionary<Guid, PerspectiveRowDecision> {
        [cancelled] = PerspectiveRowDecision.Cancel(),
        [deferred] = PerspectiveRowDecision.Defer(until),
      }
    };

    await _buildWorker(coordinator, guard).RunMaintenanceOnceAsync(CancellationToken.None);

    var cancelHold = coordinator.Held.Single(h => h.Row.RowId == cancelled);
    await Assert.That(cancelHold.Until).IsEqualTo(DateTimeOffset.MaxValue)
      .Because("Cancel keeps the row indefinitely — the explicit, observable leak-risk decision");
    var deferHold = coordinator.Held.Single(h => h.Row.RowId == deferred);
    await Assert.That(deferHold.Until).IsEqualTo(until);
  }

  [Test]
  public async Task Guard_Throw_RecordsTheFailureLadder_AndSweepsStillRunAsync() {
    var row = Guid.NewGuid();
    var coordinator = new RowSweepCoordinator { Collectable = [_target(row)] };
    var guard = new RecordingGuard { Throw = true };

    await _buildWorker(coordinator, guard).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.FailureRecorded).Contains(new PerspectiveRowRef("wh_per_guarded", row))
      .Because("a throwing guard gets the destruction retry ladder — bounded retries, then policy");
    await Assert.That(coordinator.TtlReapCalls).IsEqualTo(1)
      .Because("one guard's failure must not stall the whole retention step; the held rows are safe");
    await Assert.That(guard.AfterReap).Count().IsEqualTo(0)
      .Because("nothing was released, so PostDestruction has nothing to report");
  }

  [Test]
  public async Task SettledFold_RunsBehindTheFleetClaim_WithTheConfiguredWindowAsync() {
    var coordinator = new RowSweepCoordinator();
    var options = new MaintenanceWorkerOptions { IntervalMinutes = 1, SettledFoldIdleDays = 30, SettledFoldBatchSize = 250 };

    await _buildWorker(coordinator, guard: null, options).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.SettledFoldCalls).IsEqualTo(1)
      .Because("idle streams only fold when something CALLS the fold — the maintenance cycle is "
             + "that caller, so a stream that never closes still gets its shape counted");
    await Assert.That(coordinator.SettledFoldIdle).IsEqualTo(TimeSpan.FromDays(30));
    await Assert.That(coordinator.SettledFoldLimit).IsEqualTo(250)
      .Because("the fold is bounded per sweep — leftovers fold on later cycles, never one giant scan");
  }

  [Test]
  public async Task SettledFold_ClaimLoser_SkipsAsync() {
    var coordinator = new RowSweepCoordinator { SettledFoldClaimResult = false };

    await _buildWorker(coordinator, guard: null).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.SettledFoldCalls).IsEqualTo(0)
      .Because("the settled fold is fleet-claimed like the cap sweep — one instance per window, "
             + "not every pod scanning the same idle streams");
  }

  [Test]
  public async Task CapSweep_ReceivesTheBatchBound_NotAnUnboundedScanAsync() {
    var coordinator = new RowSweepCoordinator();
    var options = new MaintenanceWorkerOptions { IntervalMinutes = 1, RowReapBatchSize = 123 };

    await _buildWorker(coordinator, guard: null, options).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.LastCapBatchSize).IsEqualTo(123)
      .Because("the cap sweep used to rank and evict EVERYTHING over the cap in one statement; the "
             + "worker now hands it the same batch bound the expiry sweep gets");
  }
}
