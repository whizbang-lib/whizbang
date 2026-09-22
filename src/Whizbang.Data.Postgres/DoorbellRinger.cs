using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Rings the doorbells a hot-path call queued (migration 146): runs <c>ring_doorbells()</c> as its own
/// autocommit statement on the connection the hot call just used, after that call's transaction has
/// committed. PostgreSQL serializes every notifying transaction's commit on one database-wide lock, so
/// the hot functions no longer call <c>pg_notify</c>; they queue, and this rings.
/// </summary>
/// <remarks>
/// A ring never fails the caller. The work the doorbell points at is already committed; a ring that
/// does not happen costs latency until the next ring from any instance or the claim poll, so a failure
/// is logged at Warning with the function name and swallowed. Rows another ringer holds are skipped by
/// the function itself (<c>FOR UPDATE SKIP LOCKED</c>), so two ringers never wait on each other.
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public static partial class DoorbellRinger {
  /// <summary>Command timeout for the ring. The statement touches a handful of rows in an unlogged table.</summary>
  private const int RING_TIMEOUT_SECONDS = 5;

  /// <summary>The bare name of the ring function; callers schema-qualify it where <c>search_path</c> does not resolve it.</summary>
#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  public const string FUNCTION_NAME = "ring_doorbells";
#pragma warning restore CA1707

  /// <summary>
  /// Rings queued doorbells on an OPEN connection. <paramref name="qualifiedFunctionName"/> is the
  /// schema-qualified <c>ring_doorbells</c> (or the bare name where the caller resolves through
  /// <c>search_path</c>). Returns the number of distinct notifications sent, or -1 when the ring failed.
  /// </summary>
  public static async Task<int> RingAsync(
      NpgsqlConnection connection,
      string qualifiedFunctionName,
      ILogger? logger,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(qualifiedFunctionName);
    try {
      await using var cmd = connection.CreateCommand();
      // The function name is the schema-qualified constant the caller built from its validated schema
      // (or the bare framework name on the Dapper side); it is never user input, and a function name
      // cannot be a bind parameter. Same pattern as the coordinators' schema-qualified calls.
#pragma warning disable S2077
      cmd.CommandText = $"SELECT {qualifiedFunctionName}()";
#pragma warning restore S2077
      cmd.CommandTimeout = RING_TIMEOUT_SECONDS;
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return result is null or DBNull ? 0 : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      // Shutdown: the queued doorbells are rung by the next ring from any instance.
      return -1;
    } catch (Exception ex) when (ex is not OutOfMemoryException) {
      if (logger is not null) {
        LogRingFailed(logger, ex, qualifiedFunctionName);
      }
      return -1;
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Doorbell ring via {Function} failed; the queued doorbells stay queued and ring on the next call from any instance, and the claim poll is the safety net")]
  private static partial void LogRingFailed(ILogger logger, Exception exception, string function);
}
