// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres.Schema;

/// <summary>The storage parameters a perspective model declares for its table, read from the generated schema.</summary>
/// <param name="Table">The bare table name.</param>
/// <param name="Parameters">Each declared governed parameter and its value, as PostgreSQL stores it.</param>
/// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
public sealed record TableStorageDeclaration(string Table, IReadOnlyDictionary<string, string> Parameters);

/// <summary>What one table needs to match its declaration: the values to set and the parameters to reset.</summary>
/// <param name="Table">The bare table name.</param>
/// <param name="Set">Each parameter to set, and its value.</param>
/// <param name="Reset">Each parameter to reset to the server's value.</param>
/// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
public sealed record TableStorageChange(string Table, IReadOnlyDictionary<string, string> Set, IReadOnlyList<string> Reset) {
  /// <summary>Whether the fillfactor is set or reset, which applies only to pages written from then on.</summary>
  public bool TouchesFillFactor =>
    Set.ContainsKey(PerspectiveStorageParameters.FILLFACTOR) || Reset.Contains(PerspectiveStorageParameters.FILLFACTOR);
}

/// <summary>What one storage-parameter reconcile found and did.</summary>
/// <param name="ManualTables">The tables listed in the override setting.</param>
/// <param name="Applied">The changes applied.</param>
/// <param name="Failed">The tables that could not be altered (a lock not had in time, or an error), left for the next start.</param>
/// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
public sealed record PerspectiveStorageReport(
  IReadOnlyList<string> ManualTables, IReadOnlyList<TableStorageChange> Applied, IReadOnlyList<string> Failed) {
  /// <summary>A reconcile that read or did nothing.</summary>
  public static PerspectiveStorageReport Empty { get; } = new([], [], []);
}

/// <summary>
/// Brings each perspective table's fillfactor and autovacuum settings in line with its model at every start (WS-4
/// decision 3, #1747), except on the tables an operator manages by hand.
/// </summary>
/// <remarks>
/// <para>
/// The generated perspective schema declares each table's governed parameters in a marker line
/// (<see cref="MARKER"/>), because that schema runs only when its hash changes, and a parameter removed from the model,
/// a value changed by hand, or a table taken off the override list must be seen without a schema change. The reconcile
/// reads the markers, the catalog (one query for every table and its TOAST relation) and the override setting, and
/// alters a table only where it differs: a start that finds every table in line issues no <c>ALTER TABLE</c> and takes
/// no lock.
/// </para>
/// <para>
/// The model is the authority for the seven governed parameters: a declared value is set, and a governed parameter
/// the model does not declare is reset, including one set by hand, since nothing records who set it. A parameter the
/// attribute cannot set is never touched. A table listed in <see cref="MANUAL_TABLES_SETTING"/> is not touched at all;
/// the start lists those tables once and warns of each declared value it ignores.
/// </para>
/// <para>
/// Each table is altered in one statement, in a transaction of its own under a lock timeout, so a table busy with a
/// long-running lock is skipped with a warning and tried again at the next start rather than holding the start. A
/// changed or reset fillfactor on a table that has pages queues a rewrite request. The reconcile never fails a start.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveStorageParametersTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/JsonbColumnStorageIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperTableStorageResetTests.cs</tests>
public static partial class PerspectiveStorageParameters {
#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  /// <summary>Starts a table's storage marker line; the bare table name and each <c>name=value</c> follow, space-separated.</summary>
  public const string MARKER = "-- @whizbang:table-storage ";

  /// <summary>
  /// The <c>wh_settings</c> key naming the perspective tables whose governed parameters an operator manages by hand:
  /// bare table names, comma-separated.
  /// </summary>
  /// <docs>fundamentals/perspectives/physical-fields#manually-managed-tables</docs>
  public const string MANUAL_TABLES_SETTING = "perspective_storage_manual_tables";
#pragma warning restore CA1707

  internal const string FILLFACTOR = "fillfactor";

  /// <summary>How long one table's <c>ALTER TABLE</c> waits for its lock before the table is left for the next start.</summary>
  public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

  /// <summary>The parameters <c>[PerspectiveTableStorage]</c> governs, in the order the generators declare them.</summary>
  public static IReadOnlyList<string> Governed { get; } = [
    FILLFACTOR,
    "autovacuum_vacuum_threshold",
    "autovacuum_vacuum_scale_factor",
    "autovacuum_vacuum_insert_threshold",
    "autovacuum_vacuum_insert_scale_factor",
    "toast.autovacuum_vacuum_threshold",
    "toast.autovacuum_vacuum_scale_factor",
  ];

  /// <summary>The storage declaration of each table whose schema carries a marker; a later marker for a table wins.</summary>
  /// <param name="perspectiveSql">Each perspective's generated schema SQL.</param>
  /// <returns>One declaration per table.</returns>
  public static IReadOnlyList<TableStorageDeclaration> Declarations(IEnumerable<string> perspectiveSql) {
    ArgumentNullException.ThrowIfNull(perspectiveSql);
    var byTable = new Dictionary<string, TableStorageDeclaration>(StringComparer.Ordinal);
    foreach (var line in perspectiveSql.SelectMany(sql => sql.Split('\n'))) {
      var trimmed = line.Trim();
      if (!trimmed.StartsWith(MARKER, StringComparison.Ordinal)) {
        continue;
      }
      var tokens = trimmed[MARKER.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
      // The line was trimmed and starts with the marker, so a table name follows it.
      if (!_identifier().IsMatch(tokens[0])) {
        continue;
      }
      var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var token in tokens.Skip(1)) {
        var split = token.IndexOf('=', StringComparison.Ordinal);
        // Only a governed name with a numeric value is read: the marker is generated, so anything else was not
        // written by a generator, and nothing else may reach an ALTER TABLE.
        if (split > 0 && Governed.Contains(token[..split]) && _number().IsMatch(token[(split + 1)..])) {
          parameters[token[..split]] = token[(split + 1)..];
        }
      }
      byTable[tokens[0]] = new TableStorageDeclaration(tokens[0], parameters);
    }
    return [.. byTable.Values];
  }

  /// <summary>The tables the override setting names: bare names, comma-separated, spaces and empty entries ignored.</summary>
  /// <param name="setting">The setting's value, or null when it is absent.</param>
  /// <returns>The table names, in the order given.</returns>
  /// <docs>fundamentals/perspectives/physical-fields#manually-managed-tables</docs>
  public static IReadOnlyList<string> ManualTables(string? setting) =>
    setting is null
      ? []
      : [.. setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

  /// <summary>What a table needs to match its declaration, or null when it already does.</summary>
  /// <param name="declared">The table's declaration.</param>
  /// <param name="held">The table's parameters as the catalog holds them, as <c>name=value</c>, a TOAST one prefixed <c>toast.</c>.</param>
  /// <returns>The change, or null.</returns>
  public static TableStorageChange? Plan(TableStorageDeclaration declared, IReadOnlyCollection<string> held) {
    ArgumentNullException.ThrowIfNull(declared);
    ArgumentNullException.ThrowIfNull(held);
    var set = declared.Parameters
      .Where(p => !held.Contains($"{p.Key}={p.Value}"))
      .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    var reset = Governed
      .Where(name => !declared.Parameters.ContainsKey(name) && held.Any(h => h.StartsWith(name + "=", StringComparison.Ordinal)))
      .ToList();
    return set.Count == 0 && reset.Count == 0 ? null : new TableStorageChange(declared.Table, set, reset);
  }

  /// <summary>The one statement that applies a change: <c>ALTER TABLE … SET (…), RESET (…)</c>.</summary>
  /// <param name="qualifiedTable">The table as the statement names it.</param>
  /// <param name="change">The change.</param>
  /// <returns>The statement.</returns>
  public static string AlterStatement(string qualifiedTable, TableStorageChange change) {
    ArgumentNullException.ThrowIfNull(change);
    var actions = new List<string>(2);
    if (change.Set.Count > 0) {
      actions.Add($"SET ({string.Join(", ", change.Set.Select(p => $"{p.Key} = {p.Value}"))})");
    }
    if (change.Reset.Count > 0) {
      actions.Add($"RESET ({string.Join(", ", change.Reset)})");
    }
    return $"ALTER TABLE {qualifiedTable} {string.Join(", ", actions)}";
  }

  /// <summary>
  /// Brings every declared table that is not managed by hand in line with its declaration, and logs the tables that
  /// are. Never throws but for cancellation: a failure is logged as a warning and the start goes on.
  /// </summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema, bare.</param>
  /// <param name="perspectiveSql">Each perspective's generated schema SQL.</param>
  /// <param name="logger">Where the reconcile reports; null for silence.</param>
  /// <param name="cancellationToken">Cancels the reconcile.</param>
  /// <param name="lockTimeout">How long one table's change waits for its lock; <see cref="DefaultLockTimeout"/> when null.</param>
  /// <returns>What the reconcile found and did.</returns>
  public static async Task<PerspectiveStorageReport> ReconcileAsync(
      NpgsqlConnection connection, string schema, IEnumerable<string> perspectiveSql, ILogger? logger,
      CancellationToken cancellationToken, TimeSpan? lockTimeout = null) {
    ArgumentNullException.ThrowIfNull(connection);
    logger ??= Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    var declarations = Declarations(perspectiveSql);
    if (declarations.Count == 0) {
      return PerspectiveStorageReport.Empty;
    }

    try {
      var quotedSchema = PgIdentifier.Quote(schema);
      var held = await _readHeldAsync(connection, schema, declarations, cancellationToken).ConfigureAwait(false);
      var manual = ManualTables(await _readSettingAsync(connection, quotedSchema, cancellationToken).ConfigureAwait(false));
      _reportManual(logger, schema, manual, declarations);

      var applied = new List<TableStorageChange>();
      var failed = new List<string>();
      foreach (var declaration in declarations.Where(d => !manual.Contains(d.Table) && held.ContainsKey(d.Table))) {
        var (options, hasPages) = held[declaration.Table];
        if (Plan(declaration, options) is not { } change) {
          continue;
        }
        if (await _applyAsync(connection, quotedSchema, change, hasPages, lockTimeout ?? DefaultLockTimeout, logger, schema, cancellationToken)
            .ConfigureAwait(false)) {
          applied.Add(change);
        } else {
          failed.Add(change.Table);
        }
      }
      return new PerspectiveStorageReport(manual, applied, failed);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogReconcileFailed(logger, schema, ex.Message);
      return PerspectiveStorageReport.Empty;
    }
  }

  private static void _reportManual(
      ILogger logger, string schema, IReadOnlyList<string> manual, IReadOnlyList<TableStorageDeclaration> declarations) {
    if (manual.Count == 0) {
      return;
    }
    var tables = string.Join(", ", manual);
    LogManualTables(logger, schema, tables, MANUAL_TABLES_SETTING);
    foreach (var declaration in declarations.Where(d => manual.Contains(d.Table) && d.Parameters.Count > 0)) {
      var ignored = string.Join(", ", declaration.Parameters.Select(p => $"{p.Key}={p.Value}"));
      LogDeclaredValuesIgnored(logger, schema, declaration.Table, ignored, MANUAL_TABLES_SETTING);
    }
  }

  // Every declared table's parameters, its TOAST relation's prefixed toast., and whether it has pages: one catalog read.
  private static async Task<Dictionary<string, (List<string> Options, bool HasPages)>> _readHeldAsync(
      NpgsqlConnection connection, string schema, IReadOnlyList<TableStorageDeclaration> declarations,
      CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand("""
      SELECT c.relname, coalesce(c.reloptions, ARRAY[]::text[]),
             coalesce(array(SELECT 'toast.' || o FROM unnest(t.reloptions) o), ARRAY[]::text[]),
             pg_relation_size(c.oid) > 0
      FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
      LEFT JOIN pg_class t ON t.oid = c.reltoastrelid
      WHERE n.nspname = @schema AND c.relkind IN ('r', 'p') AND c.relname = ANY (@tables)
      """, connection);
    command.Parameters.AddWithValue(nameof(schema), schema);
    command.Parameters.AddWithValue("tables", declarations.Select(d => d.Table).ToArray());
    var held = new Dictionary<string, (List<string>, bool)>(StringComparer.Ordinal);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      held[reader.GetString(0)] = ([.. reader.GetFieldValue<string[]>(1), .. reader.GetFieldValue<string[]>(2)], reader.GetBoolean(3));
    }
    return held;
  }

  private static async Task<string?> _readSettingAsync(NpgsqlConnection connection, string quotedSchema, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      $"SELECT setting_value FROM {quotedSchema}.wh_settings WHERE setting_key = @key", connection);
    command.Parameters.AddWithValue("key", MANUAL_TABLES_SETTING);
    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
  }

  // One table's change, in a transaction of its own under the lock timeout; false when the database refused it.
  private static async Task<bool> _applyAsync(
      NpgsqlConnection connection, string quotedSchema, TableStorageChange change, bool hasPages, TimeSpan lockTimeout,
      ILogger logger, string schema, CancellationToken cancellationToken) {
    var statements = new List<string> {
      $"SET LOCAL lock_timeout = '{((long)lockTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)}ms'",
      AlterStatement($"{quotedSchema}.{PgIdentifier.Quote(change.Table)}", change),
    };
    // The rewrite request names the bare table, as the rewriter lists the tables it measures. The name matched
    // _identifier when the marker was read, so it holds no quote.
    var queuesRewrite = change.TouchesFillFactor && hasPages;
    if (queuesRewrite) {
      statements.Add($"SELECT {quotedSchema}.wh_request_table_rewrite('{change.Table}')");
    }

    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    try {
      foreach (var sql in statements) {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      }
      await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    } catch (PostgresException ex) {
      await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
      LogTableFailed(logger, schema, change.Table, ex.MessageText);
      return false;
    }

    var set = change.Set.Count == 0 ? "nothing" : string.Join(", ", change.Set.Select(p => $"{p.Key}={p.Value}"));
    var reset = change.Reset.Count == 0 ? "nothing" : string.Join(", ", change.Reset);
    LogTableApplied(logger, schema, change.Table, set, reset, queuesRewrite);
    return true;
  }

  [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
  private static partial Regex _identifier();

  [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?([Ee][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
  private static partial Regex _number();

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Perspective storage ({Schema}): {Tables} are managed by hand through the {Setting} setting; their fillfactor and autovacuum settings are left as they are")]
  private static partial void LogManualTables(ILogger logger, string schema, string tables, string setting);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Perspective storage ({Schema}.{Table}): the model declares {Parameters}, which are not applied because the table is listed in the {Setting} setting")]
  private static partial void LogDeclaredValuesIgnored(ILogger logger, string schema, string table, string parameters, string setting);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Perspective storage ({Schema}.{Table}): brought in line with the model; set {Set}; reset {Reset}; rewrite queued: {RewriteQueued}")]
  private static partial void LogTableApplied(ILogger logger, string schema, string table, string set, string reset, bool rewriteQueued);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Perspective storage ({Schema}.{Table}): could not apply the model's storage settings; the next start tries again: {Error}")]
  private static partial void LogTableFailed(ILogger logger, string schema, string table, string error);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Perspective storage ({Schema}): the storage settings were not reconciled; the next start tries again: {Error}")]
  private static partial void LogReconcileFailed(ILogger logger, string schema, string error);
}
