// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one cycle of the written-here origin restoration did.</summary>
/// <param name="Batches">The batches it applied.</param>
/// <param name="RestoredReactions">Events returned to the local lane that were stored with no origin position (a reaction
/// the re-attribution walk moved to the trigger's owner).</param>
/// <param name="RestoredReturns">Events returned to the local lane that were stored with an origin position (an event of
/// this service's that adopted another service's redelivery bundle).</param>
/// <param name="Ambiguous">Unpositioned rows left in place because the evidence does not prove them written here.</param>
/// <param name="Status"><c>caught-up</c>, <c>budget</c> (the cycle's batch budget ran out first), or
/// <c>skipped (...)</c>.</param>
/// <docs>resilience/stream-integrity#events-written-here-returned</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WrittenHereOriginRestorationSqlTests.cs:Step_RestoresInBoundedBatches_LogsAndCountsEach_AndResumesFromItsCursorAsync</tests>
public readonly record struct WrittenHereOriginRestorationResult(
  int Batches, long RestoredReactions, long RestoredReturns, long Ambiguous, string Status);

/// <summary>
/// The one-off maintenance step that returns events this service wrote, stored under another service's origin before
/// migration 594, to the local lane (#2573). Two passes misattributed them: the re-attribution walk moved a receptor's
/// reaction of a type another service owns to that owner with no origin position (the owner then answered "unknown"
/// when asked for it, and the row kept its buckets undecided), and the redelivery reconcile let this service's own event
/// adopt the origin of another service's bundle that carried it back.
/// </summary>
/// <remarks>
/// <para>
/// The only proof the store keeps is the event's first hop: a row recorded under another origin whose first hop names
/// this service was written here (read from the row, its route or its staged full form, as 594 reads it). That proof needs
/// this service's name, which the store does not record, so the walk runs here, where <see cref="IServiceInstanceProvider"/>
/// knows it, rather than in the migration. The SQL (<c>wh_restore_written_here_origins</c>, migration 599) clears the row's
/// origin and origin position, moves its digest to the local lane through the shared fold, and re-arms both lanes' ledger
/// buckets; the store's own triggers refold the sealed epochs it left and joined, forget its "unknown" answer and take it
/// out of its origin's held set. An unpositioned row the evidence does not prove (a slim row that names no service
/// anywhere, or one its origin disowned) is left and counted as ambiguous. Nothing is deleted.
/// </para>
/// <para>
/// It runs in the maintenance cycle, so it is idle-gated and runs only on the maintainer turn, at most
/// <see cref="StreamIntegrityOptions.MaxReattributionBatchesPerPass"/> batches of
/// <see cref="StreamIntegrityOptions.ReattributionBatchSize"/> rows per cycle, each batch its own transaction. The walk
/// stops at the highest commit sequence the store held when it began, so once it is done a cycle costs one read of the
/// cursor row. <see cref="StreamIntegrityOptions.ReattributionEnabled"/> set to <see langword="false"/> turns it off
/// together with the other attribution passes; without a service name it is skipped.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity#events-written-here-returned</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WrittenHereOriginRestorationSqlTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
public sealed partial class WrittenHereOriginRestorationStep : IMaintenanceStep {
  private const string CAUGHT_UP = "caught-up";
  private const string BUDGET = "budget";
  private const string SHAPE_REACTION = "reaction";
  private const string SHAPE_RETURN = "return";

  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs each restoration, and each ambiguous row count, at Warning.</param>
  public WrittenHereOriginRestorationStep(ILogger logger) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WrittenHereOriginRestorationSqlTests.cs:FromServices_AndItsArguments_AreCheckedAsync</tests>
  public static WrittenHereOriginRestorationStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new WrittenHereOriginRestorationStep(
      services.GetService<ILogger<WrittenHereOriginRestorationStep>>() ?? (ILogger)NullLogger.Instance);
  }

  /// <inheritdoc />
  public string Name => "written-here-origin-restoration";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    await RestoreAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services; the database is reached through
  /// <see cref="PostgresArchiveConnection"/>, bounds and the switch come from <see cref="StreamIntegrityOptions"/>, and this
  /// service's name from <see cref="IServiceInstanceProvider"/>.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>What the cycle did.</returns>
  public async Task<WrittenHereOriginRestorationResult> RestoreAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var options = services.GetService<IOptions<StreamIntegrityOptions>>()?.Value ?? new StreamIntegrityOptions();
    if (!options.ReattributionEnabled) {
      return _skipped("skipped (Whizbang:StreamIntegrity:ReattributionEnabled=false)");
    }
    var name = services.GetService<IServiceInstanceProvider>()?.ServiceName;
    if (string.IsNullOrWhiteSpace(name)
        || string.Equals(name.Trim(), ServiceInstanceInfo.Unknown.ServiceName, StringComparison.OrdinalIgnoreCase)) {
      return _skipped("skipped (no service name: only this service's name proves an event was written here)");
    }
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var walk = new Walk(
      database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".",
      name, Math.Max(1, options.ReattributionBatchSize), services.GetService<StreamIntegrityMetrics>());
    var total = new WrittenHereOriginRestorationResult(0, 0, 0, 0, BUDGET);
    while (total.Batches < options.MaxReattributionBatchesPerPass) {
      var batch = await _batchAsync(database, walk, cancellationToken).ConfigureAwait(false);
      if (batch is null) {
        return total with { Status = CAUGHT_UP };
      }
      total = new WrittenHereOriginRestorationResult(total.Batches + 1, total.RestoredReactions + batch.Value.Reactions,
        total.RestoredReturns + batch.Value.Returns, total.Ambiguous + batch.Value.Ambiguous, BUDGET);
    }
    return total;
  }

  private sealed record Walk(string Prefix, string ServiceName, int BatchSize, StreamIntegrityMetrics? Metrics);

  // One batch, one transaction: the window, then the restore. Null when the walk is done.
  private async Task<(long Reactions, long Returns, long Ambiguous)?> _batchAsync(
      PostgresArchiveConnection database, Walk walk, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    long after, through;
    await using (var window = new NpgsqlCommand(
      $"SELECT after_commit_sequence, through_commit_sequence FROM {walk.Prefix}wh_written_here_restoration_window($1, $2)",
      connection, transaction)) {
      window.Parameters.AddWithValue(walk.ServiceName);
      window.Parameters.AddWithValue(walk.BatchSize);
      await using var reader = await window.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      (after, through) = (reader.GetInt64(0), reader.GetInt64(1));
    }
    if (through <= after) {
      await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
      return null;
    }
    await using var restore = new NpgsqlCommand(
      $"SELECT from_origin, restored_reactions, restored_returns, ambiguous_count FROM {walk.Prefix}wh_restore_written_here_origins($1, $2, $3)",
      connection, transaction);
    restore.Parameters.AddWithValue(after);
    restore.Parameters.AddWithValue(through);
    restore.Parameters.AddWithValue(walk.ServiceName);
    long reactions = 0, returns = 0, ambiguous = 0;
    await using (var reader = await restore.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        // The window holds the cursor for this transaction, so the restore always advances it; a NULL origin row is the
        // "nothing in this batch" answer.
        if (reader.IsDBNull(0)) {
          continue;
        }
        var origin = reader.GetGuid(0);
        var (reaction, returned, left) = (reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        reactions += reaction;
        returns += returned;
        ambiguous += left;
        _record(walk, origin, reaction, returned, left, after, through);
      }
    }
    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    return (reactions, returns, ambiguous);
  }

  private void _record(Walk walk, Guid origin, int reactions, int returns, int ambiguous, long after, long through) {
    if (reactions > 0) {
      walk.Metrics?.WrittenHereOriginsRestored.Add(reactions, new KeyValuePair<string, object?>("shape", SHAPE_REACTION));
    }
    if (returns > 0) {
      walk.Metrics?.WrittenHereOriginsRestored.Add(returns, new KeyValuePair<string, object?>("shape", SHAPE_RETURN));
    }
    if (reactions + returns > 0) {
      LogRestored(_logger, reactions, returns, origin, after, through);
    }
    if (ambiguous > 0) {
      walk.Metrics?.WrittenHereRestorationAmbiguous.Add(ambiguous);
      LogAmbiguous(_logger, ambiguous, origin, after, through);
    }
  }

  private WrittenHereOriginRestorationResult _skipped(string status) {
    LogSkipped(_logger, status);
    return new WrittenHereOriginRestorationResult(0, 0, 0, 0, status);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Returned {Reactions} reaction(s) and {Returns} round-tripped event(s) this service wrote, stored under origin " +
              "{Origin}, to the local lane (commit sequences {After} to {Through}); their digests, seals, held counts and " +
              "\"unknown\" answers follow (#2573)")]
  static partial void LogRestored(ILogger logger, int reactions, int returns, Guid origin, long after, long through);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Left {Count} unpositioned event(s) under origin {Origin} as ambiguous (commit sequences {After} to {Through}): " +
              "their origin disowned them or nothing records their first hop's service, so nothing proves this service " +
              "wrote them (#2573)")]
  static partial void LogAmbiguous(ILogger logger, int count, Guid origin, long after, long through);

  [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Written-here origin restoration {Status}")]
  static partial void LogSkipped(ILogger logger, string status);
}
