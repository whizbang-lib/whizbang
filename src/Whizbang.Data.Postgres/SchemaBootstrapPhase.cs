// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;

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
/// Four properties make this safe to put in front of every startup.
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
/// before this existed.
/// </para>
/// <para>
/// It <b>waits rather than skips</b> (#1636). The lock is tried first; an instance that finds it held says so, waits
/// for it, and reads the closure record again once it has it, so the holder's commit serves it and nothing is applied
/// twice. Every instance needs the closure's objects before it registers at the end of Preflight, so skipping only
/// moved the wait somewhere less visible.
/// </para>
/// <para>
/// It <b>retries what is transient and fails loudly on anything else</b> (#1636). A deadlock, a dropped connection or a
/// timeout is logged at Warning and retried after a delay; any other failure is logged at Error and thrown as a
/// <see cref="SchemaBootstrapException"/> naming what failed. Nothing is swallowed.
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
/// <docs>operations/infrastructure/migrations#bootstrap-waits-for-the-lock</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs</tests>
public static class SchemaBootstrapPhase {
  /// <summary>
  /// Applies the bootstrap scripts, then reports whether an instance can now be elected; the bootstrap of Preflight
  /// (#1636).
  /// </summary>
  /// <param name="connectionFactory">Produces the connection the lock and the scripts share.</param>
  /// <param name="lockId">The schema initialization lock key, the same one the DDL phase uses.</param>
  /// <param name="scripts">The core table SQL and the marked migration regions, in order.</param>
  /// <param name="schema">The target schema, used to probe for the elected objects.</param>
  /// <param name="commandTimeoutSeconds">The timeout for one script, and for one wait on the schema lock.</param>
  /// <param name="services">Where the stage reporter and the schema initialization observers are registered; null tells none.</param>
  /// <param name="timeProvider">The clock a retry waits on.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// <see langword="true"/> when the objects an election needs are present, whether this instance
  /// created them or found them. <see langword="false"/> means the caller must fall back to
  /// migrating under the advisory lock alone.
  /// </returns>
  /// <remarks>
  /// <para>
  /// Each attempt is one connection: read the closure record, apply the closure under the schema lock when it is not
  /// recorded, then probe whether an election is possible. The lock is waited for, not skipped: an instance that finds
  /// it held says so (<see cref="ISchemaInitializationObserver.OnSchemaLockContendedAsync"/>, and the Preflight stage
  /// message), waits, and reads the record again once it has the lock, so the holder's commit serves it.
  /// </para>
  /// <para>
  /// A failed attempt is classified. A transient database failure (<see cref="TransientDatabaseFailure"/>: a deadlock,
  /// a dropped connection, a timeout, a lock wait that ran out) is logged at Warning with what failed and retried after
  /// <see cref="RetryDelay"/>, for as long as startup lasts. Any other failure is logged at Error and thrown as a
  /// <see cref="SchemaBootstrapException"/> naming what failed: it would fail the same way on every attempt.
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs:ABootstrapThatFindsTheLockHeld_WaitsForIt_ThenAppliesTheClosureAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs:AWaiterWhoseHolderAppliedTheClosureMeanwhile_AppliesNothingAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs:ABootstrapCanceledWhileItWaits_EndsWithTheCancellation_AndAppliesNothingAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs:ATransientFailure_IsRetriedOnceItsDelayHasPassed_ThenTheClosureLandsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs:ANonTransientFailure_FailsStartupLoudly_NamingWhatFailedAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs:AConnectionRefusedForGood_FailsTheBootstrap_NamingTheConnectionAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupPreflightHostTests.cs:ABootstrapThatCannotBeApplied_FailsTheInitialization_NamingWhatFailedAsync</tests>
  public static async Task<bool> ApplyAsync(
      Func<NpgsqlConnection> connectionFactory,
      long lockId,
      IEnumerable<(string Name, string Sql)> scripts,
      string schema,
      int commandTimeoutSeconds,
      IServiceProvider? services,
      TimeProvider timeProvider,
      ILogger? logger,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(scripts);
    ArgumentNullException.ThrowIfNull(timeProvider);

    // Non-null so the log calls below need no guard. NullLogger discards, at no cost.
    var log = logger ?? NullLogger.Instance;
    var target = string.IsNullOrEmpty(schema) ? "public" : schema.Replace("\"", string.Empty);
    var closure = BootstrapClosure.Of(scripts);
    var attempt = new BootstrapAttempt(connectionFactory, lockId, closure, target, commandTimeoutSeconds, services, log);

    var failures = 0;
    while (true) {
      try {
        return await attempt.RunAsync(cancellationToken).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException
          && TransientDatabaseFailure.TryClassify(ex, out var failure)) {
        failures++;
        // Scheduled before anyone is told, so an observer driving the clock can advance it as soon as it hears.
        var delay = RetryDelay(failures);
        var wait = Task.Delay(delay, timeProvider, cancellationToken);
        SchemaBootstrapLog.RetryScheduled(
          log, ex, target, attempt.Step, failure.SqlState ?? "none", failures, (long)delay.TotalMilliseconds);
        await SchemaInitializationStages.BootstrapRetryingAsync(services, cancellationToken).ConfigureAwait(false);
        await SchemaInitializationObservers.BootstrapRetryScheduledAsync(services, target, failures, ex, delay, cancellationToken)
          .ConfigureAwait(false);
        await wait.ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        SchemaBootstrapLog.Failed(log, ex, target, attempt.Step);
        await SchemaInitializationStages.BootstrapFailedAsync(services, cancellationToken).ConfigureAwait(false);
        throw new SchemaBootstrapException(target, attempt.Step, ex);
      }
    }
  }

  /// <summary>
  /// Applies the bootstrap scripts, then reports whether an instance can now be elected, retrying on the system clock
  /// and telling no observer.
  /// </summary>
  /// <param name="connectionFactory">Produces the connection the lock and the scripts share.</param>
  /// <param name="lockId">The schema initialization lock key, the same one the DDL phase uses.</param>
  /// <param name="scripts">The core table SQL and the marked migration regions, in order.</param>
  /// <param name="schema">The target schema, used to probe for the elected objects.</param>
  /// <param name="commandTimeoutSeconds">The timeout for one script, and for one wait on the schema lock.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Whether the objects an election needs are present.</returns>
  /// <exception cref="SchemaBootstrapException">The bootstrap failed for a reason that is not transient.</exception>
  public static Task<bool> ApplyAsync(
      Func<NpgsqlConnection> connectionFactory,
      long lockId,
      IEnumerable<(string Name, string Sql)> scripts,
      string schema,
      int commandTimeoutSeconds,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) =>
    ApplyAsync(
      connectionFactory, lockId, scripts, schema, commandTimeoutSeconds, null, TimeProvider.System, logger, cancellationToken);

  /// <summary>
  /// One attempt at the bootstrap, and what it was doing, so a failure can say what failed.
  /// </summary>
  private sealed class BootstrapAttempt(
      Func<NpgsqlConnection> connectionFactory,
      long lockId,
      BootstrapClosure closure,
      string schema,
      int commandTimeoutSeconds,
      IServiceProvider? services,
      ILogger log) {
    /// <summary>What the attempt is doing: a script's name, or a step around the scripts in angle brackets.</summary>
    public string Step { get; private set; } = CONNECT;

    /// <summary>Reads the record, applies the closure when it is not recorded, and probes for an election.</summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken) {
      Step = CONNECT;
      await using var connection = connectionFactory();
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

      Step = CLOSURE_RECORD;
      var recorded = await _recordedClosureAsync(connection, schema, closure, cancellationToken).ConfigureAwait(false);
      if (recorded == RecordedClosure.Current) {
        // The closure this instance carries is the one the database already holds. Applying it again
        // would take a share lock per statement on tables the running instances write, for nothing:
        // an instance starting under load deadlocked on exactly that.
        SchemaBootstrapLog.ClosureCurrent(log, closure.Hash, schema);
      } else if (recorded == RecordedClosure.Legacy) {
        // The same closure, recorded by an earlier release under the hash it computed then. Current
        // all the same; the normalized hash is recorded beside it so the next start finds it directly.
        await _recordNormalizedClosureHashAsync(connection, lockId, closure, schema, log, cancellationToken)
          .ConfigureAwait(false);
      } else {
        // Its own method so the transaction is closed before the probe below, which has to read
        // committed state. That ordering is the reason this is not inlined here.
        await _applyUnderTheLockAsync(connection, cancellationToken).ConfigureAwait(false);
      }

      Step = ELECTION_PROBE;
      var ready = await CanElectAsync(connection, schema, cancellationToken).ConfigureAwait(false);
      if (!ready) {
        // Said out loud because the consequence is invisible otherwise: startup still works, but
        // every instance migrates under the lock instead of one being chosen.
        SchemaBootstrapLog.ElectionUnavailable(log, schema);
      }
      return ready;
    }

    /// <summary>
    /// Applies every script in one transaction, under the schema lock, waiting for the lock when another session holds
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All-or-nothing, which suits it: every statement is idempotent DDL, none of it needs to run outside a
    /// transaction, and a partly applied bootstrap cannot elect anything anyway because the caller's probe asks for the
    /// full set. Nothing rolls back explicitly: disposing an uncommitted transaction is a rollback, and on every path
    /// out of this method that does not commit, that is exactly what should happen. Failures propagate to the caller,
    /// which classifies them.
    /// </para>
    /// <para>
    /// The record is read again once the lock is held: the holder this instance waited for may have applied the same
    /// closure, and applying it again would take every statement's lock on tables now in use, for nothing.
    /// </para>
    /// </remarks>
    private async Task _applyUnderTheLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken) {
      Step = SCHEMA_LOCK;
      await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

      if (!await _tryLockAsync(connection, lockId, cancellationToken).ConfigureAwait(false)) {
        SchemaBootstrapLog.WaitingForTheLock(log, schema, lockId);
        await SchemaInitializationStages.WaitingForTheBootstrapLockAsync(services, cancellationToken).ConfigureAwait(false);
        await SchemaInitializationObservers.LockContendedAsync(services, schema, cancellationToken).ConfigureAwait(false);
        await _lockAsync(connection, lockId, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        Step = CLOSURE_RECORD;
        if (await _recordedClosureAsync(connection, schema, closure, cancellationToken).ConfigureAwait(false)
            == RecordedClosure.Current) {
          SchemaBootstrapLog.AppliedWhileWaiting(log, closure.Hash, schema);
          return;
        }
      }

      // The bootstrap regions are migration text, and shipped migration text may ask current_schema()
      // about the schema it alters. Transaction-local, so it ends with this transaction.
      Step = SEARCH_PATH;
      _ = await MigrationSearchPath.EnterAsync(connection, schema, cancellationToken).ConfigureAwait(false);

      foreach (var (name, sql) in closure.Scripts) {
        Step = name;
        // Catalog-checked (#1636): one transaction applies the whole closure, so an ADD COLUMN or CREATE INDEX that
        // took its lock with nothing to do held it across every table after it, while the instances already running
        // used those tables.
        await using var command = new NpgsqlCommand(CatalogCheckedDdl.Guard(sql), connection) {
          CommandTimeout = commandTimeoutSeconds,
        };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      }

      // Recorded in the same transaction as the scripts, so the record exists exactly when the
      // closure it names does. The table is part of the closure (migration 000's region).
      Step = CLOSURE_RECORD;
      await using (var record = new NpgsqlCommand(
          $"INSERT INTO {_quoteIdentifier(schema)}.wh_bootstrap_closure (closure_hash) VALUES ($1) ON CONFLICT DO NOTHING",
          connection)) {
        record.Parameters.AddWithValue(closure.Hash);
        await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      }

      await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
      SchemaBootstrapLog.ClosureApplied(log, closure.Hash, schema);
    }

    /// <summary>Whether the database records <paramref name="closure"/>, under its hash or only under its legacy hash.</summary>
    /// <remarks>
    /// Two statements rather than one: a query that names the record table fails to parse when the
    /// table does not exist yet, whatever else the query says, and on an empty database it does not.
    /// A record that cannot be read reads as "not recorded", which applies the closure; the safe
    /// direction, since applying is idempotent and skipping is not.
    /// </remarks>
    private static async Task<RecordedClosure> _recordedClosureAsync(
        NpgsqlConnection connection, string schema, BootstrapClosure closure, CancellationToken cancellationToken) {
      await using (var exists = new NpgsqlCommand("SELECT to_regclass($1 || '.wh_bootstrap_closure') IS NOT NULL", connection)) {
        exists.Parameters.AddWithValue(_quoteIdentifier(schema));
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
          return RecordedClosure.None;
        }
      }
      var table = $"{_quoteIdentifier(schema)}.wh_bootstrap_closure";
      await using var recorded = new NpgsqlCommand(
        $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {table} WHERE closure_hash = $1) THEN {(int)RecordedClosure.Current} "
        + $"WHEN EXISTS (SELECT 1 FROM {table} WHERE closure_hash = $2) THEN {(int)RecordedClosure.Legacy} "
        + $"ELSE {(int)RecordedClosure.None} END",
        connection);
      recorded.Parameters.AddWithValue(closure.Hash);
      recorded.Parameters.AddWithValue(closure.LegacyHash);
      return (RecordedClosure)(int)(await recorded.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Takes the schema lock for the life of the caller's transaction, waiting for it (#1636).
    /// </summary>
    /// <remarks>
    /// Bounded by the command timeout, so a holder that keeps the lock longer ends this wait as a timeout, which is
    /// transient: the caller retries after its delay, on a fresh connection, and waits again.
    /// </remarks>
    private static async Task _lockAsync(
        NpgsqlConnection connection, long lockId, int commandTimeoutSeconds, CancellationToken cancellationToken) {
      await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection) {
        CommandTimeout = commandTimeoutSeconds,
      };
      command.Parameters.AddWithValue(lockId);
      await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The pre-migration step for the bootstrap closure: records the closure's normalized hash beside the legacy one an
    /// earlier release recorded, under the schema lock and without applying anything.
    /// </summary>
    /// <param name="connection">An open connection with no transaction of its own.</param>
    /// <param name="lockId">The schema initialization lock key.</param>
    /// <param name="closure">The closure whose legacy hash the database holds.</param>
    /// <param name="schema">The target schema, which holds the record table.</param>
    /// <param name="log">Where to report; never null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// The legacy row is kept rather than replaced: an instance of the earlier release still running in the fleet keeps
    /// finding its own record, and does not apply the closure's DDL on its next start. Idempotent: once the normalized
    /// hash is recorded, <see cref="ApplyAsync"/> finds it and never comes here.
    /// </para>
    /// <para>
    /// Unlike the bootstrap itself, it never waits and never throws: nothing is applied here. An instance that does not
    /// get the lock records nothing (the holder's start, or this instance's next one, does it), and a record that cannot be written is
    /// reported: the closure is current either way.
    /// </para>
    /// </remarks>
    private static async Task _recordNormalizedClosureHashAsync(
        NpgsqlConnection connection,
        long lockId,
        BootstrapClosure closure,
        string schema,
        ILogger log,
        CancellationToken cancellationToken) {
      await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
      try {
        if (!await _tryLockAsync(connection, lockId, cancellationToken).ConfigureAwait(false)) {
          SchemaBootstrapLog.LockHeldElsewhere(log, lockId);
          return;
        }

        await using (var record = new NpgsqlCommand(
            $"INSERT INTO {_quoteIdentifier(schema)}.wh_bootstrap_closure (closure_hash) VALUES ($1) ON CONFLICT DO NOTHING",
            connection)) {
          record.Parameters.AddWithValue(closure.Hash);
          await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        SchemaBootstrapLog.LegacyClosureRecordedAgain(log, closure.LegacyHash, closure.Hash, schema);
      } catch (NpgsqlException ex) {
        // Reported, never rethrown: nothing was applied and the closure is current, so the start goes on. A database
        // refusal is the failure this can meet; cancellation, and anything that is not the database's, propagates.
        SchemaBootstrapLog.LegacyClosureRecordFailed(log, ex, closure.LegacyHash, schema);
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

  // What a bootstrap attempt reports as the step that failed, when it was not a script.
  private const string CONNECT = "<connect>";
  private const string CLOSURE_RECORD = "<closure record>";
  private const string SCHEMA_LOCK = "<schema lock>";
  private const string SEARCH_PATH = "<search path>";
  private const string ELECTION_PROBE = "<election probe>";


  /// <summary>The wait before the bootstrap's next attempt after its <paramref name="attempt"/>th transient failure.</summary>
  /// <param name="attempt">Which attempt failed, from 1.</param>
  /// <returns>The wait.</returns>
  /// <remarks>
  /// One second, doubling to a thirty-second cap, with no limit on attempts: startup lasts until the host stops it, and
  /// a database that stays unavailable is asked at a bounded rate. No jitter: instances retrying together serialize on
  /// the schema lock anyway.
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/Schema/SchemaBootstrapRetryTests.cs:TheRetryDelay_DoublesFromOneSecond_ToAThirtySecondCapAsync</tests>
  internal static TimeSpan RetryDelay(int attempt) =>
    TimeSpan.FromSeconds(Math.Min(1 << Math.Clamp(attempt - 1, 0, 5), 30));

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

    // #1638: the row records the phase the instance is in, Migrating, as part of the registration. A row without a
    // phase reads as an older build, which is eligible for work whenever it is live, so a new instance registered here
    // would be handed partitions for its whole migration. The instance-state participant's own Migrating write ran
    // before this row existed and found nothing to update. Only where the recording function exists: the bootstrap of
    // an empty database registers before the migration that adds it, and with no older instance there is no peer to
    // hand a share to anyway.
    await using var probe = new NpgsqlCommand(
      "SELECT to_regprocedure($1 || '.record_instance_state(uuid,text,text)') IS NOT NULL", connection);
    probe.Parameters.AddWithValue(_quoteIdentifier(target));
    if (await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
      return;
    }
    await using var record = new NpgsqlCommand(
      $"SELECT {_quoteIdentifier(target)}.record_instance_state($1, $2)", connection);
    record.Parameters.AddWithValue(instance.InstanceId);
    record.Parameters.AddWithValue(nameof(LifecyclePhase.Migrating));
    await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
  /// Only statements take part: each script is hashed in the form <see cref="MigrationSqlNormalizer"/>
  /// gives it, the same form a migration's ledger row records. Comments, whitespace at line edges and
  /// between tokens, blank lines and the line ending convention are dropped before hashing, because
  /// none of them changes what is applied and any of them can differ between two instances of one
  /// release: a header a builder writes, a note an author adds, or a stamp of the clock. One such
  /// stamp once made every instance compute its own closure, so the record never matched and every
  /// start applied the DDL.
  /// </para>
  /// </remarks>
  public static string ClosureHash(IEnumerable<(string Name, string Sql)> scripts) {
    ArgumentNullException.ThrowIfNull(scripts);
    using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
    foreach (var (name, sql) in scripts) {
      sha.AppendData(System.Text.Encoding.UTF8.GetBytes(name));
      sha.AppendData("\n"u8);
      sha.AppendData(System.Text.Encoding.UTF8.GetBytes(MigrationSqlNormalizer.Normalize(sql)));
      sha.AppendData("\n\n"u8);
    }
    return Convert.ToHexStringLower(sha.GetHashAndReset());
  }

  /// <summary>
  /// The closure hash earlier releases recorded: comment-only and blank lines dropped and line endings ignored, the
  /// rest of every line hashed as written. Read only to recognize a closure an earlier release recorded.
  /// </summary>
  /// <param name="scripts">The scripts, in the order they apply.</param>
  /// <returns>Sixty-four lowercase hex characters.</returns>
  internal static string LegacyClosureHash(IEnumerable<(string Name, string Sql)> scripts) {
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


  /// <summary>How the database records a closure: not at all, under its hash, or only under the legacy hash.</summary>
  private enum RecordedClosure { None = 0, Current = 1, Legacy = 2 }

  private static string _quoteIdentifier(string identifier) =>
    "\"" + identifier.Replace("\"", "\"\"") + "\"";

  /// <summary>The scripts a bootstrap applies, and the hashes that name exactly that set.</summary>
  /// <param name="Scripts">The scripts, in the order they apply.</param>
  /// <param name="Hash">Their <see cref="ClosureHash"/>.</param>
  /// <param name="LegacyHash">Their <see cref="LegacyClosureHash"/>, which an earlier release recorded.</param>
  private sealed record BootstrapClosure(IReadOnlyList<(string Name, string Sql)> Scripts, string Hash, string LegacyHash) {
    /// <summary>Materializes <paramref name="scripts"/> once and hashes them.</summary>
    public static BootstrapClosure Of(IEnumerable<(string Name, string Sql)> scripts) {
      var list = new List<(string Name, string Sql)>(scripts);
      return new BootstrapClosure(list, ClosureHash(list), LegacyClosureHash(list));
    }
  }
}

/// <summary>Source-generated logging for the bootstrap phase.</summary>
internal static partial class SchemaBootstrapLog {
  [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Debug,
      Message = "Recording the bootstrap closure's normalized hash skipped: another session holds schema lock {LockId}; the next start records it")]
  public static partial void LockHeldElsewhere(ILogger logger, long lockId);

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

  [LoggerMessage(
      EventId = 6,
      Level = LogLevel.Information,
      Message = "Schema bootstrap closure is recorded for {Schema} under its legacy hash {LegacyHash}; nothing applied, "
              + "and its normalized hash {ClosureHash} is now recorded beside it")]
  public static partial void LegacyClosureRecordedAgain(ILogger logger, string legacyHash, string closureHash, string schema);

  [LoggerMessage(
      EventId = 7,
      Level = LogLevel.Warning,
      Message = "Schema bootstrap closure is recorded for {Schema} under its legacy hash {LegacyHash}, but its normalized "
              + "hash could not be recorded beside it; nothing was applied, and the next start tries again")]
  public static partial void LegacyClosureRecordFailed(ILogger logger, Exception exception, string legacyHash, string schema);

  [LoggerMessage(
      EventId = 8,
      Level = LogLevel.Warning,
      Message = "Schema bootstrap for {Schema} met a transient database failure at {Step} (SQLSTATE {SqlState}, attempt "
              + "{Attempt}); retrying in {DelayMs} ms, and Preflight does not finish until it lands")]
  public static partial void RetryScheduled(
      ILogger logger, Exception exception, string schema, string step, string sqlState, int attempt, long delayMs);

  [LoggerMessage(
      EventId = 9,
      Level = LogLevel.Error,
      Message = "Schema bootstrap for {Schema} failed at {Step} with an error that is not transient, so it is not "
              + "retried: this initialization attempt fails in Preflight, before any election or migration")]
  public static partial void Failed(ILogger logger, Exception exception, string schema, string step);

  [LoggerMessage(
      EventId = 10,
      Level = LogLevel.Information,
      Message = "Schema bootstrap for {Schema} is waiting for schema lock {LockId}, which another session holds")]
  public static partial void WaitingForTheLock(ILogger logger, string schema, long lockId);

  [LoggerMessage(
      EventId = 11,
      Level = LogLevel.Information,
      Message = "Schema bootstrap closure {ClosureHash} was applied to {Schema} by the session this instance waited "
              + "for; nothing applied here")]
  public static partial void AppliedWhileWaiting(ILogger logger, string closureHash, string schema);
}
