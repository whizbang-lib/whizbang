// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.MultiStream;
using Whizbang.Core.Notifications;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.Postgres.MultiStream;

/// <summary>
/// The Postgres <see cref="IMultiStreamCommandStore"/>, over the functions of migration 208. Each call is one statement on
/// its own connection, opened the way the notification stores open theirs, so it is safe behind a transaction pooler.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#storage</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:RecordRunAsync_ANewRun_IsPendingForEveryStreamAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:ClaimAsync_UnderAPublishedAssignment_EachInstanceClaimsOnlyItsPartitionShareAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:CompleteAsync_TheLastStream_CompletesTheRunOnceAsync</tests>
public sealed class PgMultiStreamCommandStore : IMultiStreamCommandStore {
  /// <summary>The channel a newly recorded run is announced on; the payload is the run id.</summary>
  public const string CHANNEL = MultiStreamCommandWorker.CHANNEL;

  private readonly WhizbangNotificationOptions _options;
  private readonly IConfiguration _configuration;
  private readonly int _partitionCount;
  private readonly INotificationConnectionStringFallback? _connectionStringFallback;
  private readonly INotificationDataSource? _notificationDataSource;

  /// <summary>Creates the store.</summary>
  /// <param name="options">Where the connection comes from.</param>
  /// <param name="configuration">Configuration, for the connection string fallback.</param>
  /// <param name="claimOptions">The work claim's options. Their partition count is used, so a stream's partition here is
  /// the one its live work is claimed by; the default count applies when they are not registered.</param>
  /// <param name="connectionStringFallback">An optional connection string fallback.</param>
  /// <param name="notificationDataSource">An optional data source to open connections from.</param>
  public PgMultiStreamCommandStore(
      IOptions<WhizbangNotificationOptions> options,
      IConfiguration configuration,
      IOptions<ClaimWorkerOptions>? claimOptions = null,
      INotificationConnectionStringFallback? connectionStringFallback = null,
      INotificationDataSource? notificationDataSource = null) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(configuration);
    _options = options.Value;
    _configuration = configuration;
    _partitionCount = claimOptions?.Value.PartitionCount ?? new ClaimWorkerOptions().PartitionCount;
    _connectionStringFallback = connectionStringFallback;
    _notificationDataSource = notificationDataSource;
  }

  /// <inheritdoc/>
  public async Task<bool> RecordRunAsync(MultiStreamRun run, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(run);
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using var connectionScope = connection.ConfigureAwait(false);
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT wh_record_multi_stream_run(@run_id, @type, @payload, @scope, @streams, @partitions)";
    cmd.Parameters.AddWithValue("run_id", run.RunId);
    cmd.Parameters.AddWithValue("type", run.CommandType);
    cmd.Parameters.AddWithValue("payload", run.Payload);
    cmd.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Text) { Value = (object?)run.Scope ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("streams", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = run.StreamIds.ToArray() });
    cmd.Parameters.AddWithValue("partitions", _partitionCount);
    return ScalarResult.IsTrue(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
  }

  /// <inheritdoc/>
  public async Task<IReadOnlyList<MultiStreamWorkItem>> ClaimAsync(MultiStreamClaim claim, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(claim);
    var items = new List<MultiStreamWorkItem>();
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT run_id, stream_id, command_type, payload, scope, attempt "
        + "FROM wh_claim_multi_stream_work(@instance, @batch, @lease, @epoch, @revision)";
      cmd.Parameters.AddWithValue("instance", claim.InstanceId);
      cmd.Parameters.AddWithValue("batch", claim.BatchSize);
      cmd.Parameters.AddWithValue("lease", (int)Math.Ceiling(claim.Lease.TotalSeconds));
      cmd.Parameters.Add(new NpgsqlParameter("epoch", NpgsqlDbType.Bigint) { Value = (object?)claim.Assignment?.Epoch ?? DBNull.Value });
      cmd.Parameters.Add(new NpgsqlParameter("revision", NpgsqlDbType.Bigint) { Value = (object?)claim.Assignment?.Revision ?? DBNull.Value });
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      // The claim sends a run's payload on its first row only (212), and each run's rows share that one string, so a
      // batch holds one copy of each run's payload however many of its streams it claimed. A row with a payload of its
      // own (a claim from before 212) is read once per run too.
      var payloads = new Dictionary<Guid, string>();
      var rows = new List<(Guid RunId, Guid StreamId, string CommandType, string? Scope, int Attempt)>();
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var runId = reader.GetGuid(0);
        if (!payloads.ContainsKey(runId) && !reader.IsDBNull(3)) {
          payloads[runId] = reader.GetString(3);
        }
        rows.Add((runId, reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5)));
      }
      foreach (var row in rows) {
        items.Add(new MultiStreamWorkItem(
          RunId: row.RunId,
          StreamId: row.StreamId,
          CommandType: row.CommandType,
          // Every run's first row carries its payload, so the lookup always finds it
          Payload: payloads[row.RunId],
          Scope: row.Scope,
          Attempt: row.Attempt));
      }
    }
    return items;
  }

  /// <inheritdoc/>
  public async Task<IReadOnlyList<MultiStreamRunCompletion>> CompleteAsync(
      Guid instanceId, IReadOnlyList<MultiStreamOutcome> outcomes, int maxAttempts, TimeSpan releaseDelay,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(outcomes);
    var completions = new List<MultiStreamRunCompletion>();
    if (outcomes.Count == 0) {
      return completions;
    }
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT run_id, command_type, stream_count, succeeded, failed "
        + "FROM wh_complete_multi_stream_work(@instance, @runs, @streams, @errors, @released, @attempts, @release)";
      cmd.Parameters.AddWithValue("instance", instanceId);
      cmd.Parameters.Add(new NpgsqlParameter("runs", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = outcomes.Select(o => o.RunId).ToArray() });
      cmd.Parameters.Add(new NpgsqlParameter("streams", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = outcomes.Select(o => o.StreamId).ToArray() });
      cmd.Parameters.Add(new NpgsqlParameter("errors", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = outcomes.Select(o => o.Error).ToArray() });
      cmd.Parameters.Add(new NpgsqlParameter("released", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = outcomes.Select(o => o.Released).ToArray() });
      cmd.Parameters.AddWithValue("attempts", maxAttempts);
      cmd.Parameters.AddWithValue("release", (int)Math.Ceiling(releaseDelay.TotalSeconds));
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        completions.Add(new MultiStreamRunCompletion(
          RunId: reader.GetGuid(0),
          CommandType: reader.GetString(1),
          StreamCount: reader.GetInt32(2),
          Succeeded: reader.GetInt32(3),
          Failed: reader.GetInt32(4)));
      }
    }
    return completions;
  }

  /// <inheritdoc/>
  public async Task<MultiStreamRunStatus?> ReadRunAsync(Guid runId, CancellationToken cancellationToken) {
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using var connectionScope = connection.ConfigureAwait(false);
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT run_id, command_type, stream_count, succeeded, failed, pending, created_at, completed_at "
      + "FROM wh_read_multi_stream_run(@run)";
    cmd.Parameters.AddWithValue("run", runId);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      return null;
    }
    return new MultiStreamRunStatus(
      RunId: reader.GetGuid(0),
      CommandType: reader.GetString(1),
      StreamCount: reader.GetInt32(2),
      Succeeded: reader.GetInt32(3),
      Failed: reader.GetInt32(4),
      Pending: reader.GetInt32(5),
      CreatedAt: reader.GetFieldValue<DateTimeOffset>(6),
      CompletedAt: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));
  }

  /// <inheritdoc/>
  public async Task<int> PurgeCompletedAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken) {
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using var connectionScope = connection.ConfigureAwait(false);
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT wh_purge_multi_stream_runs(@before)";
    cmd.Parameters.AddWithValue("before", completedBefore);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
  }

  private async ValueTask<NpgsqlConnection> _openAsync(CancellationToken cancellationToken) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    return await NotificationConnectionPlan.Create(_notificationDataSource, resolution).OpenAsync(cancellationToken).ConfigureAwait(false);
  }
}
