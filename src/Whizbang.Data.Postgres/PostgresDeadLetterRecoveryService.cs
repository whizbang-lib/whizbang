// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The dead-letter recovery surface both Postgres drivers run (#1934): a thin wrapper over the recovery SQL functions
/// (migration 051 and later), which the drivers share. The EF Core driver's <c>EFCoreDeadLetterRecoveryService</c> runs it
/// on its context's connection; the Dapper driver registers it over connections it opens per call. Without it the Dapper
/// driver had no recovery service, so the dead-letter recovery worker redrove nothing there, an
/// <see cref="MessageFailureReason.AwaitingRestore"/> dead letter included once its stream's restore made it due.
/// </summary>
/// <remarks>
/// Each call takes the work-coordinator gate's slot when a gate is given, then a connection from the opener, and gives
/// both back when it ends. A connection the service opened (<c>ownsConnection</c>) is closed with the call; a borrowed
/// one (the EF Core context's) is left open for its owner.
/// </remarks>
/// <docs>operations/dead-letter-queue/recovery</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveRestoreRedriveTests.cs:AddWhizbangPostgres_AnAwaitingRestoreDeadLetter_IsRedrivenThroughTheOutbox_OnceItsRestoreCompletesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreDeadLetterRecoveryServiceTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BranchCoverage/EFCore/DeadLetterGateAndModelSchemaTests.cs</tests>
[SuppressMessage("csharpsquid", "S2077:Formatting SQL queries is security-sensitive",
  Justification = "The only interpolated value is a SQL function name, schema-qualified by the driver's qualifier from the " +
    "framework's configured schema, not user input. SQL identifiers cannot be parameterized; all row values are @parameters.")]
internal sealed class PostgresDeadLetterRecoveryService : IDeadLetterRecoveryService {
  private readonly Func<CancellationToken, ValueTask<DbConnection>> _open;
  private readonly bool _ownsConnection;
  private readonly Func<string, string> _fn;
  private readonly WorkCoordinatorGate? _gate;
  private readonly int _partitionCount;

  /// <summary>Creates the service.</summary>
  /// <param name="open">Opens (or hands over, already open) the connection a call runs on.</param>
  /// <param name="ownsConnection">Whether a call closes the connection when it ends: true for a connection opened for
  /// the call, false for one borrowed from its owner.</param>
  /// <param name="fn">Qualifies a recovery function's name with the framework's schema.</param>
  /// <param name="gate">The gate bounding concurrent coordinator calls, when one is registered.</param>
  /// <param name="partitionCount">The partition count a recovered outbox or perspective row is stored with (#1722).</param>
  internal PostgresDeadLetterRecoveryService(
      Func<CancellationToken, ValueTask<DbConnection>> open,
      bool ownsConnection,
      Func<string, string> fn,
      WorkCoordinatorGate? gate,
      int partitionCount) {
    ArgumentNullException.ThrowIfNull(open);
    ArgumentNullException.ThrowIfNull(fn);
    _open = open;
    _ownsConnection = ownsConnection;
    _fn = fn;
    _gate = gate;
    _partitionCount = partitionCount;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<DeadLetterEntry>> FetchDueAsync(int maxCount, CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync(
      $"SELECT dead_letter_id, source_table, source_id, stream_id, message_type, failure_reason, attempts_when_dlq, dead_lettered_at, recovery_status, recovery_attempts, generation, error_fingerprint FROM {_fn("fetch_dead_letters_due")}(NOW(), @max)",
      ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("max", NpgsqlTypes.NpgsqlDbType.Integer) { Value = maxCount });
    var results = new List<DeadLetterEntry>();
    await using var reader = await call.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      results.Add(new DeadLetterEntry(
        DeadLetterId: reader.GetGuid(0),
        SourceTable: reader.GetString(1),
        SourceId: reader.GetGuid(2),
        StreamId: await reader.IsDBNullAsync(3, ct).ConfigureAwait(false) ? null : reader.GetGuid(3),
        MessageType: reader.GetString(4),
        FailureReason: (MessageFailureReason)reader.GetInt32(5),
        AttemptsWhenDlq: reader.GetInt32(6),
        DeadLetteredAt: await reader.GetFieldValueAsync<DateTimeOffset>(7, ct).ConfigureAwait(false),
        RecoveryStatus: (DeadLetterRecoveryStatus)reader.GetInt32(8),
        RecoveryAttempts: reader.GetInt32(9),
        Generation: reader.GetString(10),
        ErrorFingerprint: await reader.IsDBNullAsync(11, ct).ConfigureAwait(false) ? null : reader.GetString(11)));
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<int> PurgeUndeliverableHeldAsync(CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync($"SELECT {_fn("purge_undeliverable_held_dead_letters")}()", ct).ConfigureAwait(false);
    return (int)(await call.Command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<HeldCohort>> ListHeldCohortsAsync(CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync(
      $"SELECT fingerprint, row_count, message_type_count FROM {_fn("list_held_dead_letter_cohorts")}()", ct).ConfigureAwait(false);
    var results = new List<HeldCohort>();
    await using var reader = await call.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      results.Add(new HeldCohort(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2)));
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<int> BeginCanaryProbesAsync(string fingerprint, string generation, int probeSize, int generationBudget, CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync($"SELECT {_fn("begin_canary_probes")}(@fp, @gen, @size, @budget)", ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("fp", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = fingerprint });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("gen", NpgsqlTypes.NpgsqlDbType.Text) { Value = generation });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("size", NpgsqlTypes.NpgsqlDbType.Integer) { Value = probeSize });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("budget", NpgsqlTypes.NpgsqlDbType.Integer) { Value = generationBudget });
    return (int)(await call.Command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  public async Task<CanaryVerdict> EvaluateCampaignAsync(string fingerprint, string generation, CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync(
      $"SELECT verdict, probes_succeeded, probes_failed, probes_outstanding FROM {_fn("evaluate_canary_campaign")}(@fp, @gen)",
      ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("fp", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = fingerprint });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("gen", NpgsqlTypes.NpgsqlDbType.Text) { Value = generation });
    await using var reader = await call.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    if (!await reader.ReadAsync(ct).ConfigureAwait(false)) {
      return new CanaryVerdict(CanaryVerdictKind.Pending, 0, 0, 0);
    }
    return new CanaryVerdict(
      (CanaryVerdictKind)reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
  }

  /// <inheritdoc />
  public async Task<int> ReleaseHeldCohortAsync(string fingerprint, TimeSpan stagger, CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync($"SELECT {_fn("release_held_dead_letter_cohort")}(@fp, @stagger)", ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("fp", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = fingerprint });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter(nameof(stagger), NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)stagger.TotalSeconds });
    return (int)(await call.Command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<string>> GetPassedCampaignFingerprintsAsync(string generation, CancellationToken ct = default) {
    ArgumentException.ThrowIfNullOrEmpty(generation);
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync($"SELECT fingerprint FROM {_fn("get_passed_campaign_fingerprints")}(@gen)", ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("gen", NpgsqlTypes.NpgsqlDbType.Text) { Value = generation });
    var results = new List<string>();
    await using var reader = await call.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      results.Add(reader.GetString(0));
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<UnstackedDeadLetter>> FetchUnstackedAsync(int maxCount, CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync($"SELECT dead_letter_id, error_text FROM {_fn("fetch_unstacked_dead_letters")}(@max)", ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("max", NpgsqlTypes.NpgsqlDbType.Integer) { Value = maxCount });
    var results = new List<UnstackedDeadLetter>();
    await using var reader = await call.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);
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
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
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
    await using var call = await _callAsync($"SELECT {_fn("record_dead_letter_stacks")}(@entries::jsonb)", ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter(nameof(entries), NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = sb.ToString() });
    return (int)(await call.Command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  public async Task<int> PruneStackHistoryAsync(int retentionDays, CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync($"SELECT {_fn("prune_stack_history")}(@days)", ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("days", NpgsqlTypes.NpgsqlDbType.Integer) { Value = retentionDays });
    return (int)(await call.Command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  public async Task RecordStackAsync(Guid deadLetterId, Whizbang.Core.DeadLetters.StackIdentity stack, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(stack);
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync($"SELECT {_fn("record_dead_letter_stack")}(@id, @sid, @prose, @frames)", ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = deadLetterId });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("sid", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = stack.SequenceHash });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("prose", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = stack.IsProse });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("frames", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = stack.Frames.ToArray() });
    await call.Command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<int> BeginTrickleWaveAsync(string fingerprint, string generation, int waveSize, CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync($"SELECT {_fn("begin_trickle_wave")}(@fp, @gen, @size)", ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("fp", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = fingerprint });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("gen", NpgsqlTypes.NpgsqlDbType.Text) { Value = generation });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("size", NpgsqlTypes.NpgsqlDbType.Integer) { Value = waveSize });
    return (int)(await call.Command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  public async Task<int> CountWaveRequarantinesAsync(string fingerprint, string generation, CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync($"SELECT {_fn("count_wave_requarantines")}(@fp, @gen)", ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("fp", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = fingerprint });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("gen", NpgsqlTypes.NpgsqlDbType.Text) { Value = generation });
    return (int)(await call.Command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
  }

  /// <inheritdoc />
  /// <remarks>
  /// An outbox or perspective dead letter re-enters through its store, stored with this service's partition count
  /// (#1722); the store claims the dead letter, and the completion settles it.
  /// </remarks>
  public async Task<bool> RecoverAsync(Guid deadLetterId, CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync($"SELECT {_fn("wh_recover_dead_letter")}(@id, @partitions)", ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = deadLetterId });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("partitions", NpgsqlTypes.NpgsqlDbType.Integer) { Value = _partitionCount });
    var result = await call.Command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    return result is bool b && b;
  }

  /// <inheritdoc />
  public async Task<DeadLetterRedelivery?> ReadRedeliveryAsync(Guid deadLetterId, CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync(
      $"SELECT source_table, source_id, message_type, (envelope -> 'event_data')::text FROM {_fn("wh_dead_letters")} "
        + "WHERE dead_letter_id = @id AND source_table IN ('wh_inbox', 'broker') AND envelope -> 'event_data' IS NOT NULL",
      ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = deadLetterId });
    await using var reader = await call.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    if (!await reader.ReadAsync(ct).ConfigureAwait(false)) {
      return null;
    }
    return new DeadLetterRedelivery(
      DeadLetterId: deadLetterId,
      SourceTable: reader.GetString(0),
      SourceId: reader.GetGuid(1),
      MessageType: reader.GetString(2),
      EnvelopeJson: reader.GetString(3));
  }

  /// <inheritdoc />
  public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default)
    => _execAsync($"SELECT {_fn("mark_dead_letter_holding")}(@id)", deadLetterId, ct);

  /// <inheritdoc />
  public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default)
    => _execAsync($"SELECT {_fn("mark_dead_letter_permanently_failed")}(@id)", deadLetterId, ct);

  /// <inheritdoc />
  public async Task MarkDiscardedAsync(Guid deadLetterId, string note, CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync($"SELECT {_fn("mark_dead_letter_discarded")}(@id, @note)", ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = deadLetterId });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter(nameof(note), NpgsqlTypes.NpgsqlDbType.Text) { Value = note ?? (object)DBNull.Value });
    await call.Command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) {
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync($"SELECT {_fn("schedule_next_dead_letter_attempt")}(@id, @at)", ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = deadLetterId });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("at", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = nextAt });
    await call.Command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<int> ResetForGenerationAsync(string currentGeneration, int staggerMinutes, CancellationToken ct = default) {
    ArgumentException.ThrowIfNullOrEmpty(currentGeneration);
    using var __ = await _acquireAsync(ct).ConfigureAwait(false);
    await using var call = await _callAsync($"SELECT {_fn("reset_dead_letters_for_generation")}(@gen, @stagger)", ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("gen", NpgsqlTypes.NpgsqlDbType.Text) { Value = currentGeneration });
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter("stagger", NpgsqlTypes.NpgsqlDbType.Integer) { Value = staggerMinutes });
    var result = await call.Command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    return result is int n ? n : 0;
  }

  private async Task _execAsync(string sql, Guid id, CancellationToken ct, [CallerMemberName] string caller = "<unknown>") {
    using var __ = await _acquireAsync(ct, caller).ConfigureAwait(false);
    await using var call = await _callAsync(sql, ct).ConfigureAwait(false);
    call.Command.Parameters.Add(new Npgsql.NpgsqlParameter(nameof(id), NpgsqlTypes.NpgsqlDbType.Uuid) { Value = id });
    await call.Command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  // The gate's slot for one call, tagged with the public method that asked; no slot without a gate.
  private async ValueTask<WorkCoordinatorGate.Releaser> _acquireAsync(CancellationToken ct, [CallerMemberName] string caller = "<unknown>") =>
    _gate is null ? default : await _gate.AcquireAsync(ct, caller).ConfigureAwait(false);

  // The call's connection and its command, given back together.
  private async ValueTask<Call> _callAsync(string sql, CancellationToken ct) {
    var connection = await _open(ct).ConfigureAwait(false);
    var command = connection.CreateCommand();
    command.CommandText = sql;
    return new Call(connection, command, _ownsConnection);
  }

  private sealed class Call(DbConnection connection, DbCommand command, bool ownsConnection) : IAsyncDisposable {
    public DbCommand Command => command;

    public async ValueTask DisposeAsync() {
      await command.DisposeAsync().ConfigureAwait(false);
      if (ownsConnection) {
        await connection.DisposeAsync().ConfigureAwait(false);
      }
    }
  }
}
