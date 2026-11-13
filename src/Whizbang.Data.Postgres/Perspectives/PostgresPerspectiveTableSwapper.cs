// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>
/// The PostgreSQL <see cref="IPerspectiveTableSwapper"/>, shared by the EF Core and Dapper drivers.
/// </summary>
/// <remarks>
/// <para>
/// The shadow table is <c>CREATE TABLE … (LIKE live INCLUDING ALL)</c>: the same columns, defaults, constraints
/// and indexes, which the replay's upsert needs. <c>LIKE</c> copies no storage parameters, so the shadow is then given
/// the live table's own (its fillfactor and autovacuum settings, and its TOAST relation's) before the replay writes a
/// page: the rebuilt table is filled at the fillfactor the live one declares (#2299). The swap runs in one transaction on a connection of its own. It
/// takes <c>EXCLUSIVE</c> on the live table, which stops writers and lets readers through, runs the final
/// catch-up, then takes <c>ACCESS EXCLUSIVE</c> on both tables and renames: the live table to
/// <see cref="PreviousName"/> (or drops it), the shadow table to the live name, and each index to the name the live
/// table's matching index had, so the schema pass that creates indexes by name finds them in place. A writer that
/// was waiting continues against the new table once the transaction commits, because PostgreSQL resolves a
/// relation's name again after a lock wait.
/// </para>
/// <para>
/// Every lock wait in the swap is bounded by the swap's lock timeout; a swap that times out rolls back and the
/// live table is left as it was. The installed table is then analyzed, outside the swap's transaction.
/// </para>
/// <para>
/// Only one blue-green rebuild of a table runs at a time, fleet-wide: <see cref="TryLockRebuildAsync"/> takes a session
/// advisory lock keyed on the schema and the table, which the rebuilder holds from before the shadow is created until
/// the swap is done (#2301).
/// </para>
/// </remarks>
/// <param name="openConnection">Opens a connection of the swapper's own; the swapper disposes it.</param>
/// <param name="schema">The schema the perspective tables are in, or null for the connection's search path.</param>
/// <docs>fundamentals/perspectives/rebuild#blue-green</docs>
/// <docs>fundamentals/perspectives/rebuild#blue-green-storage-settings</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/PostgresPerspectiveTableSwapperTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/JsonbColumnStorageIntegrationTests.cs:ABlueGreenSwap_KeepsTheDeclaredStorageSettingsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/JsonbColumnStorageIntegrationTests.cs:ABlueGreenSwap_OfAManuallyManagedTable_KeepsItsLiveSettingsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/BlueGreenRebuildIntegrationTests.cs</tests>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
  Justification = "Only quoted identifiers are interpolated: table and index names from the perspective registry and pg_catalog. Values are parameters.")]
[SuppressMessage("csharpsquid", "S2077:Formatting SQL queries is security-sensitive",
  Justification = "Only quoted identifiers are interpolated: table and index names from the perspective registry and pg_catalog. Values are parameters.")]
public sealed class PostgresPerspectiveTableSwapper(
    Func<CancellationToken, Task<NpgsqlConnection>> openConnection, string? schema) : IPerspectiveTableSwapper {

  private const string SHADOW_SUFFIX = "_bg";
  private const string PREVIOUS_SUFFIX = "_bg_old";
  private const int MAX_IDENTIFIER_LENGTH = 63;
  // The rebuild guard's key family (#2301): never change it, since instances of different versions must agree on it.
  private const string REBUILD_GUARD_PREFIX = "whizbang:blue-green-rebuild:";

  private readonly Func<CancellationToken, Task<NpgsqlConnection>> _openConnection =
    openConnection ?? throw new ArgumentNullException(nameof(openConnection));

  /// <summary>The name of the shadow table a rebuild of <paramref name="table"/> builds.</summary>
  public static string ShadowName(string table) => _suffixed(table, SHADOW_SUFFIX);

  /// <summary>The name a replaced table, or one of its indexes, is kept under.</summary>
  public static string PreviousName(string name) => _suffixed(name, PREVIOUS_SUFFIX);

  /// <inheritdoc />
  public async Task<string?> FindTableAsync(string modelTypeName, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelTypeName);

    // Two renderings of one name reach this method. The schema pass writes the registry key in CLR
    // form, with '+' before a nested type; the runner registry reports a model as the generated
    // fully-qualified form, 'global::'-prefixed and dotted throughout. Neither side is wrong, so the
    // match is made on the dotted normalization of both, which is the same reconciliation
    // CollectiveReplayApplier already makes for the same pair of forms. The exact comparison stays
    // first so a caller holding the registry key still hits the index.
    var key = modelTypeName.StartsWith("global::", StringComparison.Ordinal)
      ? modelTypeName["global::".Length..]
      : modelTypeName;

    await using var conn = await _openConnection(cancellationToken).ConfigureAwait(false);
    await using var cmd = new NpgsqlCommand(
      $"SELECT table_name FROM {PgIdentifier.Qualify(schema, "wh_perspective_registry")} " +
      "WHERE clr_type_name = @name OR replace(clr_type_name, '+', '.') = @dotted LIMIT 1", conn);
    cmd.Parameters.AddWithValue("name", key);
    cmd.Parameters.AddWithValue("dotted", key.Replace('+', '.'));
    return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
  }

  /// <inheritdoc />
  public async Task<string> CreateShadowAsync(string liveTable, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(liveTable);
    var shadow = ShadowName(liveTable);
    await using var conn = await _openConnection(cancellationToken).ConfigureAwait(false);
    await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    await _executeAsync(conn, tx,
      $"DROP TABLE IF EXISTS {_table(shadow)}; CREATE TABLE {_table(shadow)} (LIKE {_table(liveTable)} INCLUDING ALL);",
      cancellationToken).ConfigureAwait(false);
    // LIKE copies no storage parameters (#2299). The shadow takes the live table's own before the replay writes a page,
    // so the rebuilt table is filled at the live fillfactor. What is live is copied, not what the model declares, so a
    // table an operator manages by hand keeps the values it holds.
    await using (var copy = new NpgsqlCommand(COPY_STORAGE_SETTINGS, conn, tx)) {
      copy.Parameters.AddWithValue("live", _table(liveTable));
      copy.Parameters.AddWithValue(nameof(shadow), _table(shadow));
      if (await copy.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string alter) {
        await _executeAsync(conn, tx, alter, cancellationToken).ConfigureAwait(false);
      }
    }
    await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    return shadow;
  }

  /// <summary>
  /// The <c>ALTER TABLE shadow SET (…)</c> that gives the shadow the live table's storage parameters and its TOAST
  /// relation's (prefixed <c>toast.</c>), or null when the live table holds none. PostgreSQL builds it with
  /// <c>format</c>: each option name is quoted as an identifier and each value as a literal, and the table names are
  /// resolved through <c>regclass</c>, so nothing read from the catalog reaches the statement unquoted.
  /// </summary>
  private const string COPY_STORAGE_SETTINGS = """
    SELECT 'ALTER TABLE ' || @shadow::regclass::text || ' SET (' || string_agg(
             format('%s%I = %L', o.prefix, split_part(o.option, '=', 1), substr(o.option, strpos(o.option, '=') + 1)),
             ', ' ORDER BY o.prefix, o.position) || ')'
    FROM pg_class c
    LEFT JOIN pg_class t ON t.oid = c.reltoastrelid
    CROSS JOIN LATERAL (
      SELECT '' AS prefix, u.option, u.position FROM unnest(c.reloptions) WITH ORDINALITY u(option, position)
      UNION ALL
      SELECT 'toast.', u.option, u.position FROM unnest(t.reloptions) WITH ORDINALITY u(option, position)
    ) o
    WHERE c.oid = @live::regclass
    HAVING count(*) > 0
    """;

  /// <inheritdoc />
  public async Task DeleteRowsAsync(string table, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    ArgumentNullException.ThrowIfNull(ids);
    await using var conn = await _openConnection(cancellationToken).ConfigureAwait(false);
    await using var cmd = new NpgsqlCommand($"DELETE FROM {_table(table)} WHERE id = ANY(@ids)", conn);
    cmd.Parameters.AddWithValue(nameof(ids), ids.ToArray());
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task DropAsync(string table, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    await using var conn = await _openConnection(cancellationToken).ConfigureAwait(false);
    await using var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS {_table(table)}", conn);
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<string?> SwapAsync(
      PerspectiveTableSwap swap, Func<CancellationToken, Task> underWriteLock, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(swap);
    ArgumentNullException.ThrowIfNull(underWriteLock);
    await using var conn = await _openConnection(cancellationToken).ConfigureAwait(false);
    await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

    var timeoutMs = ((long)swap.LockTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
    await _executeAsync(conn, tx, "SELECT set_config('lock_timeout', @ms, true)", cancellationToken, ("ms", timeoutMs)).ConfigureAwait(false);
    // Writers wait from here; readers do not.
    await _executeAsync(conn, tx, $"LOCK TABLE {_table(swap.LiveTable)} IN EXCLUSIVE MODE", cancellationToken).ConfigureAwait(false);

    await underWriteLock(cancellationToken).ConfigureAwait(false);

    await _executeAsync(conn, tx,
      $"LOCK TABLE {_table(swap.LiveTable)}, {_table(swap.ShadowTable)} IN ACCESS EXCLUSIVE MODE", cancellationToken).ConfigureAwait(false);
    var (liveIndexes, renames) = await _indexesAsync(conn, tx, swap, cancellationToken).ConfigureAwait(false);

    string? previous = null;
    if (swap.KeepPrevious) {
      previous = PreviousName(swap.LiveTable);
      await _executeAsync(conn, tx, $"DROP TABLE IF EXISTS {_table(previous)}", cancellationToken).ConfigureAwait(false);
      // Every name moves with the previous table, so an index created on the live table while the rebuild ran
      // (and so missing from the shadow) leaves its name free for the schema pass to create it on the new table.
      foreach (var live in liveIndexes) {
        await _executeAsync(conn, tx, $"ALTER INDEX {_table(live)} RENAME TO {PgIdentifier.Quote(PreviousName(live))}", cancellationToken).ConfigureAwait(false);
      }
      await _executeAsync(conn, tx, $"ALTER TABLE {_table(swap.LiveTable)} RENAME TO {PgIdentifier.Quote(previous)}", cancellationToken).ConfigureAwait(false);
    } else {
      await _executeAsync(conn, tx, $"DROP TABLE {_table(swap.LiveTable)}", cancellationToken).ConfigureAwait(false);
    }
    foreach (var (live, shadow) in renames) {
      await _executeAsync(conn, tx, $"ALTER INDEX {_table(shadow)} RENAME TO {PgIdentifier.Quote(live)}", cancellationToken).ConfigureAwait(false);
    }
    await _executeAsync(conn, tx, $"ALTER TABLE {_table(swap.ShadowTable)} RENAME TO {PgIdentifier.Quote(swap.LiveTable)}", cancellationToken).ConfigureAwait(false);

    await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

    // A table just created reads as never analyzed: the planner guesses its size, and a rewrite's re-measure divides by
    // a row count it does not have (#2301). ANALYZE samples it, outside the swap's transaction and its locks.
    await using (var analyze = new NpgsqlCommand($"ANALYZE {_table(swap.LiveTable)}", conn)) {
      await analyze.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    return previous;
  }

  /// <inheritdoc />
  /// <remarks>
  /// A session advisory lock, taken with a try on a connection of the guard's own and held until the guard is disposed,
  /// which unlocks it before the connection goes back to the pool. Its key is the process-stable hash of the schema (the
  /// connection's current one when none is configured) and the table, so every instance of every version computes the
  /// same key for the same table.
  /// </remarks>
  public async Task<IAsyncDisposable?> TryLockRebuildAsync(string liveTable, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(liveTable);
    NpgsqlConnection? owned = await _openConnection(cancellationToken).ConfigureAwait(false);
    try {
      await using var resolve = new NpgsqlCommand("SELECT COALESCE(@schema, current_schema())", owned);
      resolve.Parameters.AddWithValue(nameof(schema), (object?)schema ?? DBNull.Value).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
      var key = FnvHash64.Compute(REBUILD_GUARD_PREFIX + (string)(await resolve.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!
        + "." + liveTable);
      await using var take = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", owned);
      take.Parameters.AddWithValue(nameof(key), key);
      if (!(bool)(await take.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!) {
        return null;
      }
      var guard = new RebuildGuard(owned, key);
      owned = null;
      return guard;
    } finally {
      if (owned is not null) {
        await owned.DisposeAsync().ConfigureAwait(false);
      }
    }
  }

  /// <inheritdoc />
  /// <remarks>Every perspective the registry records as writing the table; one is the answer, none or several is null.</remarks>
  public async Task<string?> FindPerspectiveAsync(string table, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    await using var conn = await _openConnection(cancellationToken).ConfigureAwait(false);
    await using var cmd = new NpgsqlCommand(
      "SELECT CASE WHEN count(DISTINCT p.name) = 1 THEN min(p.name) END " +
      $"FROM {PgIdentifier.Qualify(schema, "wh_perspective_registry")} r CROSS JOIN LATERAL unnest(r.perspective_names) p(name) " +
      "WHERE r.table_name = @table", conn);
    cmd.Parameters.AddWithValue(nameof(table), table);
    return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
  }

  /// <summary>Holds a table's rebuild guard: the advisory lock and the session it was taken on.</summary>
  private sealed class RebuildGuard(NpgsqlConnection connection, long key) : IAsyncDisposable {
    private bool _released;

    public async ValueTask DisposeAsync() {
      if (_released) {
        return;
      }
      _released = true;
      await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection)) {
        release.Parameters.AddWithValue(nameof(key), key);
        await release.ExecuteNonQueryAsync().ConfigureAwait(false);
      }
      await connection.DisposeAsync().ConfigureAwait(false);
    }
  }

  /// <summary>
  /// The live table's indexes, and each paired with the shadow index <c>LIKE … INCLUDING ALL</c> copied from it: the
  /// same uniqueness and the same definition after its name and table. The shadow index takes the live index's name.
  /// </summary>
  private async Task<(List<string> LiveIndexes, List<(string Live, string Shadow)> Renames)> _indexesAsync(
      NpgsqlConnection conn, NpgsqlTransaction tx, PerspectiveTableSwap swap, CancellationToken cancellationToken) {
    await using var cmd = new NpgsqlCommand("""
      SELECT t.relname, i.relname, x.indisunique, substring(pg_get_indexdef(i.oid) from ' USING .*$')
      FROM pg_index x
      JOIN pg_class i ON i.oid = x.indexrelid
      JOIN pg_class t ON t.oid = x.indrelid
      JOIN pg_namespace n ON n.oid = t.relnamespace
      WHERE n.nspname = COALESCE(@schema, current_schema()) AND t.relname = ANY(@tables)
      ORDER BY i.relname
      """, conn, tx);
    cmd.Parameters.AddWithValue("schema", (object?)schema ?? DBNull.Value).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
    cmd.Parameters.AddWithValue("tables", new[] { swap.LiveTable, swap.ShadowTable });
    var live = new List<(string Name, string Shape)>();
    var shadow = new List<(string Name, string Shape)>();
    await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var index = (reader.GetString(1), reader.GetBoolean(2) + reader.GetString(3));
        (reader.GetString(0) == swap.LiveTable ? live : shadow).Add(index);
      }
    }
    var renames = new List<(string Live, string Shadow)>(live.Count);
    foreach (var (name, shape) in live) {
      var match = shadow.FindIndex(s => s.Shape == shape);
      if (match >= 0) {
        renames.Add((name, shadow[match].Name));
        shadow.RemoveAt(match);
      }
    }
    return ([.. live.Select(l => l.Name)], renames);
  }

  private static async Task _executeAsync(
      NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken cancellationToken,
      params (string Name, object Value)[] parameters) {
    await using var cmd = new NpgsqlCommand(sql, conn, tx);
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  private string _table(string name) => PgIdentifier.QualifyPrefix(schema) + PgIdentifier.Quote(name);

  /// <summary>
  /// <paramref name="name"/> with <paramref name="suffix"/>, inside PostgreSQL's 63-byte identifier limit: a name
  /// too long keeps its start and a hash of the whole, so two long names that share a start stay distinct.
  /// </summary>
  private static string _suffixed(string name, string suffix) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    if (Encoding.UTF8.GetByteCount(name) + suffix.Length <= MAX_IDENTIFIER_LENGTH) {
      return name + suffix;
    }
    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
    var keep = MAX_IDENTIFIER_LENGTH - suffix.Length - hash.Length - 1;
    return name[..keep] + "_" + hash + suffix;
  }
}
