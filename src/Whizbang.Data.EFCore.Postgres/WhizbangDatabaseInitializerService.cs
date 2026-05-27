using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Hosted service that initializes the Whizbang database schema before workers issue SQL.
/// Registered as a plain IHostedService (not BackgroundService) so StartAsync blocks
/// until initialization completes. After migrations succeed (and after a best-effort
/// partition recompute), signals <see cref="ISchemaReadyGate"/> so workers (which await
/// the gate at the top of their ExecuteAsync) can proceed.
/// </summary>
/// <remarks>
/// <para>
/// On migration failure, the gate is NOT marked ready — StartAsync throws, the host aborts,
/// and workers never enter their main loop. This keeps the system in a safe halted state
/// instead of running on a broken schema.
/// </para>
/// <para>
/// Partition recompute is best-effort: a failure does NOT block <see cref="ISchemaReadyGate.MarkReady"/>.
/// Recompute self-heals partition_number columns left inconsistent by a previous PartitionCount
/// (e.g., crossing a partition-count boundary on redeploy). Mirrors the legacy publisher's
/// <c>_recomputePartitionsOnStartupAsync</c> which ran on every worker startup.
/// </para>
/// </remarks>
/// <docs>data/turnkey-initialization</docs>
internal sealed partial class WhizbangDatabaseInitializerService(
    IServiceProvider serviceProvider,
    ISchemaReadyGate schemaReadyGate,
    IOptions<ClaimWorkerOptions> claimWorkerOptions,
    ILogger<WhizbangDatabaseInitializerService> logger) : IHostedService {

  private readonly IServiceProvider _serviceProvider = serviceProvider;
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate;
  private readonly IOptions<ClaimWorkerOptions> _claimWorkerOptions = claimWorkerOptions;
  private readonly ILogger<WhizbangDatabaseInitializerService> _logger = logger;

  public async Task StartAsync(CancellationToken cancellationToken) {
    await DbContextInitializationRegistry.InitializeAllAsync(
        _serviceProvider, _logger, cancellationToken);

    // Best-effort: recompute partition_number columns that may have drifted across a
    // PartitionCount change. NEVER blocks MarkReady — workers can run on a stale partition
    // map (next claim cycle picks them up correctly via the live PartitionCount).
    await _tryRecomputePartitionsAsync(cancellationToken);

    _schemaReadyGate.MarkReady();
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  private async Task _tryRecomputePartitionsAsync(CancellationToken cancellationToken) {
    try {
      await using var scope = _serviceProvider.CreateAsyncScope();
      var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
      if (coordinator is null) {
        // No coordinator registered (e.g., test fixture without a driver) — nothing to do.
        return;
      }
      var partitionCount = _claimWorkerOptions.Value.PartitionCount;
      var result = await coordinator.RecomputePartitionNumbersAsync(partitionCount, cancellationToken);
      if (result.AnyRecomputed) {
        LogPartitionRecompute(_logger, partitionCount,
          result.InboxRowsRecomputed, result.OutboxRowsRecomputed, result.ActiveStreamsRowsRecomputed);
      }
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogPartitionRecomputeFailed(_logger, ex);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Partition recompute on startup: PartitionCount={PartitionCount}, inbox={Inbox}, outbox={Outbox}, activeStreams={ActiveStreams}")]
  static partial void LogPartitionRecompute(
    ILogger logger, int partitionCount, long inbox, long outbox, long activeStreams);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Partition recompute on startup failed; continuing without it (non-fatal)")]
  static partial void LogPartitionRecomputeFailed(ILogger logger, Exception ex);
}
