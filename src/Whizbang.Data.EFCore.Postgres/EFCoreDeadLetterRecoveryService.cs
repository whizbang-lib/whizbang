using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EFCore + Npgsql implementation of <see cref="IDeadLetterRecoveryService"/>. Thin
/// wrapper over the 6 recovery SQL functions in migration 051.
/// </summary>
/// <docs>operations/dead-letter-queue/recovery</docs>
public sealed class EFCoreDeadLetterRecoveryService<TDbContext>(
  TDbContext dbContext,
  ILogger<EFCoreDeadLetterRecoveryService<TDbContext>> logger,
  WorkCoordinatorGate? gate = null
) : IDeadLetterRecoveryService where TDbContext : DbContext {
  private readonly TDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
  private readonly ILogger<EFCoreDeadLetterRecoveryService<TDbContext>> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly WorkCoordinatorGate? _gate = gate;

  /// <inheritdoc />
  public async Task<IReadOnlyList<DeadLetterEntry>> FetchDueAsync(int maxCount, CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT dead_letter_id, source_table, source_id, stream_id, message_type, failure_reason, attempts_when_dlq, dead_lettered_at, recovery_status, recovery_attempts, generation FROM fetch_dead_letters_due(NOW(), @max)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("max", NpgsqlTypes.NpgsqlDbType.Integer) { Value = maxCount });
    var results = new List<DeadLetterEntry>();
    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      results.Add(new DeadLetterEntry(
        DeadLetterId: reader.GetGuid(0),
        SourceTable: reader.GetString(1),
        SourceId: reader.GetGuid(2),
        StreamId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
        MessageType: reader.GetString(4),
        FailureReason: (MessageFailureReason)reader.GetInt32(5),
        AttemptsWhenDlq: reader.GetInt32(6),
        DeadLetteredAt: reader.GetFieldValue<DateTimeOffset>(7),
        RecoveryStatus: (DeadLetterRecoveryStatus)reader.GetInt32(8),
        RecoveryAttempts: reader.GetInt32(9),
        Generation: reader.GetString(10)));
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<bool> RecoverAsync(Guid deadLetterId, CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT recover_dead_letter(@id)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = deadLetterId });
    var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    return result is bool b && b;
  }

  /// <inheritdoc />
  public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default)
    => _execAsync("SELECT mark_dead_letter_holding(@id)", deadLetterId, ct);

  /// <inheritdoc />
  public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default)
    => _execAsync("SELECT mark_dead_letter_permanently_failed(@id)", deadLetterId, ct);

  /// <inheritdoc />
  public async Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT schedule_next_dead_letter_attempt(@id, @at)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = deadLetterId });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("at", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = nextAt });
    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<int> ResetForGenerationAsync(string currentGeneration, CancellationToken ct = default) {
    ArgumentException.ThrowIfNullOrEmpty(currentGeneration);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT reset_dead_letters_for_generation(@gen)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("gen", NpgsqlTypes.NpgsqlDbType.Text) { Value = currentGeneration });
    var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    return result is int n ? n : 0;
  }

  private async Task _execAsync(string sql, Guid id, CancellationToken ct) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = id });
    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }
}
