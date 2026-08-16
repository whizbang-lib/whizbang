using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// The Postgres <see cref="IDutyElector"/>: a duty is a session advisory lock on a dedicated
/// direct connection (session-scoped, so a crash releases server-side with no timeout to tune),
/// with the holding recorded through <c>record_capability</c> — <b>the lock decides, the row
/// reports</b>. The eviction fence reaches acquisition: <c>record_capability</c> refuses a
/// tombstoned instance, and the elector releases the lock it just won and stands down.
/// </summary>
/// <remarks>
/// <para>
/// The lock rides its own connection, not the shared notify connection — a session lock would pin
/// the shared conn for the holder's entire tenure, which is incompatible with its role as the
/// per-pod multiplexer. Same reasoning, and same connection resolution, as the commit-order
/// stamper's leader lock: prefer the registered <see cref="INotificationDataSource"/>, fall back
/// to the resolver, and warn on a pooled fallback — <c>pg_try_advisory_lock</c> is session-scoped
/// and a transaction-pooling front-end will not preserve it across the tenure.
/// </para>
/// <para>
/// Failover needs no machinery here: a clean death releases the lock as the session ends, and the
/// next attempt wins. Re-attempt cadence is the caller's concern (blocked waiters, a poll, or an
/// <c>InstanceDiedSignal</c> prompt); the elector only ever answers "yours now" or "not yours".
/// </para>
/// </remarks>
/// <docs>proposals/startup-pipeline#capabilities</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DutyElectionE2ETests.cs</tests>
public sealed partial class PgDutyElector(
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  IServiceInstanceProvider instanceProvider,
  ILogger<PgDutyElector> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null,
  INotificationDataSource? notificationDataSource = null
) : IDutyElector {
  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly ILogger<PgDutyElector> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;
  private readonly INotificationDataSource? _notificationDataSource = notificationDataSource;

  /// <inheritdoc />
  public async Task<IDutyGrant?> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrEmpty(duty);

    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    var plan = NotificationConnectionPlan.Create(_notificationDataSource, resolution);
    if (!plan.IsAvailable) {
      LogNoConnection(_logger, duty);
      return null;
    }
    if (!plan.UsesDataSource && resolution.Source == NotificationConnectionStringResolver.ResolutionSource.PooledKeyFallback) {
      // Session locks do not survive transaction pooling; the duty would silently un-hold itself.
      LogPooledFallbackWarning(_logger, duty);
    }

    var key = DutyLockKey.Compute(resolution.SearchPath, duty);
    var connection = await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
    try {
      await using (var tryLock = connection.CreateCommand()) {
        tryLock.CommandText = "SELECT pg_try_advisory_lock(@key)";
        tryLock.Parameters.AddWithValue("key", key);
        var won = await tryLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (won is not true) {
          await connection.DisposeAsync().ConfigureAwait(false);
          return null;
        }
      }

      // The lock decided; now the row reports — unless the fence refuses. record_capability
      // returns false for a tombstoned (or unregistered) instance: release what was won and
      // stand down, because an evicted instance must not hold exclusive work.
      await using (var record = connection.CreateCommand()) {
        record.CommandText = "SELECT record_capability(@id, @duty)";
        record.Parameters.AddWithValue("id", _instanceProvider.InstanceId);
        record.Parameters.AddWithValue("duty", duty);
        var recorded = await record.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (recorded is not true) {
          await using (var unlock = connection.CreateCommand()) {
            unlock.CommandText = "SELECT pg_advisory_unlock(@key)";
            unlock.Parameters.AddWithValue("key", key);
            await unlock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
          }
          await connection.DisposeAsync().ConfigureAwait(false);
          LogRefused(_logger, duty, _instanceProvider.InstanceId);
          return null;
        }
      }

      LogAcquired(_logger, duty, _instanceProvider.InstanceId);
      return new PgDutyGrant(connection, key, duty, _instanceProvider.InstanceId, _logger);
    } catch {
      await connection.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }

  private sealed class PgDutyGrant(
      NpgsqlConnection connection, long key, string duty, Guid instanceId, ILogger logger) : IDutyGrant {
    private bool _lost;
    private bool _disposed;

    public string Duty { get; } = duty;
    public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UtcNow;

    public async Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) {
      if (_lost || _disposed) {
        return false;
      }
      try {
        await using var ping = connection.CreateCommand();
        ping.CommandText = "SELECT 1";
        _ = await ping.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return true;
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        throw;
      } catch (Exception ex) {
        // The session that holds the lock is gone — so is the lock. Another instance may already
        // hold the duty; the caller must stop its exclusive work.
        _lost = true;
        LogGrantLost(logger, Duty, instanceId, ex);
        return false;
      }
    }

    public async ValueTask DisposeAsync() {
      if (_disposed) {
        return;
      }
      _disposed = true;
      try {
        if (!_lost) {
          await using (var release = connection.CreateCommand()) {
            release.CommandText = "SELECT release_capability(@id, @duty)";
            release.Parameters.AddWithValue("id", instanceId);
            release.Parameters.AddWithValue("duty", Duty);
            await release.ExecuteNonQueryAsync().ConfigureAwait(false);
          }
          await using (var unlock = connection.CreateCommand()) {
            unlock.CommandText = "SELECT pg_advisory_unlock(@key)";
            unlock.Parameters.AddWithValue("key", key);
            await unlock.ExecuteNonQueryAsync().ConfigureAwait(false);
          }
        }
#pragma warning disable CA1031, RCS1075 // best-effort clean release: a dead session already
        // released the lock server-side, and the recorded holding reaps with the instance row —
        // failing a dispose over it would turn crash-tolerant design into a shutdown error.
      } catch (Exception) {
        // intentionally swallowed — see pragma justification
      }
#pragma warning restore CA1031, RCS1075
      await connection.DisposeAsync().ConfigureAwait(false);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "PgDutyElector: no connection available; cannot contend for duty '{Duty}'")]
  static partial void LogNoConnection(ILogger logger, string duty);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "PgDutyElector: duty '{Duty}' lock connection resolves through a pooled key (pgbouncer transaction pooling does not preserve session locks) — configure the '-direct' connection")]
  static partial void LogPooledFallbackWarning(ILogger logger, string duty);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "PgDutyElector: instance {InstanceId} acquired duty '{Duty}'")]
  static partial void LogAcquired(ILogger logger, string duty, Guid instanceId);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "PgDutyElector: instance {InstanceId} won the '{Duty}' lock but was refused at recording (evicted or unregistered) — released and standing down")]
  static partial void LogRefused(ILogger logger, string duty, Guid instanceId);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "PgDutyElector: grant for duty '{Duty}' on instance {InstanceId} lost its session — another instance may already hold it")]
  static partial void LogGrantLost(ILogger logger, string duty, Guid instanceId, Exception ex);
}
