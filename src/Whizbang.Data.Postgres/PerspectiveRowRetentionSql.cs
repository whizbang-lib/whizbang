// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The perspective row-retention statements both Postgres work coordinators run: enrollment, the expiry and cap
/// sweeps, the adoption gate, the pre-destruction seam's holds, and the stream-group cascade drain.
/// </summary>
/// <remarks>
/// <para>
/// One copy, so the drivers cannot drift: the Dapper coordinator inherited the interface's no-op defaults for all of
/// these, and on that driver no row-retention sweep ever ran (#1505). Each coordinator supplies a command on its own
/// connection and the schema prefix it qualifies names with; every value is a bound parameter.
/// </para>
/// <para>
/// The <c>prefix</c> every member takes is the quoted schema followed by a dot (<c>"svc".</c>), or empty where the
/// caller resolves through <c>search_path</c>. It is always built by <see cref="PgIdentifier"/>, never from input.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowDestructionSeamSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamGroupCascadeSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperRowRetentionTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Identifiers cannot be parameterized: the schema prefix is built by PgIdentifier, table names read back from the registry are validated by PgIdentifier.RequireBare or come from the registry the framework writes, and every value is a bound parameter.")]
internal static class PerspectiveRowRetentionSql {
  internal const string HOLD_TABLE = "wh_perspective_row_hold";
  internal const string REGISTRY_TABLE = "wh_perspective_registry";
  internal const string JOURNAL_TABLE = "wh_row_eviction_journal";
  internal const string EXPIRY_SWEEP = "reap_enrolled_perspective_rows";
  internal const string CAP_SWEEP = "reap_perspective_row_caps";
  internal const string ROW_CAP_SWEEP_KEY = "row_cap_sweep_last_run";
  internal const string ROW_CAP_SWEEP_DESCRIPTION =
    "Last claimed cap sweep: the first instance to CAS this watermark runs the ranking eviction; siblings skip.";

  private const string CLR = "clr";
  private const string CAP = "cap";
  private const string CAP_KEY = "capkey";
  private const string TABLES = "tables";
  private const string IDS = "ids";
  private const string CASCADE_REASON = "cascade";

  /// <summary>
  /// Carries each declaration into the registry, two statements per declaration on commands from
  /// <paramref name="newCommand"/>. A declaration that matches no registry row is a key drift (#697), so it is handed
  /// to <paramref name="onUnmatched"/> rather than swallowed, and the declarations after it are still carried.
  /// </summary>
  internal static async Task SyncRetentionAsync(
      Func<DbCommand> newCommand, string prefix, IReadOnlyList<PerspectiveRetentionDeclaration> declarations,
      Action<string> onUnmatched, CancellationToken cancellationToken) {
    foreach (var declaration in declarations) {
      await using var cmd = newCommand();
      cmd.CommandText = $"SELECT {prefix}sync_perspective_retention(@{CLR}, @enrolled, @ttl, @maxage, @{CAP}, @{CAP_KEY})";
      cmd.Parameters.Add(new NpgsqlParameter(CLR, declaration.ClrTypeName));
      cmd.Parameters.Add(new NpgsqlParameter("enrolled", declaration.Enrolled));
      cmd.Parameters.Add(_nullableInt("ttl", declaration.TtlSeconds));
      cmd.Parameters.Add(_nullableInt("maxage", declaration.MaxAgeSeconds));
      _addCapParameters(cmd, declaration);
      var matched = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      if (ScalarResult.IntOrZero(matched) == 0) {
        onUnmatched(declaration.ClrTypeName);
        continue;
      }

      await using var capCmd = newCommand();
      capCmd.CommandText = $"UPDATE {prefix}{REGISTRY_TABLE} " +
        $"SET row_cap_per_scope = @{CAP}, row_cap_scope_key = @{CAP_KEY} WHERE clr_type_name = @{CLR}";
      capCmd.Parameters.Add(new NpgsqlParameter(CLR, declaration.ClrTypeName));
      _addCapParameters(capCmd, declaration);
      await capCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>The seam's COLLECT phase: the rows the next sweep would destroy, with their payloads.</summary>
  internal static async Task<IReadOnlyList<PerspectiveRowDestructionTarget>> CollectReapTargetsAsync(
      DbCommand cmd, string prefix, IReadOnlyCollection<string> clrTypeNames, int perTableLimit,
      CancellationToken cancellationToken) {
    cmd.CommandText =
      $"SELECT o_clr_type_name, o_table_name, o_row_id, o_scope, o_data, o_reason FROM {prefix}collect_perspective_row_reap_targets(@names, @lim)";
    cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = clrTypeNames.ToArray() });
    cmd.Parameters.Add(new NpgsqlParameter("lim", perTableLimit));
    var targets = new List<PerspectiveRowDestructionTarget>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var scope = await _readScopeAsync(reader, 3, cancellationToken).ConfigureAwait(false);
      using var dataDoc = JsonDocument.Parse(reader.GetString(4));
      targets.Add(new PerspectiveRowDestructionTarget(
        reader.GetString(0), reader.GetString(1), reader.GetGuid(2), scope, dataDoc.RootElement.Clone(), reader.GetString(5)));
    }
    return targets;
  }

  /// <summary>Holds the rows until <paramref name="holdUntil"/>; idempotent.</summary>
  internal static async Task HoldAsync(
      DbCommand cmd, string prefix, IReadOnlyCollection<PerspectiveRowRef> rows, DateTimeOffset holdUntil,
      CancellationToken cancellationToken) {
    cmd.CommandText =
      $"INSERT INTO {prefix}{HOLD_TABLE} (table_name, row_id, hold_until) " +
      $"SELECT u.t, u.r, @until FROM unnest(@{TABLES}, @{IDS}) AS u(t, r) " +
      "ON CONFLICT (table_name, row_id) DO UPDATE SET hold_until = EXCLUDED.hold_until";
    _addRowRefParameters(cmd, rows);
    cmd.Parameters.Add(new NpgsqlParameter("until", holdUntil));
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Releases the holds on the rows.</summary>
  internal static async Task ReleaseHoldsAsync(
      DbCommand cmd, string prefix, IReadOnlyCollection<PerspectiveRowRef> rows, CancellationToken cancellationToken) {
    cmd.CommandText =
      $"DELETE FROM {prefix}{HOLD_TABLE} h USING unnest(@{TABLES}, @{IDS}) AS u(t, r) " +
      "WHERE h.table_name = u.t AND h.row_id = u.r";
    _addRowRefParameters(cmd, rows);
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>The destruction retry ladder; returns the batch's highest failure count.</summary>
  internal static async Task<int> RecordDestructionFailureAsync(
      DbCommand cmd, string prefix, IReadOnlyCollection<PerspectiveRowRef> rows, TimeSpan retryBackoff, int maxRetries,
      OnDestroyFailure onDestroyFailure, CancellationToken cancellationToken) {
    // Under the cap, hold for the backoff and re-offer; past it, the policy decides: '-infinity' is no active hold, so
    // the next sweep takes the row (forced delete); 'infinity' keeps it forever (the explicit leak-risk choice).
    // ForceDeleteImmediately short-circuits on the first failure.
    cmd.CommandText =
      $"INSERT INTO {prefix}{HOLD_TABLE} AS h (table_name, row_id, hold_until, failure_count) " +
      "SELECT u.t, u.r, " +
      "  CASE WHEN @policy = 2 THEN '-infinity'::timestamptz " +
      "       ELSE NOW() + make_interval(secs => @backoff) END, 1 " +
      $"FROM unnest(@{TABLES}, @{IDS}) AS u(t, r) " +
      "ON CONFLICT (table_name, row_id) DO UPDATE SET " +
      "  failure_count = h.failure_count + 1, " +
      "  hold_until = CASE " +
      "    WHEN @policy = 2 THEN '-infinity'::timestamptz " +
      "    WHEN h.failure_count + 1 > @max THEN " +
      "      CASE WHEN @policy = 1 THEN 'infinity'::timestamptz ELSE '-infinity'::timestamptz END " +
      "    ELSE NOW() + make_interval(secs => @backoff) END " +
      "RETURNING failure_count";
    _addRowRefParameters(cmd, rows);
    cmd.Parameters.Add(new NpgsqlParameter("backoff", retryBackoff.TotalSeconds));
    cmd.Parameters.Add(new NpgsqlParameter("max", maxRetries));
    cmd.Parameters.Add(new NpgsqlParameter("policy", (int)onDestroyFailure));
    var highest = 0;
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      highest = Math.Max(highest, reader.GetInt32(0));
    }
    return highest;
  }

  /// <summary>Runs one batched sweep function, <see cref="EXPIRY_SWEEP"/> or <see cref="CAP_SWEEP"/>.</summary>
  internal static async Task<PerspectiveRowReapResult> ReapAsync(
      DbCommand cmd, string prefix, string function, int batchSize, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT rows_affected, status FROM {prefix}{function}(@batch)";
    cmd.Parameters.Add(new NpgsqlParameter("batch", batchSize));
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
      ? new PerspectiveRowReapResult(reader.GetInt32(0), reader.GetString(1))
      : new PerspectiveRowReapResult(0, "no result");
  }

  /// <summary>Opens the adoption gate for one enrolled perspective.</summary>
  internal static async Task AcknowledgeAsync(DbCommand cmd, string prefix, string clrTypeName, CancellationToken cancellationToken) {
    cmd.CommandText = $"UPDATE {prefix}{REGISTRY_TABLE} SET retention_enforcement_acknowledged = TRUE WHERE clr_type_name = @{CLR}";
    cmd.Parameters.Add(new NpgsqlParameter(CLR, clrTypeName));
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Adopts every enrolled, unacknowledged perspective; one row per adoption.</summary>
  internal static async Task<IReadOnlyList<PerspectiveRetentionAdoption>> AdoptAsync(
      DbCommand cmd, string prefix, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT perspective, backlog FROM {prefix}adopt_enrolled_perspective_retention()";
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    var adopted = new List<PerspectiveRetentionAdoption>();
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      adopted.Add(new PerspectiveRetentionAdoption(reader.GetString(0), reader.GetInt64(1)));
    }
    return adopted;
  }

  /// <summary>What enforcement would remove for one enrolled perspective, removing nothing.</summary>
  internal static async Task<long> CountBacklogAsync(DbCommand cmd, string prefix, string clrTypeName, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT {prefix}count_perspective_retention_backlog(@{CLR})";
    cmd.Parameters.Add(new NpgsqlParameter(CLR, clrTypeName));
    return ScalarResult.LongOrZero(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
  }

  /// <summary>Claims a batch of journaled evictions; DELETE ... RETURNING, so replicas never double-cascade.</summary>
  internal static async Task<IReadOnlyList<PerspectiveRowRef>> DrainJournalAsync(
      DbCommand cmd, string prefix, int limit, CancellationToken cancellationToken) {
    var journal = prefix + JOURNAL_TABLE;
    cmd.CommandText =
      $"DELETE FROM {journal} WHERE ctid IN (SELECT ctid FROM {journal} ORDER BY evicted_at LIMIT @n) " +
      "RETURNING table_name, row_id";
    cmd.Parameters.Add(new NpgsqlParameter("n", limit));
    var drained = new List<PerspectiveRowRef>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      drained.Add(new PerspectiveRowRef(reader.GetString(0), reader.GetGuid(1)));
    }
    return drained;
  }

  /// <summary>Re-journals evictions whose cascade was deferred; idempotent.</summary>
  internal static async Task RequeueAsync(
      DbCommand cmd, string prefix, IReadOnlyCollection<PerspectiveRowRef> rows, CancellationToken cancellationToken) {
    cmd.CommandText =
      $"INSERT INTO {prefix}{JOURNAL_TABLE} (table_name, row_id) SELECT u.t, u.r FROM unnest(@{TABLES}, @{IDS}) AS u(t, r) " +
      "ON CONFLICT (table_name, row_id) DO NOTHING";
    _addRowRefParameters(cmd, rows);
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>The registry's (CLR type name, table name) pairs for the given models.</summary>
  internal static async Task<IReadOnlyList<PerspectiveTableName>> TableNamesAsync(
      DbCommand cmd, string prefix, IReadOnlyCollection<string> clrTypeNames, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT clr_type_name, table_name FROM {prefix}{REGISTRY_TABLE} WHERE clr_type_name = ANY(@names)";
    cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = clrTypeNames.ToArray() });
    var names = new List<PerspectiveTableName>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      names.Add(new PerspectiveTableName(reader.GetString(0), reader.GetString(1)));
    }
    return names;
  }

  /// <summary>Loads specific rows as cascade destruction targets, so a guard is offered them with their payloads.</summary>
  internal static async Task<IReadOnlyList<PerspectiveRowDestructionTarget>> RowsByIdsAsync(
      DbCommand cmd, string prefix, string clrTypeName, string tableName, IReadOnlyCollection<Guid> rowIds,
      CancellationToken cancellationToken) {
    // tableName is read from the registry and interpolated UNQUOTED, so unlike the schema it cannot be escaped without
    // changing how PostgreSQL folds its case. Validated instead, which keeps the folding behavior and fails closed.
    var safeTable = PgIdentifier.RequireBare(tableName, nameof(tableName));
    cmd.CommandText = $"SELECT id, scope, data FROM {prefix}{safeTable} WHERE id = ANY(@{IDS})";
    cmd.Parameters.Add(new NpgsqlParameter(IDS, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rowIds.ToArray() });
    var targets = new List<PerspectiveRowDestructionTarget>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var scope = await _readScopeAsync(reader, 1, cancellationToken).ConfigureAwait(false);
      using var dataDoc = JsonDocument.Parse(reader.GetString(2));
      targets.Add(new PerspectiveRowDestructionTarget(
        clrTypeName, tableName, reader.GetGuid(0), scope, dataDoc.RootElement.Clone(), CASCADE_REASON));
    }
    return targets;
  }

  /// <summary>One member table's share of the group cascade: a hold-aware delete. Returns the count destroyed.</summary>
  internal static async Task<int> CascadeDeleteAsync(
      DbCommand cmd, string prefix, string tableName, IReadOnlyCollection<Guid> rowIds, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT {prefix}cascade_delete_perspective_rows(@t, @{IDS})";
    cmd.Parameters.Add(new NpgsqlParameter("t", tableName));
    cmd.Parameters.Add(new NpgsqlParameter(IDS, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rowIds.ToArray() });
    return ScalarResult.IntOrZero(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
  }

  /// <summary>
  /// The staged rebuild's presence reconcile: deletes follower rows absent from every announcer, hold-aware. With
  /// <paramref name="streamIds"/> only those streams' rows are read; null reconciles the whole table.
  /// </summary>
  internal static async Task<int> ReconcileFollowerPresenceAsync(
      DbCommand cmd, string prefix, string followerTable, IReadOnlyCollection<string> announcerTables,
      IReadOnlyCollection<Guid>? streamIds, CancellationToken cancellationToken) {
    // The table names come from wh_perspective_registry, which the framework writes, not from input.
    var absentFromEveryAnnouncer = string.Join(" AND ", announcerTables.Select(a =>
      $"NOT EXISTS (SELECT 1 FROM {prefix}{a} a WHERE a.id = f.id)"));
    var scoped = streamIds is null ? "" : $"f.id = ANY(@{IDS}) AND ";
    cmd.CommandText =
      $"DELETE FROM {prefix}{followerTable} f WHERE {scoped}{absentFromEveryAnnouncer} " +
      $"AND NOT EXISTS (SELECT 1 FROM {prefix}{HOLD_TABLE} h WHERE h.table_name = @t AND h.row_id = f.id AND h.hold_until > NOW())";
    cmd.Parameters.Add(new NpgsqlParameter("t", followerTable));
    if (streamIds is not null) {
      cmd.Parameters.Add(new NpgsqlParameter(IDS, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = streamIds.ToArray() });
    }
    return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// The one-per-service claim: compare-and-swap a timestamp watermark in <c>wh_settings</c>. The update fires only
  /// when the stored instant is older than the window, so exactly one instance wins.
  /// </summary>
  internal static async Task<bool> TryClaimWatermarkAsync(
      DbCommand cmd, string prefix, string settingKey, string description, TimeSpan claimWindow,
      CancellationToken cancellationToken) {
    var settings = prefix + "wh_settings";
    cmd.CommandText = $"""
      INSERT INTO {settings} (setting_key, setting_value, value_type, description)
      VALUES (@p_key, NOW()::text, 'timestamptz', @p_description)
      ON CONFLICT (setting_key) DO UPDATE
        SET setting_value = NOW()::text, updated_at = NOW()
        WHERE ({settings}.setting_value)::timestamptz <= NOW() - @p_window
      """;
    cmd.Parameters.Add(new NpgsqlParameter("p_key", settingKey));
    cmd.Parameters.Add(new NpgsqlParameter("p_description", description));
    cmd.Parameters.Add(new NpgsqlParameter("p_window", NpgsqlDbType.Interval) { Value = claimWindow });
    return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
  }

  private static async Task<JsonElement?> _readScopeAsync(DbDataReader reader, int ordinal, CancellationToken cancellationToken) {
    if (await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)) {
      return null;
    }
    using var scopeDoc = JsonDocument.Parse(reader.GetString(ordinal));
    return scopeDoc.RootElement.Clone();
  }

  private static NpgsqlParameter _nullableInt(string name, int? value) =>
    new(name, NpgsqlDbType.Integer) { Value = (object?)value ?? DBNull.Value };

  private static void _addCapParameters(DbCommand cmd, PerspectiveRetentionDeclaration declaration) {
    cmd.Parameters.Add(_nullableInt(CAP, declaration.CapPerScope));
    cmd.Parameters.Add(new NpgsqlParameter(CAP_KEY, NpgsqlDbType.Text) { Value = (object?)declaration.CapScopeKey ?? DBNull.Value });
  }

  private static void _addRowRefParameters(DbCommand cmd, IReadOnlyCollection<PerspectiveRowRef> rows) {
    cmd.Parameters.Add(new NpgsqlParameter(TABLES, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => r.TableName).ToArray() });
    cmd.Parameters.Add(new NpgsqlParameter(IDS, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(r => r.RowId).ToArray() });
  }
}
