// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Whether the stored perspective registry says what this build declares, and the reconcile when it does not (#2561):
/// what every start's converge step runs on either driver. No hash covers the registry, so a start that skips the pass
/// (a hash-clean restart, a schema Assess found current, a waiter whose migrator may have been another build, a restored
/// database) would otherwise keep whatever registry it found.
/// </summary>
/// <remarks>
/// <para>
/// The check reads, with no lock, exactly what <c>reconcile_perspective_registry</c> writes for each payload entry
/// (the table name, the schema JSON, the schema hash, and the perspectives that fold the model when the payload names
/// any), so a registry it reads as current is one the reconcile would only re-stamp. Read from the table itself, which
/// a statement locks as it resolves it, so the recheck under the lock sees a commit the waiter waited for (Trap 4).
/// </para>
/// <para>
/// Only a registry that differs is reconciled, under the schema initialization lock the schema pass's own reconcile
/// runs under, in one transaction, after checking again once the lock is held: a waiter that waited for another
/// waiter's reconcile finds nothing to do. Never fatal, as the reconcile is on the EF Core driver: a registry that
/// cannot be read or reconciled is said at Warning, and the next start tries again.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMigratorElectionTests.cs</tests>
public static partial class PerspectiveRegistryCurrency {
  /// <summary>
  /// Reconciles the stored registry from <paramref name="payloads"/> when, and only when, it differs from them.
  /// </summary>
  /// <param name="connectionFactory">Opens a connection of its own.</param>
  /// <param name="schema">The schema, unquoted; its schema initialization lock is the one taken.</param>
  /// <param name="payloads">This build's registry payloads.</param>
  /// <param name="commandTimeoutSeconds">The budget for one statement, and for the wait on the lock.</param>
  /// <param name="services">Where the observers are registered; told when the lock is held elsewhere.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Whether this call reconciled the registry.</returns>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMigratorElectionTests.cs:AWaiterBehindASameBuildMigrator_DoesNoRegistryReconcileAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMigratorElectionTests.cs:AWaiterBehindAMigratorWithAnotherRegistry_ReconcilesItAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMigratorElectionTests.cs:AWaiterThatWaitsForAnothersReconcile_ChecksAgainUnderTheLock_AndDoesNothingAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMigratorElectionTests.cs:ARegistryThatCannotBeReconciled_IsNotFatalAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/PerspectiveRegistryRestartTests.cs</tests>
  public static async Task<bool> ReconcileIfStaleAsync(
      Func<NpgsqlConnection> connectionFactory, string schema, IReadOnlyList<PerspectiveRegistryPayload> payloads,
      int commandTimeoutSeconds, IServiceProvider? services, ILogger? logger, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(payloads);
    var lockId = SchemaInitializationLockKey.Compute(schema);
    var log = logger ?? NullLogger.Instance;
    try {
      await using var connection = connectionFactory();
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
      if (await _isCurrentAsync(connection, schema, payloads, commandTimeoutSeconds, null, cancellationToken).ConfigureAwait(false)) {
        return false;
      }

      await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
        .ConfigureAwait(false);
      if (!await _tryLockAsync(connection, transaction, lockId, cancellationToken).ConfigureAwait(false)) {
        await SchemaInitializationObservers.LockContendedAsync(services, schema, cancellationToken).ConfigureAwait(false);
        await _lockAsync(connection, transaction, lockId, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (await _isCurrentAsync(connection, schema, payloads, commandTimeoutSeconds, transaction, cancellationToken)
            .ConfigureAwait(false)) {
          return false;   // another instance reconciled it while this one waited; disposing rolls back, releasing the lock
        }
      }

      foreach (var payload in payloads) {
#pragma warning disable S2077 // The schema cannot be a bind parameter; PgIdentifier.Qualify quotes it. Every value is bound.
        await using var reconcile = new NpgsqlCommand(
          $"SELECT count(*) FROM {PgIdentifier.Qualify(schema, "reconcile_perspective_registry")}($1::jsonb, $2)",
          connection, transaction) { CommandTimeout = commandTimeoutSeconds };
#pragma warning restore S2077
        reconcile.Parameters.AddWithValue(payload.Json);
        reconcile.Parameters.AddWithValue(payload.ServiceName);
        await reconcile.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      }
      await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
      LogReconciled(log, schema);
      return true;
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogFailed(log, ex, schema);
      return false;
    }
  }

  /// <summary>Whether every payload entry is stored as the reconcile would write it.</summary>
  private static async Task<bool> _isCurrentAsync(
      NpgsqlConnection connection, string schema, IReadOnlyList<PerspectiveRegistryPayload> payloads, int commandTimeoutSeconds,
      NpgsqlTransaction? transaction, CancellationToken cancellationToken) {
    var registry = PgIdentifier.Qualify(schema, "wh_perspective_registry");
    foreach (var payload in payloads) {
#pragma warning disable S2077 // The schema cannot be a bind parameter; PgIdentifier.Qualify quotes it. Every value is bound.
      await using var differs = new NpgsqlCommand($"""
        SELECT EXISTS (
          SELECT 1
          FROM (SELECT e->>'ClrTypeName' AS clr_type_name, e->>'TableName' AS table_name, (e->'SchemaJson')::jsonb AS schema_json,
                       e->>'SchemaHash' AS schema_hash, COALESCE(e->>'ServiceName', $2) AS service_name
                FROM jsonb_array_elements($1::jsonb) AS e) AS p
          LEFT JOIN {registry} AS r ON r.clr_type_name = p.clr_type_name AND r.service_name = p.service_name
          CROSS JOIN LATERAL (
            SELECT array_agg(DISTINCT n->>'PerspectiveName' ORDER BY n->>'PerspectiveName') AS names
            FROM jsonb_array_elements($1::jsonb) AS n
            WHERE n->>'ClrTypeName' = p.clr_type_name AND COALESCE(n->>'ServiceName', $2) = p.service_name
              AND NULLIF(n->>'PerspectiveName', '') IS NOT NULL) AS declared
          WHERE r.clr_type_name IS NULL
             OR r.table_name IS DISTINCT FROM p.table_name
             OR r.schema_hash IS DISTINCT FROM p.schema_hash
             OR r.schema_json::jsonb IS DISTINCT FROM p.schema_json
             OR (declared.names IS NOT NULL AND r.perspective_names IS DISTINCT FROM declared.names))
        """, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
#pragma warning restore S2077
      differs.Parameters.AddWithValue(payload.Json);
      differs.Parameters.AddWithValue(payload.ServiceName);
      if (await differs.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true) {
        return false;
      }
    }
    return true;
  }

  private static async Task<bool> _tryLockAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, long lockId, CancellationToken cancellationToken) {
    await using var attempt = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection, transaction);
    attempt.Parameters.AddWithValue(lockId);
    return BooleanScalar.Read(await attempt.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), "schema lock attempt");
  }

  private static async Task _lockAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, long lockId, int commandTimeoutSeconds,
      CancellationToken cancellationToken) {
    await using var wait = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction) {
      CommandTimeout = commandTimeoutSeconds,
    };
    wait.Parameters.AddWithValue(lockId);
    await wait.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "The perspective registry of schema {Schema} differed from what this build declares, which no hash covers; it was reconciled")]
  static partial void LogReconciled(ILogger logger, string schema);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Could not check or reconcile the perspective registry of schema {Schema}; it is left as it is, and the next start tries again")]
  static partial void LogFailed(ILogger logger, Exception exception, string schema);
}
