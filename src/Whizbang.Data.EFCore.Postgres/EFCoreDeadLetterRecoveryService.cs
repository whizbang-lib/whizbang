using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EFCore + Npgsql implementation of <see cref="IDeadLetterRecoveryService"/>. Thin
/// wrapper over the 6 recovery SQL functions in migration 051.
/// </summary>
/// <docs>operations/dead-letter-queue/recovery</docs>
[SuppressMessage("csharpsquid", "S2077:Formatting SQL queries is security-sensitive",
  Justification = "The only interpolated value is a schema-qualified SQL identifier (\"schema\".fn) " +
    "resolved from the EF Core model's configured schema (HasDefaultSchema), not user input. SQL " +
    "identifiers cannot be parameterized; there is no injection vector. All row values are @parameters.")]
public sealed class EFCoreDeadLetterRecoveryService<TDbContext>(
  TDbContext dbContext,
  ILogger<EFCoreDeadLetterRecoveryService<TDbContext>> logger,
  WorkCoordinatorGate? gate = null
) : IDeadLetterRecoveryService where TDbContext : DbContext {
  private readonly TDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
  private readonly ILogger<EFCoreDeadLetterRecoveryService<TDbContext>> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly WorkCoordinatorGate? _gate = gate;

  // The DLQ functions are created in the service schema (__SCHEMA__.<fn>, migration 051). Callers
  // must schema-qualify them the same way EFCoreWorkCoordinator qualifies its SQL — a bare call only
  // resolves when the connection's search_path happens to include the service schema, which is not
  // guaranteed (e.g. the ECommerce per-service schemas: 42883 "function ... does not exist").
  // Tables need the same schema qualification as functions — see _fn's rationale.
  private string _tbl(string name) {
    var schema = _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    return string.IsNullOrWhiteSpace(schema) || schema == "public" ? name : $"\"{schema}\".{name}";
  }

  private string _fn(string name) {
    var schema = _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    return string.IsNullOrWhiteSpace(schema) || schema == "public" ? name : $"\"{schema}\".{name}";
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<DeadLetterEntry>> FetchDueAsync(int maxCount, CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT dead_letter_id, source_table, source_id, stream_id, message_type, failure_reason, attempts_when_dlq, dead_lettered_at, recovery_status, recovery_attempts, generation FROM {_fn("fetch_dead_letters_due")}(NOW(), @max)";
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
  public async Task<int> PurgeUndeliverableHeldAsync(CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {_fn("purge_undeliverable_held_dead_letters")}()";
    return (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<HeldCohort>> ListHeldCohortsAsync(CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT fingerprint, row_count, message_type_count FROM {_fn("list_held_dead_letter_cohorts")}()";
    var results = new List<HeldCohort>();
    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      results.Add(new HeldCohort(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2)));
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<int> BeginCanaryProbesAsync(string fingerprint, string generation, int probeSize, int generationBudget, CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {_fn("begin_canary_probes")}(@fp, @gen, @size, @budget)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("fp", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = fingerprint });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("gen", NpgsqlTypes.NpgsqlDbType.Text) { Value = generation });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("size", NpgsqlTypes.NpgsqlDbType.Integer) { Value = probeSize });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("budget", NpgsqlTypes.NpgsqlDbType.Integer) { Value = generationBudget });
    return (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  public async Task<CanaryVerdict> EvaluateCampaignAsync(string fingerprint, string generation, CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT verdict, probes_succeeded, probes_failed, probes_outstanding FROM {_fn("evaluate_canary_campaign")}(@fp, @gen)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("fp", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = fingerprint });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("gen", NpgsqlTypes.NpgsqlDbType.Text) { Value = generation });
    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
    if (!await reader.ReadAsync(ct).ConfigureAwait(false)) {
      return new CanaryVerdict(CanaryVerdictKind.Pending, 0, 0, 0);
    }
    return new CanaryVerdict(
      (CanaryVerdictKind)reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
  }

  /// <inheritdoc />
  public async Task<int> ReleaseHeldCohortAsync(string fingerprint, TimeSpan stagger, CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {_fn("release_held_dead_letter_cohort")}(@fp, @stagger)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("fp", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = fingerprint });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("stagger", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)stagger.TotalSeconds });
    return (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<UnstackedDeadLetter>> FetchUnstackedAsync(int maxCount, CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT dead_letter_id, error_text FROM {_fn("fetch_unstacked_dead_letters")}(@max)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("max", NpgsqlTypes.NpgsqlDbType.Integer) { Value = maxCount });
    var results = new List<UnstackedDeadLetter>();
    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      results.Add(new UnstackedDeadLetter(reader.GetGuid(0), reader.GetString(1)));
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<int> RecordStacksAsync(
      IReadOnlyList<(Guid DeadLetterId, Whizbang.Core.DeadLetters.StackIdentity Stack)> entries,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(entries);
    if (entries.Count == 0) {
      return 0;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    // Hand-built JSON: JsonNode.Add is not AOT/trim-safe (IL2026/IL3050), and every value
    // here is a string/bool, so a StringBuilder is both correct and reflection-free.
    var sb = new System.Text.StringBuilder("[");
    for (var i = 0; i < entries.Count; i++) {
      var (id, stack) = entries[i];
      if (i > 0) { sb.Append(','); }
      sb.Append("{\"dead_letter_id\":\"").Append(id.ToString())
        .Append("\",\"stack_id\":\"").Append(System.Text.Json.JsonEncodedText.Encode(stack.SequenceHash)).Append('"')
        .Append(",\"is_prose\":").Append(stack.IsProse ? "true" : "false")
        .Append(",\"frames\":[");
      for (var j = 0; j < stack.Frames.Count; j++) {
        if (j > 0) { sb.Append(','); }
        sb.Append('"').Append(System.Text.Json.JsonEncodedText.Encode(stack.Frames[j])).Append('"');
      }
      sb.Append("]}");
    }
    sb.Append(']');
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {_fn("record_dead_letter_stacks")}(@entries::jsonb)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(nameof(entries), NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = sb.ToString() });
    return (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  public async Task<DeadLetterStatusSummary> GetStatusSummaryAsync(CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    long pending = 0, recovering = 0, held = 0, permanentlyFailed = 0, recovered = 0;
    await using (var counts = conn.CreateCommand()) {
      counts.CommandText =
        "SELECT count(*) FILTER (WHERE recovered_at IS NULL AND recovery_status = 0), "
      + "       count(*) FILTER (WHERE recovered_at IS NULL AND recovery_status = 1), "
      + "       count(*) FILTER (WHERE recovered_at IS NULL AND recovery_status = 2), "
      + "       count(*) FILTER (WHERE recovery_status = 4), "
      + "       count(*) FILTER (WHERE recovered_at IS NOT NULL) "
      + $"FROM {_tbl("wh_dead_letters")}";
      await using var r = await counts.ExecuteReaderAsync(ct).ConfigureAwait(false);
      if (await r.ReadAsync(ct).ConfigureAwait(false)) {
        pending = r.GetInt64(0);
        recovering = r.GetInt64(1);
        held = r.GetInt64(2);
        permanentlyFailed = r.GetInt64(3);
        recovered = r.GetInt64(4);
      }
    }
    var cohorts = await ListHeldCohortsAsync(ct).ConfigureAwait(false);
    var campaigns = new List<CampaignStatus>();
    await using (var camp = conn.CreateCommand()) {
      camp.CommandText =
        "SELECT fingerprint, generation, verdict, probes_succeeded, probes_failed, wave "
      + $"FROM {_tbl("wh_dlq_probe_campaigns")} ORDER BY started_at DESC LIMIT 20";
      await using var r = await camp.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await r.ReadAsync(ct).ConfigureAwait(false)) {
        campaigns.Add(new CampaignStatus(
          r.GetString(0), r.GetString(1), (CanaryVerdictKind)r.GetInt32(2),
          r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)));
      }
    }
    return new DeadLetterStatusSummary(pending, recovering, held, permanentlyFailed, recovered, cohorts, campaigns);
  }

  /// <inheritdoc />
  public async Task<int> PruneStackHistoryAsync(int retentionDays, CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {_fn("prune_stack_history")}(@days)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("days", NpgsqlTypes.NpgsqlDbType.Integer) { Value = retentionDays });
    return (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  public async Task RecordStackAsync(Guid deadLetterId, Whizbang.Core.DeadLetters.StackIdentity stack, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(stack);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {_fn("record_dead_letter_stack")}(@id, @sid, @prose, @frames)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = deadLetterId });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("sid", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = stack.SequenceHash });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("prose", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = stack.IsProse });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("frames", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = stack.Frames.ToArray() });
    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<int> BeginTrickleWaveAsync(string fingerprint, string generation, int waveSize, CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {_fn("begin_trickle_wave")}(@fp, @gen, @size)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("fp", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = fingerprint });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("gen", NpgsqlTypes.NpgsqlDbType.Text) { Value = generation });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("size", NpgsqlTypes.NpgsqlDbType.Integer) { Value = waveSize });
    return (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  public async Task<int> CountWaveRequarantinesAsync(string fingerprint, string generation, CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {_fn("count_wave_requarantines")}(@fp, @gen)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("fp", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = fingerprint });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("gen", NpgsqlTypes.NpgsqlDbType.Text) { Value = generation });
    return (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  public async Task<bool> RecoverAsync(Guid deadLetterId, CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {_fn("recover_dead_letter")}(@id)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = deadLetterId });
    var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    return result is bool b && b;
  }

  /// <inheritdoc />
  public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default)
    => _execAsync($"SELECT {_fn("mark_dead_letter_holding")}(@id)", deadLetterId, ct);

  /// <inheritdoc />
  public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default)
    => _execAsync($"SELECT {_fn("mark_dead_letter_permanently_failed")}(@id)", deadLetterId, ct);

  /// <inheritdoc />
  public async Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {_fn("schedule_next_dead_letter_attempt")}(@id, @at)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = deadLetterId });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("at", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = nextAt });
    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<int> ResetForGenerationAsync(string currentGeneration, int staggerMinutes, CancellationToken ct = default) {
    ArgumentException.ThrowIfNullOrEmpty(currentGeneration);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {_fn("reset_dead_letters_for_generation")}(@gen, @stagger)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("gen", NpgsqlTypes.NpgsqlDbType.Text) { Value = currentGeneration });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("stagger", NpgsqlTypes.NpgsqlDbType.Integer) { Value = staggerMinutes });
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
