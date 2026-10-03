using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The Postgres <see cref="IStreamPurger"/>, shared by both drivers and by <c>whizbang streams purge</c>. Each batch
/// is one transaction that takes the batch's <c>PublishOnceAsync</c> claim (<c>wh_unique_emission_claims</c>) and
/// then runs <c>wh_purge_streams</c> (migration 180), so only one instance ever runs a batch and a crash rolls the
/// claim back with the batch. A dry run takes no claim and changes nothing.
/// </summary>
/// <param name="openConnection">Opens a connection to the service's database.</param>
/// <param name="schema">The schema, unquoted; null resolves the tables through the connection's search path, as
/// the driver's other stores do.</param>
/// <param name="logger">Logs each batch.</param>
/// <docs>operations/infrastructure/purging-streams</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Operations/StreamPurgeTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamPurgeTests.cs</tests>
public sealed partial class PostgresStreamPurger(
  Func<CancellationToken, ValueTask<NpgsqlConnection>> openConnection,
  string? schema = null,
  ILogger<PostgresStreamPurger>? logger = null) : IStreamPurger {

  /// <summary>The claim key prefix, so the claim prune and an operator can tell purge claims apart.</summary>
  public const string CLAIM_KEY_PREFIX = "whizbang:stream-purge:";

  /// <inheritdoc />
  public async Task<StreamPurgeReport> PurgeAsync(StreamPurgeRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
    return await RunAsync(connection, schema, request, logger, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>The claim a batch takes: one per (purge, batch), so a resumed purge skips what it committed.</summary>
  /// <param name="purgeId">The purge.</param>
  /// <param name="batchIndex">The batch's position in the request.</param>
  public static string ClaimKey(Guid purgeId, int batchIndex) =>
    $"{CLAIM_KEY_PREFIX}{purgeId:N}:{batchIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

  /// <summary>
  /// Runs a purge on an open connection: one transaction per batch.
  /// </summary>
  /// <param name="connection">An open connection, with no transaction in progress.</param>
  /// <param name="schema">The schema, unquoted; null uses the connection's search path.</param>
  /// <param name="request">The streams, who asks, and why.</param>
  /// <param name="logger">Logs each batch.</param>
  /// <param name="cancellationToken">Cancellation token. A batch that has committed stays committed.</param>
  public static async Task<StreamPurgeReport> RunAsync(
      NpgsqlConnection connection,
      string? schema,
      StreamPurgeRequest request,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(request);
    var batches = request.Batches();
    var prefix = schema is null ? string.Empty : SqlText.Identifier(schema) + ".";
    var results = new List<StreamPurgeBatch>(batches.Count);

    for (var index = 0; index < batches.Count; index++) {
      var ids = batches[index];
      await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
      if (!request.DryRun && !await _claimAsync(connection, transaction, prefix, request.PurgeId, index, cancellationToken).ConfigureAwait(false)) {
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        _reportSkipped(logger, request.PurgeId, index);
        results.Add(new StreamPurgeBatch(index, ids, Ran: false, new Dictionary<string, long>(StringComparer.Ordinal)));
        continue;
      }

      var rows = await _purgeAsync(connection, transaction, prefix, request, index, ids, cancellationToken).ConfigureAwait(false);
      // A dry run changes nothing either way; rolling it back keeps it from holding anything it read.
      if (request.DryRun) {
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
      } else {
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
      }
      _reportRan(logger, request, index, ids.Count, rows);
      results.Add(new StreamPurgeBatch(index, ids, Ran: true, rows));
    }

    return new StreamPurgeReport(request.PurgeId, request.DryRun, results);
  }

  private static async Task<bool> _claimAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, string prefix, Guid purgeId, int index,
      CancellationToken cancellationToken) {
    await using var claim = new NpgsqlCommand(
      $"INSERT INTO {prefix}wh_unique_emission_claims (claim_key, claimed_by_event_id) VALUES ($1, $2) ON CONFLICT (claim_key) DO NOTHING",
      connection, transaction);
    claim.Parameters.AddWithValue(ClaimKey(purgeId, index));
    claim.Parameters.AddWithValue(purgeId);
    return await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "One batch of the purge: the connection and its transaction, where the tables live, the request, and which batch.")]
  private static async Task<Dictionary<string, long>> _purgeAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, string prefix, StreamPurgeRequest request, int index,
      IReadOnlyList<Guid> ids, CancellationToken cancellationToken) {
    await using var purge = new NpgsqlCommand(
      $"SELECT purged_table, purged_rows FROM {prefix}wh_purge_streams($1, $2, $3, $4, $5, $6)",
      connection, transaction);
    purge.Parameters.Add(new NpgsqlParameter { Value = ids.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    purge.Parameters.AddWithValue(request.DryRun);
    purge.Parameters.AddWithValue(request.PurgeId);
    purge.Parameters.AddWithValue(index);
    purge.Parameters.AddWithValue(request.RequestedBy);
    purge.Parameters.AddWithValue(request.Reason);

    var rows = new Dictionary<string, long>(StringComparer.Ordinal);
    await using var reader = await purge.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      rows[reader.GetString(0)] = reader.GetInt64(1);
    }
    return rows;
  }

  private static void _reportRan(ILogger? logger, StreamPurgeRequest request, int index, int streamCount, Dictionary<string, long> rows) {
    if (logger is not null && logger.IsEnabled(LogLevel.Information)) {
      LogBatch(logger, request.DryRun ? "Dry run of" : "Purged", request.PurgeId, index, streamCount,
        rows.GetValueOrDefault("wh_event_store"), $"{request.RequestedBy}: {request.Reason}");
    }
  }

  private static void _reportSkipped(ILogger? logger, Guid purgeId, int index) {
    if (logger is not null) {
      LogBatchAlreadyClaimed(logger, purgeId, index);
    }
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "{Action} stream purge {PurgeId} batch {BatchIndex}: {StreamCount} stream(s), {EventCount} event(s); requested by {Request}")]
  static partial void LogBatch(ILogger logger, string action, Guid purgeId, int batchIndex, int streamCount, long eventCount, string request);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Stream purge {PurgeId} batch {BatchIndex} is already claimed (another instance, or an earlier run of this purge); skipped")]
  static partial void LogBatchAlreadyClaimed(ILogger logger, Guid purgeId, int batchIndex);
}
