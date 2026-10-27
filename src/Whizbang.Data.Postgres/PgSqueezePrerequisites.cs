// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;

namespace Whizbang.Data.Postgres;

/// <summary>What the server says about one table and the role that would squeeze it, read before each call.</summary>
/// <param name="Role">The connection's role (<c>current_user</c>).</param>
/// <param name="TableExists">Whether the table exists.</param>
/// <param name="SqueezeInstalled">Whether <c>squeeze.squeeze_table</c> exists in this database.</param>
/// <param name="CanExecuteSqueeze">Whether the role has <c>USAGE</c> on schema <c>squeeze</c> and <c>EXECUTE</c> on the function.</param>
/// <param name="WalLevel">The server's <c>wal_level</c>.</param>
/// <param name="ServerVersionNum">The server's <c>server_version_num</c>.</param>
/// <param name="SlotsInUse">How many replication slots exist (<c>pg_replication_slots</c>).</param>
/// <param name="MaxReplicationSlots">The server's <c>max_replication_slots</c>.</param>
/// <param name="HasIdentityIndex">Whether the table has a primary key or a replica identity index.</param>
/// <param name="CanReplicate">Whether the role is a superuser or has the <c>REPLICATION</c> attribute.</param>
/// <param name="OwnsTable">Whether the role is a member of the table's owner.</param>
/// <docs>operations/infrastructure/storage-maintenance#online-rewrite-prerequisites</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PgSqueezePrerequisitesTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OnlineTableRewriteSqlTests.cs</tests>
public readonly record struct PgSqueezePrerequisiteFacts(
  string Role,
  bool TableExists,
  bool SqueezeInstalled,
  bool CanExecuteSqueeze,
  string WalLevel,
  int ServerVersionNum,
  int SlotsInUse,
  int MaxReplicationSlots,
  bool HasIdentityIndex,
  bool CanReplicate,
  bool OwnsTable);

/// <summary>
/// What a <c>squeeze.squeeze_table</c> call depends on, read with one catalog query and judged before the call, so a
/// table that cannot be squeezed is reported with the reason instead of failing inside pg_squeeze's background worker.
/// </summary>
/// <remarks>
/// <para>
/// pg_squeeze (1.6 and later) runs the rewrite in a background worker that connects as the calling role and creates a
/// logical replication slot, and from Postgres 15 creating the slot checks that the role may (superuser or
/// <c>REPLICATION</c>). Below Postgres 19 logical decoding needs <c>wal_level = logical</c>. The worker applies the
/// changes made during the copy through the table's identity index, so it needs a primary key or a replica identity
/// index. pg_squeeze itself does not check that the caller owns the table; the library does, because the
/// <c>ANALYZE</c> that follows needs ownership and a rewrite should run as the table's owner, as <c>VACUUM FULL</c> does.
/// </para>
/// <para>The checks are reported in a fixed order, so the operator's first fix comes first.</para>
/// </remarks>
/// <docs>operations/infrastructure/storage-maintenance#online-rewrite-prerequisites</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PgSqueezePrerequisitesTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OnlineTableRewriteSqlTests.cs</tests>
public static class PgSqueezePrerequisites {
  /// <summary>The first Postgres version that enables logical decoding on demand, without <c>wal_level = logical</c>.</summary>
  private const int LOGICAL_DECODING_ON_DEMAND = 190_000;

  /// <summary>The query; <c>@q</c> is the table's (possibly schema-qualified) name.</summary>
  internal const string SQL = """
    SELECT current_user::TEXT,
           to_regclass(@q) IS NOT NULL,
           EXISTS (SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                   WHERE n.nspname = 'squeeze' AND p.proname = 'squeeze_table'),
           COALESCE((SELECT bool_or(has_schema_privilege(n.oid, 'USAGE') AND has_function_privilege(p.oid, 'EXECUTE'))
                     FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                     WHERE n.nspname = 'squeeze' AND p.proname = 'squeeze_table'), false),
           current_setting('wal_level'),
           current_setting('server_version_num')::INT,
           (SELECT count(*)::INT FROM pg_catalog.pg_replication_slots),
           current_setting('max_replication_slots')::INT,
           EXISTS (SELECT 1 FROM pg_catalog.pg_index i
                   WHERE i.indrelid = to_regclass(@q) AND (i.indisprimary OR i.indisreplident)),
           COALESCE((SELECT r.rolsuper OR r.rolreplication FROM pg_catalog.pg_roles r WHERE r.rolname = current_user), false),
           COALESCE((SELECT pg_has_role(current_user, c.relowner, 'MEMBER')
                     FROM pg_catalog.pg_class c WHERE c.oid = to_regclass(@q)), false)
    """;

  /// <summary>Reads the facts for <paramref name="table"/> on an open connection, as its role.</summary>
  /// <param name="connection">An open connection: the one the rewrite would run on.</param>
  /// <param name="table">The table, as <c>to_regclass</c> takes it (schema-qualified, or resolved through the search path).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The facts.</returns>
  public static async Task<PgSqueezePrerequisiteFacts> ReadAsync(
      DbConnection connection, string table, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    await using var command = connection.CreateCommand();
    command.CommandText = SQL;
    var parameter = command.CreateParameter();
    parameter.ParameterName = "q";
    parameter.Value = table;
    command.Parameters.Add(parameter);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new PgSqueezePrerequisiteFacts(
      Role: reader.GetString(0),
      TableExists: reader.GetBoolean(1),
      SqueezeInstalled: reader.GetBoolean(2),
      CanExecuteSqueeze: reader.GetBoolean(3),
      WalLevel: reader.GetString(4),
      ServerVersionNum: reader.GetInt32(5),
      SlotsInUse: reader.GetInt32(6),
      MaxReplicationSlots: reader.GetInt32(7),
      HasIdentityIndex: reader.GetBoolean(8),
      CanReplicate: reader.GetBoolean(9),
      OwnsTable: reader.GetBoolean(10));
  }

  /// <summary>The first prerequisite <paramref name="facts"/> leaves unmet, as a sentence for a log line, or null when all are met.</summary>
  /// <param name="facts">What the server said.</param>
  /// <param name="table">The table's name, for the sentence.</param>
  /// <returns>Why the table cannot be squeezed now, or null.</returns>
  public static string? Unmet(PgSqueezePrerequisiteFacts facts, string table) =>
    _unmetObject(facts, table) ?? _unmetServer(facts) ?? _unmetRole(facts, table);

  /// <summary>The table and the extension.</summary>
  private static string? _unmetObject(PgSqueezePrerequisiteFacts facts, string table) {
    if (!facts.TableExists) {
      return $"{table} does not exist";
    }
    if (!facts.SqueezeInstalled) {
      return "pg_squeeze is not installed in this database (no squeeze.squeeze_table function)";
    }
    if (!facts.CanExecuteSqueeze) {
      return $"role '{facts.Role}' may not call squeeze.squeeze_table; grant it USAGE on schema squeeze and EXECUTE on the function";
    }
    return null;
  }

  /// <summary>The server's settings.</summary>
  private static string? _unmetServer(PgSqueezePrerequisiteFacts facts) {
    if (facts.ServerVersionNum < LOGICAL_DECODING_ON_DEMAND && !string.Equals(facts.WalLevel, "logical", StringComparison.Ordinal)) {
      return $"wal_level is {facts.WalLevel}, and pg_squeeze needs logical below Postgres 19; set wal_level = logical, then restart the server";
    }
    if (facts.SlotsInUse >= facts.MaxReplicationSlots) {
      return $"all {facts.MaxReplicationSlots} replication slots are in use (max_replication_slots = {facts.MaxReplicationSlots}); "
        + "pg_squeeze needs one free slot for the rewrite";
    }
    return null;
  }

  /// <summary>The table's identity index and the role's rights.</summary>
  private static string? _unmetRole(PgSqueezePrerequisiteFacts facts, string table) {
    if (!facts.HasIdentityIndex) {
      return $"{table} has no primary key or replica identity index, which pg_squeeze needs to apply the changes made during the rewrite";
    }
    if (!facts.CanReplicate) {
      return $"role '{facts.Role}' has neither the REPLICATION attribute nor superuser, and pg_squeeze creates a replication slot "
        + $"as the calling role; grant it REPLICATION (ALTER ROLE {facts.Role} WITH REPLICATION)";
    }
    return facts.OwnsTable
      ? null
      : $"role '{facts.Role}' does not own {table}; configure the schema-initialization (-init) connection so the table owner runs the rewrite";
  }
}
