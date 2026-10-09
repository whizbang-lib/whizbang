// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres.Schema;

/// <summary>What one reconcile did.</summary>
/// <param name="Skipped">True when another session held the schema lock, or the ledger does not exist yet.</param>
/// <param name="Recorded">How many ledger rows were written.</param>
/// <param name="Dropped">The objects dropped.</param>
/// <param name="Kept">The objects that were candidates for dropping but were kept, and why.</param>
/// <param name="Missing">Declared objects the database lacks.</param>
/// <param name="Failed">Drops the database refused, with its message.</param>
public sealed record ManagedSchemaReport(
  bool Skipped, int Recorded, IReadOnlyList<PlannedDrop> Dropped, IReadOnlyList<KeptObject> Kept,
  IReadOnlyList<DeclaredSchemaObject> Missing, IReadOnlyList<(PlannedDrop Drop, string Error)> Failed) {
  /// <summary>A reconcile that did nothing.</summary>
  public static ManagedSchemaReport SkippedReport { get; } = new(true, 0, [], [], [], []);
}

/// <summary>
/// Runs a reconcile against a database: reads the objects on its perspective tables and the ledger, plans with
/// <see cref="ManagedSchemaPlanner"/>, records the ledger, and drops what the plan drops.
/// </summary>
/// <remarks>
/// <para>
/// It runs on a connection of its own under the schema advisory lock, taken with a try so a start that finds
/// another session doing schema work leaves the reconcile to that session (or to the next start) instead of
/// waiting. Index drops use <c>DROP INDEX CONCURRENTLY</c>, which cannot run inside a transaction, so each drop
/// runs on its own and is recorded as retired only once it has succeeded; a refused drop is reported and the
/// object stays pending for the next run.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaReconcilerTests.cs</tests>
public static class ManagedSchemaReconciler {
  /// <summary>Reconciles the perspective tables of one schema.</summary>
  /// <param name="connection">An open connection, outside any transaction, that the caller owns.</param>
  /// <param name="schema">The schema whose perspective tables are reconciled (bare name, e.g. <c>public</c>).</param>
  /// <param name="declared">Everything declared by Whizbang's contributors.</param>
  /// <param name="settings">The mode, kinds to keep, configured pins and the fleet gate.</param>
  /// <param name="lockId">The schema advisory lock id.</param>
  /// <param name="instanceId">This instance, whose declarations are recorded for the fleet gate; null when unknown.</param>
  /// <param name="logger">Where to report what was done.</param>
  /// <param name="cancellationToken">Cancels the reconcile.</param>
  public static async Task<ManagedSchemaReport> RunAsync(
      NpgsqlConnection connection, string schema, ManagedSchemaObjectSet declared,
      ManagedSchemaSettings settings, long lockId, Guid? instanceId, ILogger? logger, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentException.ThrowIfNullOrWhiteSpace(schema);
    ArgumentNullException.ThrowIfNull(declared);
    ArgumentNullException.ThrowIfNull(settings);
    if (settings.Mode == ReconcileMode.Off) {
      return ManagedSchemaReport.SkippedReport;
    }

    if (!await _scalarAsync<bool>(connection, "SELECT pg_try_advisory_lock(@id)", cancellationToken, ("id", lockId))) {
      return ManagedSchemaReport.SkippedReport;
    }

    try {
      var ledgerTable = PgIdentifier.Quote(schema) + ".wh_managed_objects";
      if (await _scalarAsync<string?>(connection, "SELECT to_regclass(@t)::text", cancellationToken, ("t", ledgerTable)) is null) {
        return ManagedSchemaReport.SkippedReport;
      }

      var live = await ManagedSchemaCatalog.ReadLiveAsync(connection, schema, cancellationToken);
      var ledger = await ManagedSchemaCatalog.ReadLedgerAsync(connection, ledgerTable, cancellationToken);
      var fleet = await ManagedSchemaFleet.ReportAndReadAsync(
        connection, schema, instanceId, declared, ManagedSchemaFleet.LiveWindow, cancellationToken);
      var plan = ManagedSchemaPlanner.Plan(declared, live, ledger, settings.ForPass(fleet), DateTimeOffset.UtcNow);

      await ManagedSchemaCatalog.WriteLedgerAsync(connection, ledgerTable, plan.Records, cancellationToken);

      var dropped = new List<PlannedDrop>();
      var failed = new List<(PlannedDrop, string)>();
      foreach (var drop in plan.Drops) {
        try {
          await _executeAsync(connection, ManagedSchemaCatalog.DropStatement(schema, drop), cancellationToken);
          await _executeAsync(connection,
            $"UPDATE {ledgerTable} SET status = '{ManagedStatuses.RETIRED}', retired_at = NOW() WHERE table_name = @t AND object_name = @n",
            cancellationToken, ("t", drop.Table), ("n", drop.Name));
          dropped.Add(drop);
          if (logger?.IsEnabled(LogLevel.Information) == true) {
            ManagedSchemaLog.Dropped(logger, schema, drop.Table, drop.Kind, drop.Name, drop.Reason);
          }
        } catch (PostgresException ex) {
          failed.Add((drop, ex.MessageText));
          if (logger?.IsEnabled(LogLevel.Warning) == true) {
            ManagedSchemaLog.DropFailed(logger, schema, drop.Table, drop.Kind, drop.Name, ex.MessageText);
          }
        }
      }

      if (logger?.IsEnabled(LogLevel.Information) == true) {
        foreach (var keep in plan.Kept) {
          ManagedSchemaLog.Kept(logger, schema, keep.Table, keep.Kind, keep.Name, keep.Reason);
        }
      }
      if (logger?.IsEnabled(LogLevel.Warning) == true) {
        foreach (var missing in plan.Missing) {
          ManagedSchemaLog.Missing(logger, schema, missing.Table, missing.Kind, missing.Name, missing.DeclaredBy);
        }
      }

      return new ManagedSchemaReport(false, plan.Records.Count, dropped, plan.Kept, plan.Missing, failed);
    } finally {
      await _executeAsync(connection, "SELECT pg_advisory_unlock(@id)", CancellationToken.None, ("id", lockId));
    }
  }

  /// <summary>
  /// The perspective tables missing an object the model declares and the ledger has seen: built once, then dropped by
  /// hand. Schema initialization forgets these tables' hashes so its pass builds them again at this start.
  /// </summary>
  /// <remarks>
  /// An object the ledger has never seen is left out: one the server never built (an index in an optional-extension
  /// block it refused) would otherwise send every start through the schema pass under the lock, to fail the same way.
  /// It is reported missing by the reconcile instead. Nothing is named when the schema has no ledger yet.
  /// </remarks>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema, bare.</param>
  /// <param name="declared">What the model declares.</param>
  /// <param name="cancellationToken">Cancels the read.</param>
  public static async Task<IReadOnlyList<string>> TablesMissingDeclaredObjectsAsync(
      NpgsqlConnection connection, string schema, ManagedSchemaObjectSet declared, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(declared);
    var ledgerTable = PgIdentifier.Quote(schema) + ".wh_managed_objects";
    if (await _scalarAsync<string?>(connection, "SELECT to_regclass(@t)::text", cancellationToken, ("t", ledgerTable)) is null) {
      return [];
    }
    var live = (await ManagedSchemaCatalog.ReadLiveAsync(connection, schema, cancellationToken))
      .Select(o => (o.Table, o.Name)).ToHashSet();
    var seen = (await ManagedSchemaCatalog.ReadLedgerAsync(connection, ledgerTable, cancellationToken))
      .Select(r => (r.Table, r.Name)).ToHashSet();
    return [.. declared.Objects
      .Where(o => !live.Contains((o.Table, o.Name)) && seen.Contains((o.Table, o.Name)))
      .Select(o => o.Table)
      .Distinct(StringComparer.Ordinal)
      .Order(StringComparer.Ordinal)];
  }

  private static async Task<T> _scalarAsync<T>(
      NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters) {
    await using var command = new NpgsqlCommand(sql, connection);
    foreach (var (name, value) in parameters) {
      command.Parameters.AddWithValue(name, value);
    }
    var result = await command.ExecuteScalarAsync(cancellationToken);
    return result is null or DBNull ? default! : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
  }

  private static async Task _executeAsync(
      NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters) {
    await using var command = new NpgsqlCommand(sql, connection);
    foreach (var (name, value) in parameters) {
      command.Parameters.AddWithValue(name, value);
    }
    await command.ExecuteNonQueryAsync(cancellationToken);
  }
}

/// <summary>Source-generated logging for the reconcile.</summary>
internal static partial class ManagedSchemaLog {
  [LoggerMessage(Level = LogLevel.Information,
    Message = "Schema reconcile ({Schema}.{Table}): dropped {Kind} {Name} ({Reason})")]
  public static partial void Dropped(ILogger logger, string schema, string table, ManagedObjectKind kind, string name, string reason);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Schema reconcile ({Schema}.{Table}): could not drop {Kind} {Name}; it stays pending for the next run: {Error}")]
  public static partial void DropFailed(ILogger logger, string schema, string table, ManagedObjectKind kind, string name, string error);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Schema reconcile ({Schema}.{Table}): kept {Kind} {Name} ({Reason})")]
  public static partial void Kept(ILogger logger, string schema, string table, ManagedObjectKind kind, string name, string reason);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Schema reconcile ({Schema}.{Table}): declared {Kind} {Name} is missing from the database (declared by {DeclaredBy})")]
  public static partial void Missing(ILogger logger, string schema, string table, ManagedObjectKind kind, string name, string declaredBy);
}
