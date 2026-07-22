using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests for <see cref="WhizbangDatabaseInitializerService"/>'s best-effort partition-recompute
/// cancellation contract. A query-level <see cref="OperationCanceledException"/> (e.g. a command
/// timeout while the shared database is saturated during a slow first-boot migration) must never
/// escape <c>StartAsync</c> and crash the host — the recompute self-heals on the next claim cycle.
/// A genuine host-shutdown cancellation must still propagate.
/// </summary>
public class WhizbangDatabaseInitializerServiceTests {

  [Test]
  public async Task TryRecompute_QueryCancellation_NonShutdownToken_IsSwallowedAsync() {
    // RED before the fix: the catch filter excluded OperationCanceledException, so a query-level
    // cancellation (the host is NOT shutting down) escaped and aborted StartAsync.
    var service = _create(new _ThrowingCoordinator(new OperationCanceledException()));

    // Must NOT throw: a plain OCE with a live (uncancelled) token is a query cancellation.
    await service.TryRecomputePartitionsAsync(CancellationToken.None);
  }

  [Test]
  public async Task TryRecompute_HostShutdown_CancelledToken_PropagatesAsync() {
    // The other half of the contract: when the host IS shutting down (token cancelled), the
    // cancellation still propagates so startup halts cleanly rather than silently continuing.
    var service = _create(new _ThrowingCoordinator(new OperationCanceledException()));
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.That(async () => await service.TryRecomputePartitionsAsync(cts.Token))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task TryRecompute_NonCancellationFailure_IsSwallowedAsync() {
    // Locks the pre-existing best-effort behavior: a non-cancellation recompute failure stays
    // swallowed (never blocks MarkReady) — the fix must not regress this.
    var service = _create(new _ThrowingCoordinator(new InvalidOperationException("boom")));

    await service.TryRecomputePartitionsAsync(CancellationToken.None);
  }

  private static WhizbangDatabaseInitializerService _create(IWorkCoordinator coordinator) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    var provider = services.BuildServiceProvider();
    return new WhizbangDatabaseInitializerService(
      provider,
      new SchemaReadyGate(),
      Options.Create(new ClaimWorkerOptions()),
      NullLogger<WhizbangDatabaseInitializerService>.Instance);
  }

  /// <summary>Fake coordinator whose partition recompute throws a supplied exception; every other
  /// member relies on the interface's default no-op implementations.</summary>
  private sealed class _ThrowingCoordinator(Exception toThrow) : IWorkCoordinator {
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(
        int partitionCount, CancellationToken cancellationToken = default)
      => throw toThrow;

    // Non-defaulted interface members:
    public Task ReportPerspectiveCompletionAsync(
        PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(
        PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(
        InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
