using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Fills promoted columns for rows that hold the value only in the document (issue #1009).
/// </summary>
/// <remarks>
/// <para>
/// The start that promotes a field adds its column and fills it from the document. During a rolling deploy
/// an instance still on the previous release keeps writing, and it does not know the column: the rows it
/// writes carry the value in the document and null in the column. Queries on the field read the column, so a
/// filter, sort or count misses or misplaces those rows until each one's next event.
/// </para>
/// <para>
/// The schema pass arms each column it adds in <c>wh_physical_column_fills</c> (migration 179), and this
/// fills the armed columns, a bounded batch per column per round, until every column's batch comes up short
/// or the round limit is reached. A row's column is only ever set where it is null, so a value a writer
/// stored is never replaced, and running this again finds nothing. A column stays armed until a round finds
/// nothing once the settle window has passed, which is how the rows written by the last instance of the
/// previous release are caught however long the rollout takes.
/// </para>
/// <para>
/// Filling the column is chosen over reading <c>COALESCE(column, extraction)</c> for a promoted field: that
/// expression is not the column, so a filter on it cannot use the column's index and the promotion would
/// buy nothing.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#rows-written-during-a-rolling-deploy</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalColumnFillMaintenanceStepTests.cs</tests>
public static partial class PhysicalColumnFill {
  /// <summary>The rows one batch fills in one column unless told otherwise.</summary>
  public const int DEFAULT_BATCH_SIZE = 1000;

  /// <summary>The batches one call runs per column unless told otherwise.</summary>
  public const int DEFAULT_MAX_ROUNDS = 20;

  /// <summary>
  /// How long a column stays armed unless told otherwise: longer than a rolling deploy takes, so the last
  /// instance of the previous release has stopped writing before the column stops being watched.
  /// </summary>
  public static readonly TimeSpan DefaultSettle = TimeSpan.FromDays(1);

  /// <summary>Whether any column is armed.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="quotedSchema">The schema the fills table lives in, quoted as an identifier.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns><see langword="true"/> when at least one column is armed.</returns>
  public static async Task<bool> AnyArmedAsync(
      NpgsqlConnection connection, string quotedSchema, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentException.ThrowIfNullOrWhiteSpace(quotedSchema);
    await using var command = new NpgsqlCommand(
      $"SELECT EXISTS (SELECT 1 FROM {quotedSchema}.wh_physical_column_fills)", connection);
    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }

  /// <summary>
  /// Fills the armed columns, a batch per column per round, until no column's batch is full or
  /// <paramref name="maxRounds"/> rounds have run.
  /// </summary>
  /// <param name="connection">An open connection, not in a transaction: each round commits on its own.</param>
  /// <param name="quotedSchema">The schema the fills table lives in, quoted as an identifier.</param>
  /// <param name="batchSize">The most rows one round fills in one column.</param>
  /// <param name="maxRounds">The most rounds this call runs.</param>
  /// <param name="settle">How long a column stays armed after the pass that added it.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The rows filled, in total.</returns>
  public static async Task<long> RunAsync(
      NpgsqlConnection connection, string quotedSchema, int batchSize, int maxRounds, TimeSpan settle,
      ILogger? logger, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentException.ThrowIfNullOrWhiteSpace(quotedSchema);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
    var log = logger ?? NullLogger.Instance;
    long total = 0;
    for (var round = 0; round < maxRounds; round++) {
      var more = false;
      await using var command = new NpgsqlCommand(
        $"SELECT fill_table, fill_column, filled, disarmed FROM {quotedSchema}.wh_fill_physical_columns($1, $2)",
        connection);
      command.Parameters.AddWithValue(batchSize);
      command.Parameters.Add(new NpgsqlParameter { Value = settle, NpgsqlDbType = NpgsqlDbType.Interval });
      await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var table = reader.GetString(0);
        var column = reader.GetString(1);
        if (await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)) {
          FillFailed(log, table, column);
          continue;
        }
        var filled = reader.GetInt64(2);
        total += filled;
        more |= filled >= batchSize;
        if (filled > 0) {
          Filled(log, filled, table, column);
        }
        if (reader.GetBoolean(3)) {
          Disarmed(log, table, column);
        }
      }
      if (!more) {
        break;
      }
    }
    return total;
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Filled {Count} rows of {Table}.{Column} whose value was only in the document")]
  private static partial void Filled(ILogger logger, long count, string table, string column);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "{Table}.{Column} has no rows left to fill and has settled; it is no longer watched")]
  private static partial void Disarmed(ILogger logger, string table, string column);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Could not fill {Table}.{Column} from the document; the next run tries again")]
  private static partial void FillFailed(ILogger logger, string table, string column);
}
