using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Brings up just enough of the schema for an instance to join the registry and be elected to
/// migrate the rest.
/// </summary>
/// <remarks>
/// <para>
/// Schema initialization has a cycle in it. Choosing which instance migrates is a duty election;
/// the elector records its win through <c>record_capability</c>; that function is created by a
/// migration; and it answers FALSE for an instance that is not in the registry, which at this point
/// in startup is every instance. So the migration pass cannot be what makes election possible, and
/// a small marked subset is applied first by whichever instance gets there, the way an operating
/// system loads only enough of itself to load the rest.
/// </para>
/// <para>
/// Three properties make this safe to put in front of every startup.
/// </para>
/// <para>
/// It <b>writes no ledger rows</b>. Nothing here records a hash or a migration status, so the
/// ordinary pass still applies these same migrations afterwards and records them exactly as it
/// always did. The bootstrap only makes objects exist; it never claims to have migrated anything.
/// </para>
/// <para>
/// It <b>does not assume its own success</b>. Having applied the scripts, it asks the database
/// whether election is now actually possible, and that answer is what it reports. A closure that is
/// missing an object therefore degrades to "no election", which is how every instance behaved
/// before this existed, rather than to a service that cannot start.
/// </para>
/// <para>
/// It <b>never blocks</b>. The lock is taken with <c>pg_try_advisory_lock</c>, and an instance that
/// does not get it applies nothing and simply asks the same question. The holder's work serves
/// everyone, and a fleet starting together does not queue.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs</tests>
public static class SchemaBootstrapPhase {
  /// <summary>
  /// Applies the bootstrap scripts, then reports whether an instance can now be elected.
  /// </summary>
  /// <param name="connectionFactory">Produces the connection the lock and the scripts share.</param>
  /// <param name="lockId">The schema initialization lock key, the same one the DDL phase uses.</param>
  /// <param name="scripts">The core table SQL and the marked migration regions, in order.</param>
  /// <param name="schema">The target schema, used to probe for the elected objects.</param>
  /// <param name="commandTimeoutSeconds">The timeout for one script.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// <see langword="true"/> when the objects an election needs are present, whether this instance
  /// created them or found them. <see langword="false"/> means the caller must fall back to
  /// migrating under the advisory lock alone.
  /// </returns>
  public static async Task<bool> ApplyAsync(
      Func<NpgsqlConnection> connectionFactory,
      long lockId,
      IEnumerable<(string Name, string Sql)> scripts,
      string schema,
      int commandTimeoutSeconds,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(scripts);

    await using var connection = connectionFactory();
    await connection.OpenAsync(cancellationToken);

    if (await _tryLockAsync(connection, lockId, cancellationToken).ConfigureAwait(false)) {
      try {
        foreach (var (name, sql) in scripts) {
          await _applyAsync(connection, name, sql, commandTimeoutSeconds, logger, cancellationToken)
            .ConfigureAwait(false);
        }
      } finally {
        await _unlockAsync(connection, lockId).ConfigureAwait(false);
      }
    } else if (logger is not null) {
      // The expected outcome for every instance but one, and not a failure: the holder's committed
      // work is what the probe below will find.
      SchemaBootstrapLog.LockHeldElsewhere(logger, lockId);
    }

    var ready = await CanElectAsync(connection, schema, cancellationToken).ConfigureAwait(false);
    if (!ready && logger is not null) {
      // Said out loud because the consequence is invisible otherwise: startup still works, but
      // every instance migrates under the lock instead of one being chosen.
      SchemaBootstrapLog.ElectionUnavailable(logger, schema);
    }

    return ready;
  }

  /// <summary>
  /// Whether the objects a duty election needs are present in <paramref name="schema"/>.
  /// </summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The target schema.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns><see langword="true"/> when an instance can register and be granted a duty.</returns>
  /// <remarks>
  /// Asks for exactly what the elector uses and nothing more: somewhere to register, somewhere to
  /// record a holding, and the two functions that do it. Probing the outcome rather than trusting
  /// the scripts is what turns an incomplete closure into a lost optimization instead of an outage.
  /// </remarks>
  public static async Task<bool> CanElectAsync(
      NpgsqlConnection connection,
      string schema,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);

    var target = string.IsNullOrEmpty(schema) ? "public" : schema.Replace("\"", string.Empty);

    await using var command = new NpgsqlCommand(
      """
      SELECT to_regclass($1 || '.wh_service_instances') IS NOT NULL
         AND to_regclass($1 || '.wh_instance_evictions') IS NOT NULL
         AND to_regclass($1 || '.wh_instance_capabilities') IS NOT NULL
         AND to_regprocedure($1 || '.record_capability(uuid,text)') IS NOT NULL
         AND to_regprocedure($1 || '.release_capability(uuid,text)') IS NOT NULL
         AND to_regprocedure(
               $1 || '.register_instance_heartbeat(uuid,character varying,character varying,'
                  || 'integer,jsonb,timestamp with time zone,timestamp with time zone)')
             IS NOT NULL
      """, connection);
    command.Parameters.AddWithValue(_quoteIdentifier(target));
    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }

  /// <summary>
  /// Puts this instance in the registry, so a duty can be recorded against it.
  /// </summary>
  /// <param name="connectionFactory">Produces the connection to register over.</param>
  /// <param name="schema">The target schema.</param>
  /// <param name="instance">Who to register.</param>
  /// <param name="leaseSeconds">How long the registration is asserted good for.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <remarks>
  /// <para>
  /// Through the framework's own registration function rather than an <c>INSERT</c> written here,
  /// so a change to the registry cannot leave two disagreeing ideas of what a registered instance
  /// looks like.
  /// </para>
  /// <para>
  /// This runs before the heartbeat worker exists, which is the point: the worker starts after the
  /// schema is ready, and the election that decides who makes it ready happens now. The row this
  /// writes is picked up and maintained by that worker as soon as it does start.
  /// </para>
  /// </remarks>
  public static async Task RegisterInstanceAsync(
      Func<NpgsqlConnection> connectionFactory,
      string schema,
      IServiceInstanceProvider instance,
      int leaseSeconds = 60,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(instance);

    var target = string.IsNullOrEmpty(schema) ? "public" : schema.Replace("\"", string.Empty);

    await using var connection = connectionFactory();
    await connection.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand(
      $"SELECT {_quoteIdentifier(target)}.register_instance_heartbeat("
      + "$1, $2, $3, $4, NULL::jsonb, now(), now() + make_interval(secs => $5))", connection);
    command.Parameters.AddWithValue(instance.InstanceId);
    command.Parameters.AddWithValue(instance.ServiceName);
    command.Parameters.AddWithValue(instance.HostName);
    command.Parameters.AddWithValue(instance.ProcessId);
    command.Parameters.AddWithValue((double)leaseSeconds);
    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  private static string _quoteIdentifier(string identifier) =>
    "\"" + identifier.Replace("\"", "\"\"") + "\"";

  private static async Task _applyAsync(
      NpgsqlConnection connection,
      string name,
      string sql,
      int commandTimeoutSeconds,
      ILogger? logger,
      CancellationToken cancellationToken) {
    try {
      await using var command = new NpgsqlCommand(sql, connection) {
        CommandTimeout = commandTimeoutSeconds,
      };
      await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Reported and carried on with. A failure here costs the election, not the startup, and the
      // probe that follows is what decides; stopping would convert a lost optimization into the
      // outage this whole phase exists to avoid.
      if (logger is not null) {
        SchemaBootstrapLog.ScriptFailed(logger, ex, name);
      }
    }
  }

  private static async Task<bool> _tryLockAsync(
      NpgsqlConnection connection, long lockId, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
    command.Parameters.AddWithValue(lockId);
    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }

  private static async Task _unlockAsync(NpgsqlConnection connection, long lockId) {
    // Deliberately not cancellable: a canceled unlock would hold the lock for the life of the
    // connection and stall every other instance's bootstrap.
    await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
    command.Parameters.AddWithValue(lockId);
    await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
  }
}

/// <summary>Source-generated logging for the bootstrap phase.</summary>
internal static partial class SchemaBootstrapLog {
  [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Debug,
      Message = "Schema bootstrap skipped: another instance holds schema lock {LockId}")]
  public static partial void LockHeldElsewhere(ILogger logger, long lockId);

  [LoggerMessage(
      EventId = 2,
      Level = LogLevel.Warning,
      Message = "Could not apply the schema bootstrap script {Script}; a migrator cannot be elected "
              + "until it succeeds, and every instance will migrate under the advisory lock instead")]
  public static partial void ScriptFailed(ILogger logger, Exception exception, string script);

  [LoggerMessage(
      EventId = 3,
      Level = LogLevel.Warning,
      Message = "Schema {Schema} cannot elect a migrator yet, so this instance will migrate under "
              + "the advisory lock alone; correct but duplicated across a fleet")]
  public static partial void ElectionUnavailable(ILogger logger, string schema);
}
