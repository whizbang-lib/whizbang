using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the delegation and — more importantly — the FAILURE DIRECTIONS of the durable ledger.
///
/// <para>
/// The two directions are deliberately opposite, and getting either backwards is silent. Report
/// fails OPEN: over-reporting is recoverable, whereas suppressing a real integrity report because
/// a bookkeeping table was unavailable hides genuine data loss. Repair fails CLOSED: an unbounded
/// repair request issued against real data because the ledger could not be reached is not
/// recoverable.
/// </para>
///
/// <para>
/// This distinction is not theoretical. A parameter-binding bug made every ledger call throw, and
/// the consequence was exactly these defaults taking over — reporting unbounded as before, and
/// repair silently disabled fleet-wide. The code looked correct and every unit test passed. So the
/// defaults themselves need to be asserted, not just the happy path.
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[Category("Shard1")]
public class CoordinatorIntegrityRepairLedgerTests {

  private static IntegrityRepairLedger.DivergenceKey _key() =>
    new(Guid.Parse("aaaaaaaa-9999-9999-9999-999999999999"), "tenant-x", "T, A",
        Guid.Parse("bbbbbbbb-9999-9999-9999-999999999999"));

  /// <summary>A scope factory whose provider resolves nothing — the "no coordinator" case.</summary>
  private sealed class EmptyScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider {
    public IServiceScope CreateScope() => this;
    public IServiceProvider ServiceProvider => this;
    public object? GetService(Type serviceType) => null;
    public void Dispose() { }
  }

  private sealed class RecordingCoordinator : IWorkCoordinator {
    public int ReportCalls { get; private set; }
    public int RepairCalls { get; private set; }
    public int HealedCalls { get; private set; }
    public bool ReportResult { get; init; }
    public bool RepairResult { get; init; }

    public Task<bool> IntegrityTryBeginReportAsync(
        IntegrityRepairLedger.DivergenceKey key, long a, long b, long c, long d,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken ct = default) {
      ReportCalls++;
      return Task.FromResult(ReportResult);
    }

    public Task<bool> IntegrityTryBeginRepairAsync(
        IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan backoff, int maxAttempts,
        CancellationToken ct = default) {
      RepairCalls++;
      return Task.FromResult(RepairResult);
    }

    public Task IntegrityMarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken ct = default) {
      HealedCalls++;
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

  private static IServiceScopeFactory _factoryWith(IWorkCoordinator coordinator) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
  }

  [Test]
  public async Task DelegatesEachCallToTheCoordinatorAsync() {
    var coord = new RecordingCoordinator { ReportResult = true, RepairResult = true };
    var ledger = new CoordinatorIntegrityRepairLedger(_factoryWith(coord));

    await Assert.That(await ledger.TryBeginReportAsync(_key(), 1, 2, 3, 4, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(60))).IsTrue();
    await Assert.That(await ledger.TryBeginRepairAsync(_key(), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(300), 8)).IsTrue();
    await ledger.MarkHealedAsync(_key());

    await Assert.That(coord.ReportCalls).IsEqualTo(1);
    await Assert.That(coord.RepairCalls).IsEqualTo(1);
    await Assert.That(coord.HealedCalls).IsEqualTo(1)
      .Because("all three must reach the durable store, or convergence state is silently per-process again");
  }

  [Test]
  public async Task WithoutACoordinator_ReportFailsOpenAndRepairFailsClosedAsync() {
    var ledger = new CoordinatorIntegrityRepairLedger(new EmptyScopeFactory());

    await Assert.That(await ledger.TryBeginReportAsync(_key(), 1, 2, 3, 4, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(60)))
      .IsTrue().Because("suppressing a real integrity report because bookkeeping was unavailable "
                        + "hides data loss; over-reporting is recoverable");

    await Assert.That(await ledger.TryBeginRepairAsync(_key(), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(300), 8))
      .IsFalse().Because("an unbounded repair issued against real data because the ledger could not "
                         + "be reached is NOT recoverable");
  }

  [Test]
  public async Task ReturnsTheCoordinatorsAnswer_NotAnOptimisticDefaultAsync() {
    // The failure that motivated this: broken calls fell through to the defaults, so report looked
    // permanently "yes" and repair permanently "no" while appearing to work.
    var coord = new RecordingCoordinator { ReportResult = false, RepairResult = true };
    var ledger = new CoordinatorIntegrityRepairLedger(_factoryWith(coord));

    await Assert.That(await ledger.TryBeginReportAsync(_key(), 1, 2, 3, 4, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(60)))
      .IsFalse().Because("a suppressed report must actually suppress — always-true is the broken-call signature");
    await Assert.That(await ledger.TryBeginRepairAsync(_key(), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(300), 8))
      .IsTrue().Because("an allowed repair must actually proceed — always-false is the broken-call signature");
  }

  /// <summary>A coordinator whose ledger calls all throw — the shape a broken query takes.</summary>
  private sealed class ThrowingCoordinator : IWorkCoordinator {
    public Task<bool> IntegrityTryBeginReportAsync(
        IntegrityRepairLedger.DivergenceKey key, long a, long b, long c, long d,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken ct = default) =>
      throw new InvalidOperationException("ledger unavailable");

    public Task<bool> IntegrityTryBeginRepairAsync(
        IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan backoff, int maxAttempts,
        CancellationToken ct = default) =>
      throw new InvalidOperationException("ledger unavailable");

    public Task IntegrityMarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken ct = default) =>
      throw new InvalidOperationException("ledger unavailable");

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  [Test]
  public async Task WhenTheLedgerThrows_TheAuditKeepsRunningWithTheSafeDefaultsAsync() {
    // The exact path the parameter-binding bug took. Every call threw, and because the failure was
    // swallowed the system looked healthy while reporting stayed unbounded and repair was off
    // everywhere. An exception must not escape into the audit — but it must also not be invisible,
    // which is why the implementation logs before returning these defaults.
    var ledger = new CoordinatorIntegrityRepairLedger(_factoryWith(new ThrowingCoordinator()));

    await Assert.That(await ledger.TryBeginReportAsync(_key(), 1, 2, 3, 4, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(60)))
      .IsTrue().Because("a broken ledger must not silence a real integrity report");

    await Assert.That(await ledger.TryBeginRepairAsync(_key(), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(300), 8))
      .IsFalse().Because("a broken ledger must never license an unbounded repair against real data");

    // MarkHealed has nothing to fall back to; it must simply not take the audit down with it.
    await ledger.MarkHealedAsync(_key());
  }

  // --- Cancellation is not a ledger failure ----------------------------------
  // Every entry point wraps its coordinator call in a catch that degrades to a safe
  // default. Cancellation must NOT take that path: a shutdown mid-audit would otherwise
  // be recorded as "the ledger is broken", licensing the fallback — which for the repair
  // side means declining every repair, and for the report side means allowing every one.
  // Both are wrong answers to a question nobody asked.

  private sealed class CancellingCoordinator : IWorkCoordinator {
    public Task<bool> IntegrityTryBeginReportAsync(
        IntegrityRepairLedger.DivergenceKey key, long a, long b, long c, long d,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken ct = default) =>
      throw new OperationCanceledException();

    public Task<bool> IntegrityTryBeginRepairAsync(
        IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan backoff, int maxAttempts,
        CancellationToken ct = default) =>
      throw new OperationCanceledException();

    public Task IntegrityMarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken ct = default) =>
      throw new OperationCanceledException();

    public Task<IReadOnlyList<bool>?> IntegrityTryBeginReportBatchAsync(
        Guid originServiceId, IReadOnlyList<IntegrityReportObservation> observations,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken ct = default) =>
      throw new OperationCanceledException();

    public Task<IReadOnlyList<bool>?> IntegrityTryBeginRepairBatchAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts, int maxGrants,
        CancellationToken ct = default) =>
      throw new OperationCanceledException();

    public Task<IReadOnlyList<double>?> IntegrityMarkHealedBatchWithAgesAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        CancellationToken ct = default) =>
      throw new OperationCanceledException();

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static IntegrityReportObservation _observation() => new(_key(), 1, 2, 3, 4);

  [Test]
  public async Task TryBeginReportAsync_WhenCancelled_PropagatesAsync() {
    var ledger = new CoordinatorIntegrityRepairLedger(_factoryWith(new CancellingCoordinator()));

    await Assert.That(async () => await ledger.TryBeginReportAsync(
        _key(), 1, 2, 3, 4, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(60)))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task TryBeginRepairAsync_WhenCancelled_PropagatesAsync() {
    var ledger = new CoordinatorIntegrityRepairLedger(_factoryWith(new CancellingCoordinator()));

    await Assert.That(async () => await ledger.TryBeginRepairAsync(
        _key(), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(300), 8))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task MarkHealedAsync_WhenCancelled_PropagatesAsync() {
    var ledger = new CoordinatorIntegrityRepairLedger(_factoryWith(new CancellingCoordinator()));

    await Assert.That(async () => await ledger.MarkHealedAsync(_key()))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task TryBeginReportBatchAsync_WhenCancelled_PropagatesAsync() {
    var ledger = new CoordinatorIntegrityRepairLedger(_factoryWith(new CancellingCoordinator()));

    await Assert.That(async () => await ledger.TryBeginReportBatchAsync(
        [_observation()], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(60)))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task TryBeginRepairBatchAsync_WhenCancelled_PropagatesAsync() {
    var ledger = new CoordinatorIntegrityRepairLedger(_factoryWith(new CancellingCoordinator()));

    await Assert.That(async () => await ledger.TryBeginRepairBatchAsync(
        [_key()], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(300), maxAttempts: 8, maxGrants: 4))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task MarkHealedBatchWithAgesAsync_WhenCancelled_PropagatesAsync() {
    var ledger = new CoordinatorIntegrityRepairLedger(_factoryWith(new CancellingCoordinator()));

    await Assert.That(async () => await ledger.MarkHealedBatchWithAgesAsync([_key()]))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task MarkHealedBatchWithAgesAsync_WithNoKeys_ReturnsEmptyAsync() {
    var ledger = new CoordinatorIntegrityRepairLedger(new EmptyScopeFactory());

    await Assert.That(await ledger.MarkHealedBatchWithAgesAsync([])).IsEmpty();
  }

  // --- Batch failure degrades to the per-key path ----------------------------
  // A batch call that fails must not fail the caller: the ledger logs, then falls back to
  // the single-key loop. The existing ThrowingCoordinator only throws on the single-key
  // methods — its batch members inherit the interface defaults and return null, which is
  // the "not supported" path, not the "failed" one.

  private sealed class BatchThrowingCoordinator : IWorkCoordinator {
    public int SingleRepairCalls { get; private set; }
    public int SingleHealedCalls { get; private set; }

    public Task<IReadOnlyList<bool>?> IntegrityTryBeginRepairBatchAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts, int maxGrants,
        CancellationToken ct = default) =>
      throw new InvalidOperationException("batch ledger unavailable");

    public Task<IReadOnlyList<double>?> IntegrityMarkHealedBatchWithAgesAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        CancellationToken ct = default) =>
      throw new InvalidOperationException("batch ledger unavailable");

    public Task<bool> IntegrityTryBeginRepairAsync(
        IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan backoff, int maxAttempts,
        CancellationToken ct = default) {
      SingleRepairCalls++;
      return Task.FromResult(true);
    }

    public Task IntegrityMarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken ct = default) {
      SingleHealedCalls++;
      return Task.CompletedTask;
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  [Test]
  public async Task TryBeginRepairBatchAsync_WhenTheBatchCallFails_FallsBackToPerKeyAsync() {
    var coord = new BatchThrowingCoordinator();
    var ledger = new CoordinatorIntegrityRepairLedger(_factoryWith(coord));

    var results = await ledger.TryBeginRepairBatchAsync(
      [_key(), _key()], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(300), maxAttempts: 8, maxGrants: 4);

    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(coord.SingleRepairCalls).IsGreaterThan(0)
      .Because("a failed batch degrades to the single-key loop rather than failing the caller");
  }

  [Test]
  public async Task MarkHealedBatchWithAgesAsync_WhenTheBatchCallFails_FallsBackToPerKeyAsync() {
    var coord = new BatchThrowingCoordinator();
    var ledger = new CoordinatorIntegrityRepairLedger(_factoryWith(coord));

    var ages = await ledger.MarkHealedBatchWithAgesAsync([_key(), _key()]);

    await Assert.That(ages).IsEmpty()
      .Because("the per-key fallback heals without measuring ages, so it reports none");
    await Assert.That(coord.SingleHealedCalls).IsEqualTo(2);
  }
}
