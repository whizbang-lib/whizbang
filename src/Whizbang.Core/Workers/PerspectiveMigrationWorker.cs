using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background hosted service that processes migration-triggered perspective rebuilds.
/// On startup, checks wh_schema_migrations for status 4 (MigratingInBackground) entries
/// and calls IPerspectiveRebuilder.RebuildBlueGreenAsync for each.
/// Updates migration status to 2 (Updated) on completion.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild</docs>
public sealed partial class PerspectiveMigrationWorker(
    IPerspectiveRebuilder rebuilder,
    ILogger<PerspectiveMigrationWorker> logger) : BackgroundService {

  /// <summary>
  /// Callback to query pending migration rebuilds (status 4) from the database.
  /// Set by the hosting infrastructure during registration.
  /// Returns list of (perspectiveName, migrationKey) pairs.
  /// </summary>
  public Func<CancellationToken, Task<IReadOnlyList<PendingMigrationRebuild>>>? GetPendingRebuilds { get; set; }

  /// <summary>
  /// Callback to update migration status after rebuild completes.
  /// Set by the hosting infrastructure during registration.
  /// </summary>
  public Func<string, int, string, CancellationToken, Task>? UpdateMigrationStatus { get; set; }

  /// <inheritdoc/>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (GetPendingRebuilds == null || UpdateMigrationStatus == null) {
      return;
    }

    try {
      var pendingRebuilds = await GetPendingRebuilds(stoppingToken);

      if (pendingRebuilds.Count == 0) {
        return;
      }

      LogProcessingPending(logger, pendingRebuilds.Count);

      foreach (var pending in pendingRebuilds) {
        if (stoppingToken.IsCancellationRequested) {
          break;
        }

        try {
          LogRebuildStarting(logger, pending.PerspectiveName, pending.MigrationKey);

          var result = await rebuilder.RebuildBlueGreenAsync(pending.PerspectiveName, stoppingToken);

          if (result.Success) {
            var desc = $"Updated (rebuild completed: {result.StreamsProcessed} streams in {result.Duration})";
            await UpdateMigrationStatus(pending.MigrationKey, 2, desc, stoppingToken);
            LogRebuildCompleted(logger, pending.PerspectiveName, result.StreamsProcessed, result.Duration.TotalMilliseconds);
          } else {
            await UpdateMigrationStatus(pending.MigrationKey, -1, $"Failed: {result.Error}", stoppingToken);
            LogRebuildFailed(logger, pending.PerspectiveName, result.Error ?? "unknown");
          }
        } catch (Exception ex) {
          LogRebuildException(logger, ex, pending.PerspectiveName);

          try {
            await UpdateMigrationStatus(pending.MigrationKey, -1, $"Failed: {ex.Message}", stoppingToken);
          } catch {
            // Best effort status update
          }
        }
      }
    } catch (Exception ex) {
      LogWorkerFailed(logger, ex);
    }
  }

  /// <summary>Logs the number of pending migration rebuilds being processed.</summary>
  [LoggerMessage(Level = LogLevel.Information,
      Message = "PerspectiveMigrationWorker: processing {Count} pending migration rebuild(s)")]
  private static partial void LogProcessingPending(ILogger logger, int count);

  /// <summary>Logs the start of a migration rebuild for a specific perspective.</summary>
  [LoggerMessage(Level = LogLevel.Information,
      Message = "Starting migration rebuild for perspective {Perspective} (migration: {Migration})")]
  private static partial void LogRebuildStarting(ILogger logger, string perspective, string migration);

  /// <summary>Logs the successful completion of a migration rebuild.</summary>
  [LoggerMessage(Level = LogLevel.Information,
      Message = "Migration rebuild completed for {Perspective}: {Streams} streams in {ElapsedMs}ms")]
  private static partial void LogRebuildCompleted(ILogger logger, string perspective, int streams, double elapsedMs);

  /// <summary>Logs a migration rebuild failure with the error reason.</summary>
  [LoggerMessage(Level = LogLevel.Error,
      Message = "Migration rebuild failed for {Perspective}: {Error}")]
  private static partial void LogRebuildFailed(ILogger logger, string perspective, string error);

  /// <summary>Logs an exception thrown during migration rebuild.</summary>
  [LoggerMessage(Level = LogLevel.Error,
      Message = "Migration rebuild threw for perspective {Perspective}")]
  private static partial void LogRebuildException(ILogger logger, Exception ex, string perspective);

  /// <summary>Logs a failure in the migration worker's overall processing loop.</summary>
  [LoggerMessage(Level = LogLevel.Error,
      Message = "PerspectiveMigrationWorker failed to process pending rebuilds")]
  private static partial void LogWorkerFailed(ILogger logger, Exception ex);
}

/// <summary>
/// Represents a pending migration rebuild (status 4 in wh_schema_migrations).
/// </summary>
public sealed record PendingMigrationRebuild(
    string PerspectiveName,
    string MigrationKey);
