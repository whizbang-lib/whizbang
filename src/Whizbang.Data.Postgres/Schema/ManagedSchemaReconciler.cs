// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// It runs under the schema advisory lock, taken with a try so a start that finds another session doing schema work
/// leaves the reconcile to that session (or to the next start) instead of waiting. The lock is transaction-scoped
/// wherever a transaction is possible: the plan and its ledger rows commit together, and every drop but a concurrent
/// index drop is a transaction of its own, so a transaction-pooling pooler cannot strand the lock on a backend the
/// reconcile no longer reaches (#1639). Index drops use <c>DROP INDEX CONCURRENTLY</c>, which cannot run inside a
/// transaction, so each holds the session-level lock for its own statements, and only on a connection that reaches one
/// backend; behind a pooler they stay pending, with a warning. A drop is recorded as retired only once it has succeeded;
/// a refused drop is reported, and a drop that finds the lock taken stays pending for the next run.
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
  /// <param name="instanceId">This instance, whose declarations are recorded for the fleet gate; null when unknown.</param>
  /// <param name="logger">Where to report what was done.</param>
  /// <param name="cancellationToken">Cancels the reconcile.</param>
  /// <remarks>Takes the schema initialization lock (<see cref="SchemaInitializationLockKey"/>) with a try.</remarks>
  public static async Task<ManagedSchemaReport> RunAsync(
      NpgsqlConnection connection, string schema, ManagedSchemaObjectSet declared,
      ManagedSchemaSettings settings, Guid? instanceId, ILogger? logger, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    return await RunAsync(new ConnectionReconcileBackend(connection), schema, declared, settings, instanceId, logger, cancellationToken);
  }

  /// <summary>Reconciles the perspective tables of one schema through <paramref name="backend"/>.</summary>
  /// <param name="backend">Where the statements run.</param>
  /// <param name="schema">The schema whose perspective tables are reconciled (bare name, e.g. <c>public</c>).</param>
  /// <param name="declared">Everything declared by Whizbang's contributors.</param>
  /// <param name="settings">The mode, kinds to keep, configured pins and the fleet gate.</param>
  /// <param name="instanceId">This instance, whose declarations are recorded for the fleet gate; null when unknown.</param>
  /// <param name="logger">Where to report what was done.</param>
  /// <param name="cancellationToken">Cancels the reconcile.</param>
  /// <remarks>
  /// The plan, its ledger rows, and every drop but a concurrent index drop each run in a transaction of their own,
  /// under the schema lock taken with <c>pg_try_advisory_xact_lock</c>, which the commit or the rollback releases on
  /// the backend that took it. A concurrent index drop cannot run in a transaction, so it holds the session-level lock
  /// for its own three statements, and only on a connection that reaches one backend; behind a pooler it stays pending.
  /// </remarks>
  internal static async Task<ManagedSchemaReport> RunAsync(
      IReconcileBackend backend, string schema, ManagedSchemaObjectSet declared,
      ManagedSchemaSettings settings, Guid? instanceId, ILogger? logger, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(schema);
    ArgumentNullException.ThrowIfNull(declared);
    ArgumentNullException.ThrowIfNull(settings);
    if (settings.Mode == ReconcileMode.Off) {
      return ManagedSchemaReport.SkippedReport;
    }

    var target = new DropTarget(backend, schema, PgIdentifier.Quote(schema) + ".wh_managed_objects",
      SchemaInitializationLockKey.Compute(schema), logger ?? NullLogger.Instance);
    var plan = await backend.InTransactionAsync<ReconcilePlan?>(async (connection, token) => {
      if (!await _tryTransactionLockAsync(connection, target.LockId, token)
          || await _scalarAsync<string?>(connection, "SELECT to_regclass(@t)::text", token, ("t", target.LedgerTable)) is null) {
        return null;
      }

      var live = await ManagedSchemaCatalog.ReadLiveAsync(connection, schema, token);
      var effective = await _withEquivalentsAsync(connection, schema, declared, live, token);
      var ledger = await ManagedSchemaCatalog.ReadLedgerAsync(connection, target.LedgerTable, token);
      var fleet = await ManagedSchemaFleet.ReportAndReadAsync(
        connection, schema, instanceId, effective, ManagedSchemaFleet.LiveWindow, token);
      var planned = ManagedSchemaPlanner.Plan(effective, live, ledger, settings.ForPass(fleet), DateTimeOffset.UtcNow);
      await ManagedSchemaCatalog.WriteLedgerAsync(connection, target.LedgerTable, planned.Records, token);
      return planned;
    }, cancellationToken);
    if (plan is null) {
      return ManagedSchemaReport.SkippedReport;
    }

    var (dropped, failed) = await _dropAsync(target, plan.Drops, cancellationToken);
    _logPlan(logger, schema, plan);
    return new ManagedSchemaReport(false, plan.Records.Count, dropped, plan.Kept, plan.Missing, failed);
  }

  /// <summary>Where the drops go: the backend, the schema, its ledger, the schema lock's key, and the log.</summary>
  private sealed record DropTarget(IReconcileBackend Backend, string Schema, string LedgerTable, long LockId, ILogger Logger);

  /// <summary>How one drop ended.</summary>
  private enum DropOutcome {
    /// <summary>Dropped and recorded as retired.</summary>
    Dropped,
    /// <summary>Another session held the schema lock; the object stays pending.</summary>
    LockHeld,
    /// <summary>A concurrent index drop, behind a pooler; the object stays pending.</summary>
    NeedsDirectConnection,
  }

  /// <summary>
  /// Drops each planned object in a step of its own and records it as retired once the drop succeeded; a drop the
  /// database refuses is reported, and a drop that could not take the schema lock is left, both pending for the next
  /// run.
  /// </summary>
  private static async Task<(List<PlannedDrop> Dropped, List<(PlannedDrop, string)> Failed)> _dropAsync(
      DropTarget target, IReadOnlyList<PlannedDrop> drops, CancellationToken cancellationToken) {
    var dropped = new List<PlannedDrop>();
    var failed = new List<(PlannedDrop, string)>();
    var lockHeld = new List<string>();
    var needsDirect = new List<string>();
    var direct = drops.Any(d => d.Kind == ManagedObjectKind.Index) && await target.Backend.IsDirectAsync(cancellationToken);
    foreach (var drop in drops) {
      try {
        switch (await _dropOneAsync(target, drop, direct, cancellationToken)) {
          case DropOutcome.Dropped:
            dropped.Add(drop);
            ManagedSchemaLog.Dropped(target.Logger, target.Schema, drop.Table, drop.Kind, drop.Name, drop.Reason);
            break;
          case DropOutcome.LockHeld:
            lockHeld.Add($"{drop.Table}.{drop.Name}");
            break;
          default:
            needsDirect.Add($"{drop.Table}.{drop.Name}");
            break;
        }
      } catch (PostgresException ex) {
        failed.Add((drop, ex.MessageText));
        ManagedSchemaLog.DropFailed(target.Logger, target.Schema, drop.Table, drop.Kind, drop.Name, ex.MessageText);
      }
    }
    if (lockHeld.Count > 0) {
      ManagedSchemaLog.DropsWaitForTheLock(target.Logger, target.Schema, lockHeld);
    }
    if (needsDirect.Count > 0) {
      ManagedSchemaLog.IndexDropsNeedADirectConnection(target.Logger, target.Schema, needsDirect);
    }
    return (dropped, failed);
  }

  private static Task<DropOutcome> _dropOneAsync(DropTarget target, PlannedDrop drop, bool direct, CancellationToken cancellationToken) {
    if (drop.Kind != ManagedObjectKind.Index) {
      return target.Backend.InTransactionAsync(async (connection, token) => {
        if (!await _tryTransactionLockAsync(connection, target.LockId, token)) {
          return DropOutcome.LockHeld;
        }
        await _executeAsync(connection, ManagedSchemaCatalog.DropStatement(target.Schema, drop), token);
        await _retireAsync(connection, target.LedgerTable, drop, token);
        return DropOutcome.Dropped;
      }, cancellationToken);
    }
    return direct
      ? _dropConcurrentlyAsync(target, drop, cancellationToken)
      : Task.FromResult(DropOutcome.NeedsDirectConnection);
  }

  /// <summary>
  /// A concurrent index drop, which cannot run in a transaction, under the session-level schema lock. Only reached on a
  /// connection whose statements all reach one backend, so the release finds the lock where it was taken; a release
  /// that does not is logged.
  /// </summary>
  private static async Task<DropOutcome> _dropConcurrentlyAsync(DropTarget target, PlannedDrop drop, CancellationToken cancellationToken) {
    var backend = target.Backend;
    if (!await backend.StatementAsync((c, t) => _scalarAsync<bool>(c, "SELECT pg_try_advisory_lock(@id)", t, ("id", target.LockId)), cancellationToken)) {
      return DropOutcome.LockHeld;
    }
    try {
      await backend.StatementAsync((c, t) => _executeAsync(c, ManagedSchemaCatalog.DropStatement(target.Schema, drop), t), cancellationToken);
      await backend.StatementAsync((c, t) => _retireAsync(c, target.LedgerTable, drop, t), cancellationToken);
      return DropOutcome.Dropped;
    } finally {
      await backend.StatementAsync((c, _) => AdvisoryLockRelease.ReleaseAsync(
        c, target.LockId, $"schema initialization lock for schema {target.Schema}", target.Logger, CancellationToken.None), CancellationToken.None);
    }
  }

  private static Task<int> _retireAsync(NpgsqlConnection connection, string ledgerTable, PlannedDrop drop, CancellationToken cancellationToken) =>
    _executeAsync(connection,
      $"UPDATE {ledgerTable} SET status = '{ManagedStatuses.RETIRED}', retired_at = NOW() WHERE table_name = @t AND object_name = @n",
      cancellationToken, ("t", drop.Table), ("n", drop.Name));

  private static Task<bool> _tryTransactionLockAsync(NpgsqlConnection connection, long lockId, CancellationToken cancellationToken) =>
    _scalarAsync<bool>(connection, "SELECT pg_try_advisory_xact_lock(@id)", cancellationToken, ("id", lockId));

  /// <summary>
  /// The declarations with each declared index that stands under another name (an equivalent
  /// <c>wh_ensure_index</c> found and did not duplicate) declared under the name the table has.
  /// </summary>
  private static async Task<ManagedSchemaObjectSet> _withEquivalentsAsync(
      NpgsqlConnection connection, string schema, ManagedSchemaObjectSet declared, List<LiveSchemaObject> live,
      CancellationToken cancellationToken) {
    var equivalents = await ManagedSchemaCatalog.ReadEquivalentsAsync(connection, schema, cancellationToken);
    return declared.WithEquivalents(equivalents, live.Select(o => (o.Table, o.Name)).ToHashSet());
  }

  private static void _logPlan(ILogger? logger, string schema, ReconcilePlan plan) {
    if (logger is null) {
      return;
    }
    foreach (var keep in plan.Kept) {
      ManagedSchemaLog.Kept(logger, schema, keep.Table, keep.Kind, keep.Name, keep.Reason);
    }
    foreach (var missing in plan.Missing) {
      ManagedSchemaLog.Missing(logger, schema, missing.Table, missing.Kind, missing.Name, missing.DeclaredBy);
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
    var liveObjects = await ManagedSchemaCatalog.ReadLiveAsync(connection, schema, cancellationToken);
    var effective = await _withEquivalentsAsync(connection, schema, declared, liveObjects, cancellationToken);
    var live = liveObjects.Select(o => (o.Table, o.Name)).ToHashSet();
    var seen = (await ManagedSchemaCatalog.ReadLedgerAsync(connection, ledgerTable, cancellationToken))
      .Select(r => (r.Table, r.Name)).ToHashSet();
    return [.. effective.Objects
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

  private static async Task<int> _executeAsync(
      NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters) {
    await using var command = new NpgsqlCommand(sql, connection);
    foreach (var (name, value) in parameters) {
      command.Parameters.AddWithValue(name, value);
    }
    return await command.ExecuteNonQueryAsync(cancellationToken);
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

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Schema reconcile ({Schema}): another session took the schema lock after the plan, so these drops stay pending for the next run: {Objects}")]
  public static partial void DropsWaitForTheLock(ILogger logger, string schema, IReadOnlyList<string> objects);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Schema reconcile ({Schema}): these index drops stay pending: {Objects}. A concurrent index drop runs outside a transaction "
      + "and needs the schema lock held on one backend, and this connection goes through a pooler. Configure a direct (non-pooled) "
      + "initialization connection to drop them")]
  public static partial void IndexDropsNeedADirectConnection(ILogger logger, string schema, IReadOnlyList<string> objects);
}
