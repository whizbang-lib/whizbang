// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Versioning;

namespace Whizbang.Data.Postgres;

/// <summary>What a rollback needs: where the schema is, its lock, this instance's versions, and how far it may go.</summary>
/// <param name="ConnectionFactory">Produces the connection the rollback runs on.</param>
/// <param name="Schema">The schema, unquoted; empty for the connection's own.</param>
/// <param name="LockId">The schema initialization lock (<see cref="SchemaInitializationLockKey"/>).</param>
/// <param name="Own">This instance's versions and id.</param>
/// <param name="AllowLossy">Whether a reversal that discards data may run.</param>
/// <param name="CommandTimeoutSeconds">How long any one reverse script may run.</param>
/// <docs>operations/startup/rolling-upgrades#rolling-back</docs>
public sealed record SchemaRollbackRequest(
  Func<NpgsqlConnection> ConnectionFactory, string Schema, long LockId, VersionSighting Own, bool AllowLossy, int CommandTimeoutSeconds);

/// <summary>
/// Rolls a schema back to this instance's version (#2122, owner decision 4): runs the reverse scripts the newer version
/// stored in the ledger, newest first, under the schema lock, in one transaction, with progress logging.
/// </summary>
/// <remarks>
/// <para>
/// Startup's Assess decides that a rollback is owed; the instance that does the schema work (the elected migrator, or
/// on a driver without an election the one holding the schema lock) runs it before its pass. It decides again under the
/// lock, from the same ledger and the same rule: another instance may have finished it while this one waited (nothing
/// is left, and nothing runs), a newer version may have come online, or a change may have been recorded that cannot be
/// reversed, and then nothing runs and the start fails naming why.
/// </para>
/// <para>
/// Newest first: the changes the latest version applied, then the version before it; within a version the application
/// schema before the library's migrations (the pass applies them the other way round), each in reverse name order.
/// Each change reversed leaves the ledger, so the pass that follows migrates from this version's point of view. One
/// transaction: a reverse script that fails undoes the whole rollback, the error names the change, and the next start
/// decides again.
/// </para>
/// </remarks>
/// <docs>operations/startup/rolling-upgrades#rolling-back</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaRollbackTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperSchemaRollbackTests.cs</tests>
public static partial class SchemaRollback {
  /// <summary>Runs the rollback the ledger allows.</summary>
  /// <param name="request">Where, under which lock, for which versions.</param>
  /// <param name="timeProvider">The clock the progress is measured on.</param>
  /// <param name="logger">Where the progress goes; nowhere when null.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many changes were reversed: none when nothing was applied since this version.</returns>
  /// <exception cref="InvalidOperationException">The ledger no longer allows a rollback, or a reverse script failed.</exception>
  public static async Task<int> ApplyAsync(
      SchemaRollbackRequest request, TimeProvider timeProvider, ILogger? logger, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(timeProvider);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.Schema);
    var log = logger ?? NullLogger.Instance;
    var schema = request.Schema;
    var prefix = PgIdentifier.QualifyPrefix(schema);

    await using var connection = request.ConnectionFactory();
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction)) {
      take.Parameters.AddWithValue(request.LockId);
      await take.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    var probe = await PostgresStartupAssessment.ProbeAsync(connection, prefix, cancellationToken).ConfigureAwait(false);
    var changes = await PostgresStartupAssessment.ReadChangesAsync(connection, prefix, probe, withScripts: true, cancellationToken)
      .ConfigureAwait(false);
    var since = UpgradeOrder.AppliedSince(request.Own, [.. changes.Select(c => c.Change)]);
    if (since.Count == 0) {
      return 0;
    }
    var online = await PostgresStartupAssessment.ReadOnlineAsync(connection, schema, cancellationToken).ConfigureAwait(false);
    if (UpgradeOrder.FindNewer(request.Own, online) is { } newer) {
      throw new InvalidOperationException($"The schema {schema} is not rolled back: {newer.Describe()}.");
    }
    if (UpgradeOrder.WhyNotReversible(request.Own, since, request.AllowLossy) is { } refusal) {
      throw new InvalidOperationException($"The schema {schema} is not rolled back: {refusal}.");
    }

    var plan = changes.Where(c => since.Contains(c.Change))
      .OrderByDescending(c => c.AppliedVersionId.GetValueOrDefault())
      .ThenByDescending(c => c.Change.Kind)
      .ThenByDescending(c => c.Change.Name, StringComparer.Ordinal)
      .ToList();
    var started = timeProvider.GetTimestamp();
    LogStarting(log, schema, plan.Count, request.Own.LibraryVersion, request.Own.ApplicationVersion);
    for (var i = 0; i < plan.Count; i++) {
      var step = plan[i];
      StartupProgress.Note();
      LogReversing(log, step.Change.Name, MigrationReversal.LedgerText(step.Change.Reversal), i + 1, plan.Count);
      await _reverseAsync(connection, transaction, prefix, step, request.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
    }
    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    LogFinished(log, schema, plan.Count, new ProgressElapsed(timeProvider.GetElapsedTime(started)));
    return plan.Count;
  }

  private static async Task _reverseAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, string prefix, PostgresStartupAssessment.LedgerChange step,
      int commandTimeoutSeconds, CancellationToken cancellationToken) {
    try {
      if (!string.IsNullOrWhiteSpace(step.DownSql)) {
#pragma warning disable S2077 // the script is the one the newer version stored in the ledger when it applied the change
        await using var reverse = new NpgsqlCommand(step.DownSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
#pragma warning restore S2077
        await reverse.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      }
#pragma warning disable S2077 // the prefix is quoted by PgIdentifier; the name is bound
      await using var forget = new NpgsqlCommand($"DELETE FROM {prefix}wh_schema_migrations WHERE file_name = $1", connection, transaction);
#pragma warning restore S2077
      forget.Parameters.AddWithValue(step.Change.Name);
      await forget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    } catch (PostgresException ex) {
      throw new InvalidOperationException(
        string.Create(CultureInfo.InvariantCulture,
          $"The rollback stopped at {UpgradeOrder.DescribeChange(step.Change)}: its reverse script failed ({ex.SqlState}: "
          + $"{ex.MessageText}). Nothing was rolled back."),
        ex);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Rolling back schema {Schema} to library {Library}, application {Application}: {Count} change(s) a newer version applied, newest first")]
  private static partial void LogStarting(ILogger logger, string schema, int count, string? library, string? application);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Reversing {Change} ({Kind}) ({Step}/{Steps})")]
  private static partial void LogReversing(ILogger logger, string change, string kind, int step, int steps);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Rolled back schema {Schema}: {Count} change(s) reversed in {Elapsed}")]
  private static partial void LogFinished(ILogger logger, string schema, int count, ProgressElapsed elapsed);
}
