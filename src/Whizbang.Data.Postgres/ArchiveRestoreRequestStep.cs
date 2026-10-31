// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// It runs in the maintenance cycle, so under the maintainer duty when role assignment manages it. It resolves the
/// database connection (<see cref="PostgresArchiveConnection"/>) and the <see cref="IArchiveStore"/> from the provider
/// each run is handed, as the other maintenance steps do, so building the step (enumerating the maintenance steps) needs
/// neither: a host whose context has no Postgres data source can still list its steps.
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

  private readonly TimeProvider _timeProvider;
  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="timeProvider">The clock the request times are written from.</param>
  /// <param name="logger">Logs each restore's outcome.</param>
  public ArchiveRestoreRequestStep(TimeProvider timeProvider, ILogger<ArchiveRestoreRequestStep> logger) {
    ArgumentNullException.ThrowIfNull(timeProvider);
    ArgumentNullException.ThrowIfNull(logger);
    _timeProvider = timeProvider;
    _logger = logger;
  }

  /// <summary>
  /// Builds the step from a host's services, as both drivers register it: on the host's <see cref="TimeProvider"/> when
  /// one is registered and the system clock otherwise, logging to the host's logger when it has logging and discarding
  /// the log when it has none. A host can therefore list its maintenance steps whatever it registers.
  /// </summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <docs>fundamentals/events/archive-store#restore-requests</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRestoreRequestSqlTests.cs:FromServices_WithAHostClockAndLogger_WritesTheRequestTimesFromThatClock_AndLogsTheOutcomeToThatLoggerAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRestoreRequestSqlTests.cs:FromServices_WithNoClockOrLogger_RunsOnTheSystemClock_WithoutLoggingAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRestoreRequestSqlTests.cs:FromServices_RefusesNoServicesAsync</tests>
  public static ArchiveRestoreRequestStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new ArchiveRestoreRequestStep(
      services.GetService<TimeProvider>() ?? TimeProvider.System,
      services.GetService<ILogger<ArchiveRestoreRequestStep>>() ?? NullLogger<ArchiveRestoreRequestStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => "archive-restore-requests";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var store = services.GetRequiredService<IArchiveStore>();
    foreach (var streamId in await _claimAsync(database, cancellationToken).ConfigureAwait(false)) {
      string? failure;
      try {
        var result = await store.RestoreAsync(streamId, cancellationToken).ConfigureAwait(false);
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
      if (await _failAsync(database, streamId, failure, cancellationToken).ConfigureAwait(false)) {
        LogRestoreFailed(_logger, streamId, failure);
      }
    }
  }

  private static string _prefix(PostgresArchiveConnection database) =>
    database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";

  private async Task<List<Guid>> _claimAsync(PostgresArchiveConnection database, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT stream_id FROM {_prefix(database)}wh_claim_archive_restore_requests($1, $2)", connection);
    command.Parameters.AddWithValue(BATCH_SIZE);
    command.Parameters.AddWithValue(_timeProvider.GetUtcNow());
    var claimed = new List<Guid>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      claimed.Add(reader.GetGuid(0));
    }
    return claimed;
  }

  private async Task<bool> _failAsync(PostgresArchiveConnection database, Guid streamId, string error, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT {_prefix(database)}wh_fail_archive_restore_request($1, $2, $3)", connection);
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
