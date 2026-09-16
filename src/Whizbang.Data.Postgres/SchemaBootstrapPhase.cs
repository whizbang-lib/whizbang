using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// It <b>never blocks</b>. The lock is taken with <c>pg_try_advisory_xact_lock</c>, and an instance
/// that does not get it applies nothing and simply asks the same question. The holder's work serves
/// everyone, and a fleet starting together does not queue.
/// </para>
/// <para>
/// That the lock is transaction-scoped rather than session-scoped is load bearing. A session lock
/// does not survive a transaction-pooling front end, because the lock and the unlock land on
/// different server connections; the unlock misses, and since both advisory scopes share one lock
/// space, the leaked lock then blocks the DDL phase's own try-lock on the same key indefinitely.
/// Nothing would ever migrate that schema again. A transaction-scoped lock cannot leak: the server
/// releases it on commit and on rollback alike.
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

    // Non-null so the log calls below need no guard. NullLogger discards, at no cost.
    var log = logger ?? NullLogger.Instance;

    await using var connection = connectionFactory();
    await connection.OpenAsync(cancellationToken);

    var target = string.IsNullOrEmpty(schema) ? "public" : schema.Replace("\"", string.Empty);
    var closure = BootstrapClosure.Of(scripts);

    if (await _isClosureRecordedAsync(connection, target, closure.Hash, cancellationToken).ConfigureAwait(false)) {
      // The closure this instance carries is the one the database already holds. Applying it again
      // would take a share lock per statement on tables the running instances write, for nothing;
      // an instance starting under load deadlocked on exactly that.
      SchemaBootstrapLog.ClosureCurrent(log, closure.Hash, target);
    } else {
      // Its own method so the transaction is closed before the probe below, which has to read
      // committed state. That ordering is the reason this is not inlined here.
      await _applyUnderTheLockAsync(
        connection, lockId, closure, target, commandTimeoutSeconds, log, cancellationToken)
        .ConfigureAwait(false);
    }

    var ready = await CanElectAsync(connection, schema, cancellationToken).ConfigureAwait(false);
    if (!ready) {
      // Said out loud because the consequence is invisible otherwise: startup still works, but
      // every instance migrates under the lock instead of one being chosen.
      SchemaBootstrapLog.ElectionUnavailable(log, schema);
    }

    return ready;
  }

  /// <summary>
  /// Applies every script in one transaction, under the schema lock, reporting rather than throwing.
  /// </summary>
  /// <param name="connection">An open connection with no transaction of its own.</param>
  /// <param name="lockId">The schema initialization lock key.</param>
  /// <param name="closure">The scripts, named for reporting, and the hash recorded once they land.</param>
  /// <param name="schema">The target schema, which holds the record table.</param>
  /// <param name="commandTimeoutSeconds">The timeout for one script.</param>
  /// <param name="log">Where to report; never null.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <remarks>
  /// <para>
  /// All-or-nothing, which suits it: every statement is idempotent DDL, none of it needs to run
  /// outside a transaction, and a partly applied bootstrap cannot elect anything anyway because the
  /// caller's probe asks for the full set. So "some landed" and "none landed" reach the same
  /// fallback, and leaving the partial objects behind would only make the next instance's failure
  /// harder to read.
  /// </para>
  /// <para>
  /// Nothing rolls back explicitly. Disposing an uncommitted transaction is a rollback, and on
  /// every path out of this method that is exactly what should happen.
  /// </para>
  /// </remarks>
  private static async Task _applyUnderTheLockAsync(
      NpgsqlConnection connection,
      long lockId,
      BootstrapClosure closure,
      string schema,
      int commandTimeoutSeconds,
      ILogger log,
      CancellationToken cancellationToken) {
    var applying = "<none>";
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
      .ConfigureAwait(false);

    try {
      if (!await _tryLockAsync(connection, lockId, cancellationToken).ConfigureAwait(false)) {
        // The expected outcome for every instance but one, and not a failure: the holder's
        // committed work is what the caller's probe will find.
        SchemaBootstrapLog.LockHeldElsewhere(log, lockId);
        return;
      }

      foreach (var (name, sql) in closure.Scripts) {
        applying = name;
        await using var command = new NpgsqlCommand(sql, connection) {
          CommandTimeout = commandTimeoutSeconds,
        };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      }

      // Recorded in the same transaction as the scripts, so the record exists exactly when the
      // closure it names does. The table is part of the closure (migration 000's region).
      applying = "<closure record>";
      await using (var record = new NpgsqlCommand(
          $"INSERT INTO {_quoteIdentifier(schema)}.wh_bootstrap_closure (closure_hash) VALUES ($1) ON CONFLICT DO NOTHING",
          connection)) {
        record.Parameters.AddWithValue(closure.Hash);
        await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      }

      await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
      SchemaBootstrapLog.ClosureApplied(log, closure.Hash, schema);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Reported and carried on from, never rethrown. A bootstrap that cannot be applied costs the
      // election; the caller then migrates under the advisory lock, which is what every instance
      // did before an election existed. Throwing here would turn a wrong closure or a permission
      // problem into a service that cannot start at all.
      SchemaBootstrapLog.ScriptFailed(log, ex, applying);
    }
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

  /// <summary>
  /// The hash that identifies a closure: SHA-256 over every script's name and text, in order.
  /// </summary>
  /// <param name="scripts">The scripts, in the order they apply.</param>
  /// <returns>Sixty-four lowercase hex characters.</returns>
  /// <remarks>
  /// <para>
  /// Names take part so that the same text under a different region name, or a region moved to a
  /// different position, reads as a different closure; the point of the record is that an equal
  /// hash means the database holds exactly what this instance would apply.
  /// </para>
  /// <para>
  /// Only statements take part. A line that is nothing but a comment, a blank line, and the line
  /// ending convention are dropped before hashing, because none of them changes what is applied
  /// and any of them can differ between two instances of one release: a header a builder writes,
  /// a note an author adds, or a stamp of the clock. One such stamp once made every instance
  /// compute its own closure, so the record never matched and every start applied the DDL.
  /// </para>
  /// </remarks>
  public static string ClosureHash(IEnumerable<(string Name, string Sql)> scripts) {
    ArgumentNullException.ThrowIfNull(scripts);
    using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
    foreach (var (name, sql) in scripts) {
      sha.AppendData(System.Text.Encoding.UTF8.GetBytes(name));
      sha.AppendData("\n"u8);
      foreach (var line in _statementLines(sql)) {
        sha.AppendData(System.Text.Encoding.UTF8.GetBytes(line));
        sha.AppendData("\n"u8);
      }
      sha.AppendData("\n"u8);
    }
    return Convert.ToHexStringLower(sha.GetHashAndReset());
  }

  /// <summary>The lines of a script that carry a statement: not blank, not a comment on its own.</summary>
  private static IEnumerable<string> _statementLines(string sql) {
    foreach (var raw in sql.Split('\n')) {
      var line = raw.TrimEnd('\r');
      var content = line.AsSpan().Trim();
      if (content.IsEmpty || content.StartsWith("--", StringComparison.Ordinal)) {
        continue;
      }
      yield return line;
    }
  }

  /// <summary>Whether the database records <paramref name="closureHash"/> as applied.</summary>
  /// <remarks>
  /// Two statements rather than one: a query that names the record table fails to parse when the
  /// table does not exist yet, whatever else the query says, and on an empty database it does not.
  /// A record that cannot be read reads as "not recorded", which applies the closure; the safe
  /// direction, since applying is idempotent and skipping is not.
  /// </remarks>
  private static async Task<bool> _isClosureRecordedAsync(
      NpgsqlConnection connection, string schema, string closureHash, CancellationToken cancellationToken) {
    await using (var exists = new NpgsqlCommand("SELECT to_regclass($1 || '.wh_bootstrap_closure') IS NOT NULL", connection)) {
      exists.Parameters.AddWithValue(_quoteIdentifier(schema));
      if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
        return false;
      }
    }
    await using var recorded = new NpgsqlCommand(
      $"SELECT EXISTS (SELECT 1 FROM {_quoteIdentifier(schema)}.wh_bootstrap_closure WHERE closure_hash = $1)", connection);
    recorded.Parameters.AddWithValue(closureHash);
    return await recorded.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }

  private static string _quoteIdentifier(string identifier) =>
    "\"" + identifier.Replace("\"", "\"\"") + "\"";

  /// <summary>The scripts a bootstrap applies, and the hash that names exactly that set.</summary>
  /// <param name="Scripts">The scripts, in the order they apply.</param>
  /// <param name="Hash">Their <see cref="ClosureHash"/>.</param>
  private sealed record BootstrapClosure(IReadOnlyList<(string Name, string Sql)> Scripts, string Hash) {
    /// <summary>Materializes <paramref name="scripts"/> once and hashes them.</summary>
    public static BootstrapClosure Of(IEnumerable<(string Name, string Sql)> scripts) {
      var list = new List<(string Name, string Sql)>(scripts);
      return new BootstrapClosure(list, ClosureHash(list));
    }
  }

  /// <summary>
  /// Takes the schema lock for the life of the caller's transaction, without waiting.
  /// </summary>
  /// <remarks>
  /// Transaction-scoped rather than session-scoped, which is what makes this safe behind a
  /// transaction-pooling front end and impossible to leak. The same key the DDL phase uses, so an
  /// instance bootstrapping and an instance creating tables exclude each other.
  /// </remarks>
  private static async Task<bool> _tryLockAsync(
      NpgsqlConnection connection, long lockId, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection);
    command.Parameters.AddWithValue(lockId);
    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
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

  [LoggerMessage(
      EventId = 4,
      Level = LogLevel.Debug,
      Message = "Schema bootstrap closure {ClosureHash} is already recorded for {Schema}; nothing applied, no lock taken")]
  public static partial void ClosureCurrent(ILogger logger, string closureHash, string schema);

  [LoggerMessage(
      EventId = 5,
      Level = LogLevel.Information,
      Message = "Schema bootstrap closure {ClosureHash} applied and recorded for {Schema}")]
  public static partial void ClosureApplied(ILogger logger, string closureHash, string schema);
}
