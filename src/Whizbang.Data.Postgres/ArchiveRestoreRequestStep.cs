// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Archival;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The maintenance step that performs queued restores (#1738, WS-3 S9). An append nobody awaits to a stream archived
/// whole, of a type whose policy is <see cref="AppendToArchivedPolicy.AutoRestore"/>, is dead-lettered and queues the
/// stream's restore request in the same transaction. Each cycle this step claims up to <see cref="BATCH_SIZE"/> open
/// requests (oldest first, Restoring from then on) and restores each stream through the registered
/// <see cref="IArchiveStore"/>, so a table archive and a blob archive restore the same way.
/// </summary>
/// <remarks>
/// <para>
/// The outcome is decided by state, never by text. A restore that puts the stream back completes the request inside the
/// store's own SQL (<c>wh_restore_archived_stream</c>): the request reads Restored and the stream's AwaitingRestore dead
/// letters are made due, so the dead-letter recovery worker redrives them. Any other outcome fails the request with the
/// store's status as its error (<c>nothing_archived</c> when the archive was deleted, <c>version_conflict</c>), or the
/// exception's type and message when the store threw; its dead letters are then held for review. One stream's failure
/// never holds back another's restore.
/// </para>
/// <para>
/// It runs in the maintenance cycle, so under the maintainer duty when role assignment manages it.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/archive-store#restore-requests</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRestoreRequestSqlTests.cs:Step_RestoresEachQueuedStream_AndItsDeadLettersBecomeDueAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRestoreRequestSqlTests.cs:Step_AnArchiveThatIsGone_FailsTheRequest_WithTheStoresStatusAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRestoreRequestSqlTests.cs:Step_ARestoreThatThrows_FailsThatRequest_AndStillRestoresTheNextAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveAppendPolicyTests.cs:AddWhizbangPostgres_RegistersTheRestoreRequestStep_AsAMaintenanceStepAsync</tests>
public sealed partial class ArchiveRestoreRequestStep : IMaintenanceStep {
  /// <summary>
  /// The most requests one cycle restores. A restore copies a whole stream back, so a burst of requests drains over a few
  /// cycles rather than holding one cycle for a long run of restores.
  /// </summary>
  public const int BATCH_SIZE = 20;

  private readonly PostgresArchiveConnection _database;
  private readonly IArchiveStore _store;
  private readonly TimeProvider _timeProvider;
  private readonly ILogger _logger;
  private readonly string _prefix;

  /// <summary>Creates the step.</summary>
  /// <param name="database">How the step reaches the service's database (the requests).</param>
  /// <param name="store">The archive store that restores a stream.</param>
  /// <param name="timeProvider">The clock the request times are written from.</param>
  /// <param name="logger">Logs each restore's outcome.</param>
  public ArchiveRestoreRequestStep(
      PostgresArchiveConnection database, IArchiveStore store, TimeProvider timeProvider, ILogger<ArchiveRestoreRequestStep> logger) {
    ArgumentNullException.ThrowIfNull(database);
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(timeProvider);
    ArgumentNullException.ThrowIfNull(logger);
    _database = database;
    _store = store;
    _timeProvider = timeProvider;
    _logger = logger;
    _prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
  }

  /// <inheritdoc />
  public string Name => "archive-restore-requests";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    foreach (var streamId in await _claimAsync(cancellationToken).ConfigureAwait(false)) {
      string? failure;
      try {
        var result = await _store.RestoreAsync(streamId, cancellationToken).ConfigureAwait(false);
        failure = string.Equals(result.Status, ArchiveStatus.RESTORED, StringComparison.Ordinal) ? null : result.Status;
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        throw;
      } catch (Exception ex) {
        failure = $"{ex.GetType().Name}: {ex.Message}";
        LogRestoreThrew(_logger, streamId, ex);
      }
      if (failure is null) {
        LogRestored(_logger, streamId);
        continue;
      }
      // A nothing_archived for a stream another restore already put back was completed by that restore's SQL, so the
      // request is no longer open and this fails nothing.
      if (await _failAsync(streamId, failure, cancellationToken).ConfigureAwait(false)) {
        LogRestoreFailed(_logger, streamId, failure);
      }
    }
  }

  private async Task<List<Guid>> _claimAsync(CancellationToken cancellationToken) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT stream_id FROM {_prefix}wh_claim_archive_restore_requests($1, $2)", connection);
    command.Parameters.AddWithValue(BATCH_SIZE);
    command.Parameters.AddWithValue(_timeProvider.GetUtcNow());
    var claimed = new List<Guid>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      claimed.Add(reader.GetGuid(0));
    }
    return claimed;
  }

  private async Task<bool> _failAsync(Guid streamId, string error, CancellationToken cancellationToken) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT {_prefix}wh_fail_archive_restore_request($1, $2, $3)", connection);
    command.Parameters.AddWithValue(streamId);
    command.Parameters.AddWithValue(error);
    command.Parameters.AddWithValue(_timeProvider.GetUtcNow());
    return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }

  [LoggerMessage(Level = LogLevel.Information, Message = "Restored archived stream {StreamId} for its queued restore request; its awaiting dead letters are due for recovery")]
  static partial void LogRestored(ILogger logger, Guid streamId);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Restore request for archived stream {StreamId} failed ({Error}); its awaiting dead letters are held for review")]
  static partial void LogRestoreFailed(ILogger logger, Guid streamId, string error);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Restoring archived stream {StreamId} for its queued restore request threw")]
  static partial void LogRestoreThrew(ILogger logger, Guid streamId, Exception exception);
}
