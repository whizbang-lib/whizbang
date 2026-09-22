using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Observability;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Durable advisory ledger, backed by <c>wh_advisory_ledger</c>.
/// </summary>
/// <remarks>
/// <para>
/// Replaces per-process memory with a row per finding, which changes two things memory could not.
/// The record survives a restart, so a deployment that restarts on a schedule no longer receives
/// the same advice on that schedule forever. And the row is shared, so concurrent replicas cannot
/// each raise the same finding: the backing function decides and records in one statement, and
/// exactly one caller is told to proceed.
/// </para>
/// <para>
/// Degrades to the process-local ledger rather than to a decision. A database this cannot reach is
/// a reason to fall back to what the framework did before this existed, which is advice once per
/// process: failing open would restore the storm this replaces, and failing closed would silence
/// a real finding for as long as the fault lasts. The fallback instance is held for the lifetime of
/// this one so a persistent fault degrades to once-per-process rather than to once-per-cycle.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AdvisoryLedgerSqlTests.cs</tests>
public sealed partial class PostgresAdvisoryLedger(
    NpgsqlDataSource dataSource,
    string schema = "public",
    ILogger<PostgresAdvisoryLedger>? logger = null) : IAdvisoryLedger {

  private readonly ILogger _log = logger
    ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresAdvisoryLedger>.Instance;

  private readonly AdvisoryLedger _fallback = new();

  /// <inheritdoc />
  public async ValueTask<bool> TryBeginReportAsync(
      string findingKey, string signature, DateTimeOffset now, TimeSpan cooldown,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(findingKey);
    ArgumentNullException.ThrowIfNull(signature);

    try {
      await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
        .ConfigureAwait(false);

      // Schema-qualified from the schema this service owns, the same way the statistics provider
      // qualifies its catalog reads: a co-located service must consult ITS ledger, not whichever
      // one the search path happens to reach first.
      await using var cmd = new NpgsqlCommand(
        $"SELECT {schema}.wh_advisory_try_begin_report(@key, @signature, @now, @cooldown)",
        connection);

      cmd.Parameters.AddWithValue("key", findingKey);
      cmd.Parameters.AddWithValue(nameof(signature), signature);
      cmd.Parameters.AddWithValue(nameof(now), now);
      cmd.Parameters.AddWithValue(nameof(cooldown), cooldown);

      var answer = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

      if (answer is bool granted) {
        return granted;
      }

      // The shipped function is declared RETURNS BOOLEAN over a body that always returns, so this
      // cannot happen against the schema this ships with. It can against a schema carrying
      // something else under that name, and the safe reading of a non-answer is trouble rather than
      // "already reported": answering false here would suppress the finding for a whole cooldown on
      // the strength of an answer nobody gave.
      LogLedgerAnsweredNothing(_log, schema);

      return await _fallback.TryBeginReportAsync(findingKey, signature, now, cooldown, cancellationToken)
        .ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) when (ex is NpgsqlException or System.Data.Common.DbException or InvalidOperationException) {
      // Logged, never silent. A ledger that has quietly stopped remembering anything looks exactly
      // like a deployment with no findings, which is the reading this whole table exists to
      // prevent being wrong about.
      LogLedgerUnavailable(_log, ex);

      return await _fallback.TryBeginReportAsync(findingKey, signature, now, cooldown, cancellationToken)
        .ConfigureAwait(false);
    }
  }

  [LoggerMessage(
    EventId = 72,
    Level = LogLevel.Warning,
    Message = "The advisory ledger in schema '{Schema}' answered something other than yes or no, so "
        + "advice is being suppressed per process instead. Whatever carries that name there is not "
        + "the function this expects.")]
  static partial void LogLedgerAnsweredNothing(ILogger logger, string Schema);

  [LoggerMessage(
    EventId = 71,
    Level = LogLevel.Warning,
    Message = "The advisory ledger could not be reached, so advice is being suppressed per process "
        + "for as long as this lasts: the same finding may be reported again after a restart, and "
        + "once by each replica.")]
  static partial void LogLedgerUnavailable(ILogger logger, Exception exception);
}
