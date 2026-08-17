using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The coordinator-backed ledger's BATCH paths: one round trip when the engine supports it, and a
/// lossless fallback to the single-key calls when it does not (or fails). The fallback is not a
/// nicety — the single-key functions carry the per-operation fail-open/fail-closed semantics
/// (report degrades open, repair degrades closed), and a batch failure must land on exactly those
/// semantics rather than inventing new ones.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public class CoordinatorIntegrityRepairLedgerBatchTests {

  private sealed class _batchCoordinator : IWorkCoordinator {
    public IReadOnlyList<bool>? BatchAnswer { get; set; }
    public bool BatchHealedHandled { get; set; } = true;
    public bool ThrowOnBatch { get; set; }
    public int BatchCalls;
    public int SingleReportCalls;
    public int SingleRepairCalls;
    public int SingleHealedCalls;
    public int LastMaxGrants = -1;

    public Task<IReadOnlyList<bool>?> IntegrityTryBeginReportBatchAsync(
        Guid origin, IReadOnlyList<IntegrityReportObservation> observations,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken ct = default) {
      BatchCalls++;
      return ThrowOnBatch
        ? Task.FromException<IReadOnlyList<bool>?>(new InvalidOperationException("batch down"))
        : Task.FromResult(BatchAnswer);
    }

    public Task<IReadOnlyList<bool>?> IntegrityTryBeginRepairBatchAsync(
        Guid origin, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts, int maxGrants, CancellationToken ct = default) {
      BatchCalls++;
      LastMaxGrants = maxGrants;
      return ThrowOnBatch
        ? Task.FromException<IReadOnlyList<bool>?>(new InvalidOperationException("batch down"))
        : Task.FromResult(BatchAnswer);
    }

    public Task<bool> IntegrityMarkHealedBatchAsync(
        Guid origin, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, CancellationToken ct = default) {
      BatchCalls++;
      return ThrowOnBatch
        ? Task.FromException<bool>(new InvalidOperationException("batch down"))
        : Task.FromResult(BatchHealedHandled);
    }

    // The ledger heals through the WithAges surface; "handled" answers a (possibly empty) age
    // list and "not handled" answers null — the same fallback contract the bool method modeled.
    public Task<System.Collections.Generic.IReadOnlyList<double>?> IntegrityMarkHealedBatchWithAgesAsync(
        Guid originServiceId, System.Collections.Generic.IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        CancellationToken cancellationToken = default) {
      BatchCalls++;
      return ThrowOnBatch
        ? Task.FromException<System.Collections.Generic.IReadOnlyList<double>?>(new InvalidOperationException("batch heal exploded"))
        : Task.FromResult<System.Collections.Generic.IReadOnlyList<double>?>(BatchHealedHandled ? [] : null);
    }

    public Task<bool> IntegrityTryBeginReportAsync(
        IntegrityRepairLedger.DivergenceKey key, long ol, long oh, long ll, long lh,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken ct = default) {
      SingleReportCalls++;
      return Task.FromResult(true);
    }

    public Task<bool> IntegrityTryBeginRepairAsync(
        IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
        CancellationToken ct = default) {
      SingleRepairCalls++;
      return Task.FromResult(true);
    }

    public Task IntegrityMarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken ct = default) {
      SingleHealedCalls++;
      return Task.CompletedTask;
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static (CoordinatorIntegrityRepairLedger Ledger, _batchCoordinator Coordinator) _build() {
    var coordinator = new _batchCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    var sp = services.BuildServiceProvider();
    return (new CoordinatorIntegrityRepairLedger(sp.GetRequiredService<IServiceScopeFactory>()), coordinator);
  }

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin) =>
    new(origin, "tenant-a", "Contracts.TypeX", TrackedGuid.NewMedo().Value);

  [Test]
  public async Task ReportBatch_UsesTheCoordinatorBatch_WhenSupportedAsync() {
    var (ledger, coordinator) = _build();
    var origin = TrackedGuid.NewMedo().Value;
    coordinator.BatchAnswer = [true, false];

    var flags = await ledger.TryBeginReportBatchAsync(
      [new(_key(origin), 1, 2, 0, 0), new(_key(origin), 3, 4, 0, 0)],
      DateTimeOffset.UtcNow, TimeSpan.FromMinutes(60));

    await Assert.That(flags.ToList()).IsEquivalentTo([true, false]);
    await Assert.That(coordinator.BatchCalls).IsEqualTo(1);
    await Assert.That(coordinator.SingleReportCalls).IsEqualTo(0)
      .Because("one round trip is the point — the per-key consult is the throughput killer this replaces");
  }

  [Test]
  public async Task ReportBatch_UnsupportedEngine_FallsBackToTheSinglesAsync() {
    var (ledger, coordinator) = _build();
    var origin = TrackedGuid.NewMedo().Value;
    coordinator.BatchAnswer = null;   // the DIM default: engine cannot batch

    var flags = await ledger.TryBeginReportBatchAsync(
      [new(_key(origin), 1, 2, 0, 0), new(_key(origin), 3, 4, 0, 0)],
      DateTimeOffset.UtcNow, TimeSpan.FromMinutes(60));

    await Assert.That(flags.Count).IsEqualTo(2);
    await Assert.That(coordinator.SingleReportCalls).IsEqualTo(2)
      .Because("the single-key path carries the authoritative fail-open semantics — unsupported batches land there");
  }

  [Test]
  public async Task ReportBatch_BatchFailure_FallsBackToTheSinglesAsync() {
    var (ledger, coordinator) = _build();
    var origin = TrackedGuid.NewMedo().Value;
    coordinator.ThrowOnBatch = true;

    var flags = await ledger.TryBeginReportBatchAsync(
      [new(_key(origin), 1, 2, 0, 0)], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(60));

    await Assert.That(flags.Count).IsEqualTo(1);
    await Assert.That(coordinator.SingleReportCalls).IsEqualTo(1)
      .Because("a broken batch degrades to N singles, never to silence");
  }

  [Test]
  public async Task RepairBatch_PassesTheGrantCapThrough_AndFallsBackCappedAsync() {
    var (ledger, coordinator) = _build();
    var origin = TrackedGuid.NewMedo().Value;
    coordinator.BatchAnswer = [true, true, false];

    _ = await ledger.TryBeginRepairBatchAsync(
      [_key(origin), _key(origin), _key(origin)], DateTimeOffset.UtcNow,
      TimeSpan.FromSeconds(300), maxAttempts: 8, maxGrants: 2);
    await Assert.That(coordinator.LastMaxGrants).IsEqualTo(2)
      .Because("the cap is enforced inside the batch so no attempt budget is burned past it");

    coordinator.BatchAnswer = null;   // unsupported → capped single-key fallback
    var flags = await ledger.TryBeginRepairBatchAsync(
      [_key(origin), _key(origin), _key(origin)], DateTimeOffset.UtcNow,
      TimeSpan.FromSeconds(300), maxAttempts: 8, maxGrants: 2);
    await Assert.That(flags.Count(granted => granted)).IsEqualTo(2)
      .Because("the fallback honors the same cap — and stops consulting once it is spent");
    await Assert.That(coordinator.SingleRepairCalls).IsEqualTo(2);
  }

  [Test]
  public async Task RepairBatch_ZeroGrantBudget_ConsultsNothingAsync() {
    var (ledger, coordinator) = _build();
    var origin = TrackedGuid.NewMedo().Value;

    var flags = await ledger.TryBeginRepairBatchAsync(
      [_key(origin)], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(300), maxAttempts: 8, maxGrants: 0);

    await Assert.That(flags.All(granted => !granted)).IsTrue();
    await Assert.That(coordinator.BatchCalls).IsEqualTo(0);
    await Assert.That(coordinator.SingleRepairCalls).IsEqualTo(0)
      .Because("no budget means no consult — every consult that grants records an attempt");
  }

  [Test]
  public async Task HealedBatch_UsesTheBatch_AndFallsBackWhenUnhandledAsync() {
    var (ledger, coordinator) = _build();
    var origin = TrackedGuid.NewMedo().Value;

    await ledger.MarkHealedBatchAsync([_key(origin), _key(origin)]);
    await Assert.That(coordinator.BatchCalls).IsEqualTo(1);
    await Assert.That(coordinator.SingleHealedCalls).IsEqualTo(0);

    coordinator.BatchHealedHandled = false;   // engine says "not handled"
    await ledger.MarkHealedBatchAsync([_key(origin), _key(origin)]);
    await Assert.That(coordinator.SingleHealedCalls).IsEqualTo(2)
      .Because("an unhandled batch falls through to per-key heals — a forgotten heal only re-offers later, but only if it actually ran");
  }

  [Test]
  public async Task EmptyBatches_AreNoOpsAsync() {
    var (ledger, coordinator) = _build();

    var reports = await ledger.TryBeginReportBatchAsync([], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(60));
    var repairs = await ledger.TryBeginRepairBatchAsync([], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(300), 8, 5);
    await ledger.MarkHealedBatchAsync([]);

    await Assert.That(reports).IsEmpty();
    await Assert.That(repairs).IsEmpty();
    await Assert.That(coordinator.BatchCalls).IsEqualTo(0);
  }

  [Test]
  public async Task InMemoryLedger_BatchDefaults_LoopTheSingles_WithTheGrantCapAsync() {
    // The interface DEFAULTS: any implementation that only knows the singles still gets correct
    // batch behavior — including the stop-consulting-past-the-cap rule.
    var origin = TrackedGuid.NewMedo().Value;
    IIntegrityRepairLedger ledger = new IntegrityRepairLedger();
    var keys = Enumerable.Range(0, 4).Select(_ => _key(origin)).ToList();
    var now = DateTimeOffset.UtcNow;

    var reports = await ledger.TryBeginReportBatchAsync(
      [.. keys.Select(k => new IntegrityReportObservation(k, 1, 2, 0, 0))], now, TimeSpan.FromMinutes(60));
    await Assert.That(reports.All(granted => granted)).IsTrue()
      .Because("first sighting reports — the default loops the proven single");

    var repairs = await ledger.TryBeginRepairBatchAsync(keys, now, TimeSpan.FromSeconds(300), 8, maxGrants: 2);
    await Assert.That(repairs.Count(granted => granted)).IsEqualTo(2)
      .Because("the default enforces the same in-order cap the batch functions do");

    await ledger.MarkHealedBatchAsync(keys);
    var again = await ledger.TryBeginReportBatchAsync(
      [.. keys.Select(k => new IntegrityReportObservation(k, 1, 2, 0, 0))], now.AddMinutes(1), TimeSpan.FromMinutes(60));
    await Assert.That(again.All(granted => granted)).IsTrue()
      .Because("healed buckets are forgotten — a later sighting is a fresh incident");
  }
}
