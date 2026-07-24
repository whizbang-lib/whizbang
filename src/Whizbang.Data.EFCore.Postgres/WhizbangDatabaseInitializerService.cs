using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Hosted service that initializes the Whizbang database schema, then signals
/// <see cref="ISchemaReadyGate"/> so workers (which await the gate at the top of their ExecuteAsync)
/// can issue SQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Blocking (default, <see cref="SchemaInitializationOptions.NonBlockingSchemaInit"/> = false):</b>
/// migrations run inline in <c>StartAsync</c>, so the host does not finish starting (no HTTP port,
/// no workers) until they complete, and a failure throws — aborting startup and keeping the system
/// in a safe halted state. The long-standing behavior; unchanged for every existing consumer.
/// </para>
/// <para>
/// <b>Non-blocking (opt-in):</b> initialization runs in the background; <c>StartAsync</c> returns
/// immediately so the host binds and can answer a liveness probe, while the gate stays closed until
/// migrations succeed. Workers — and a readiness check that reads the gate — hold off until then, so
/// nothing touches an unmigrated schema. On failure (including a
/// <see cref="SchemaInitializationOptions.MigrationTimeout"/>) the gate never opens (fail-closed):
/// the pod is alive but never ready, so it stays out of traffic rotation and the rollout fails
/// cleanly rather than being killed mid-migration by a startup probe.
/// </para>
/// <para>
/// Partition recompute is best-effort: a failure does NOT block <see cref="ISchemaReadyGate.MarkReady"/>.
/// It self-heals partition_number columns left inconsistent by a previous PartitionCount (e.g.,
/// crossing a partition-count boundary on redeploy).
/// </para>
/// </remarks>
/// <docs>data/turnkey-initialization</docs>
internal sealed partial class WhizbangDatabaseInitializerService(
    IServiceProvider serviceProvider,
    ISchemaInitializationRunner initRunner,
    ISchemaReadyGate schemaReadyGate,
    IOptions<ClaimWorkerOptions> claimWorkerOptions,
    IOptions<SchemaInitializationOptions> schemaInitOptions,
    TimeProvider timeProvider,
    ILogger<WhizbangDatabaseInitializerService> logger) : IHostedService {

  private readonly IServiceProvider _serviceProvider = serviceProvider;
  private readonly ISchemaInitializationRunner _initRunner = initRunner;
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate;
  private readonly IOptions<ClaimWorkerOptions> _claimWorkerOptions = claimWorkerOptions;
  private readonly IOptions<SchemaInitializationOptions> _schemaInitOptions = schemaInitOptions;
  private readonly TimeProvider _timeProvider = timeProvider;
  private readonly ILogger<WhizbangDatabaseInitializerService> _logger = logger;

  private Task? _backgroundInit;

  /// <summary>
  /// The background initialization task when <see cref="SchemaInitializationOptions.NonBlockingSchemaInit"/>
  /// is enabled; <see langword="null"/> otherwise. Test seam: await this to deterministically observe
  /// background init completion (success or fail-closed) without polling.
  /// </summary>
  internal Task? BackgroundInitTask => _backgroundInit;

  public Task StartAsync(CancellationToken cancellationToken) {
    if (_schemaInitOptions.Value.NonBlockingSchemaInit) {
      // Non-blocking: return now so the host binds and answers liveness; migrate in the background.
      // CancellationToken.None (not the startup token, which completes once StartAsync returns) —
      // MigrationTimeout is the migration's ceiling instead.
      _backgroundInit = Task.Run(_runInitializationLoggedAsync, CancellationToken.None);
      return Task.CompletedTask;
    }

    // Blocking (default): the host waits for init; a failure throws and aborts startup.
    return _runInitializationAsync(cancellationToken);
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  private async Task _runInitializationLoggedAsync() {
    try {
      await _runInitializationAsync(CancellationToken.None);
    } catch (Exception ex) {
      // Fail-closed: MarkReady was never reached, so the gate stays closed and no worker or readiness
      // check ever sees a ready schema. The host stays alive (liveness green) but never becomes ready.
      LogBackgroundInitializationFailed(_logger, ex);
    }
  }

  private async Task _runInitializationAsync(CancellationToken cancellationToken) {
    await _runMigrationsAsync(cancellationToken);

    // Best-effort: recompute partition_number columns that may have drifted across a PartitionCount
    // change. NEVER blocks MarkReady — workers can run on a stale partition map (next claim cycle
    // picks them up correctly via the live PartitionCount).
    await TryRecomputePartitionsAsync(cancellationToken);

    _schemaReadyGate.MarkReady();
  }

  private async Task _runMigrationsAsync(CancellationToken cancellationToken) {
    if (_schemaInitOptions.Value.MigrationTimeout is not { } ceiling) {
      await _initRunner.RunAsync(cancellationToken);
      return;
    }

    // TimeProvider-driven so the timeout is deterministically testable (FakeTimeProvider.Advance).
    using var timeoutCts = new CancellationTokenSource(ceiling, _timeProvider);
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
    try {
      await _initRunner.RunAsync(linkedCts.Token);
    } catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
      // The migration blew past its configured ceiling (not host shutdown). Surface it as a failure
      // so the gate never opens — the pod stays out of rotation and the rollout fails cleanly.
      throw new TimeoutException($"Schema initialization exceeded the configured MigrationTimeout of {ceiling}.");
    }
  }

  // internal (not private) so tests can exercise the best-effort/cancellation contract directly
  // without driving the static DbContextInitializationRegistry that initialization runs first.
  internal async Task TryRecomputePartitionsAsync(CancellationToken cancellationToken) {
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
    } catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) {
      // Query-level cancellation (e.g. a command timeout under startup load) — NOT host shutdown.
      // Recompute is best-effort and self-heals on the next claim cycle, so swallowing it honors the
      // documented "never blocks MarkReady" contract instead of letting the OCE escape and abort. A
      // genuine host-shutdown cancellation (the token IS cancelled) still propagates.
      LogPartitionRecomputeFailed(_logger, ex);
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

  [LoggerMessage(EventId = 3, Level = LogLevel.Error,
    Message = "Background schema initialization failed; the schema-ready gate remains closed and the host will not become ready")]
  static partial void LogBackgroundInitializationFailed(ILogger logger, Exception ex);
}
