// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Notifications;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// The Postgres store behind the partition assigner (#1254): migration 203's
/// <c>wh_read_partition_assignment</c>, <c>wh_partition_assignment_candidates</c>,
/// <c>wh_publish_partition_assignment</c> and <c>wh_renew_partition_assignment</c>. Each call is one statement on a
/// connection of its own, so it works through a transaction pooler.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Store_PublishReadAndRenew_RoundTripAsync</tests>
public sealed class PgPartitionAssignmentStore : IPartitionAssignmentStore {
  private const string FENCE_SQLSTATE = "WHF01";

  private readonly WhizbangNotificationOptions _options;
  private readonly IConfiguration _configuration;
  private readonly INotificationConnectionStringFallback? _connectionStringFallback;
  private readonly INotificationDataSource? _notificationDataSource;

  /// <summary>Creates the store.</summary>
  /// <param name="options">Where the connection comes from.</param>
  /// <param name="configuration">Configuration, for connection resolution.</param>
  /// <param name="connectionStringFallback">Optional connection-string fallback.</param>
  /// <param name="notificationDataSource">Optional dedicated data source.</param>
  public PgPartitionAssignmentStore(
      IOptions<WhizbangNotificationOptions> options,
      IConfiguration configuration,
      INotificationConnectionStringFallback? connectionStringFallback = null,
      INotificationDataSource? notificationDataSource = null) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(configuration);
    _options = options.Value;
    _configuration = configuration;
    _connectionStringFallback = connectionStringFallback;
    _notificationDataSource = notificationDataSource;
  }

  /// <inheritdoc />
  public async Task<PartitionAssignmentRead?> ReadAsync(CancellationToken cancellationToken) {
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT epoch, revision, assigner_instance_id, members, published_at, lease_expires_at, lease_remaining "
        + "FROM wh_read_partition_assignment()";
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        return null;
      }
      var assignment = new PartitionAssignment(
        Epoch: reader.GetInt64(0),
        Revision: reader.GetInt64(1),
        AssignerInstanceId: reader.GetGuid(2),
        Members: reader.GetFieldValue<Guid[]>(3),
        PublishedAt: reader.GetFieldValue<DateTimeOffset>(4),
        LeaseExpiresAt: reader.GetFieldValue<DateTimeOffset>(5));
      return new PartitionAssignmentRead(assignment, reader.GetTimeSpan(6));
    }
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<PartitionAssignmentCandidate>> ReadCandidatesAsync(CancellationToken cancellationToken) {
    var candidates = new List<PartitionAssignmentCandidate>();
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT instance_id, connection_mode, alive_lock_held, heartbeat_age FROM wh_partition_assignment_candidates()";
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        candidates.Add(new PartitionAssignmentCandidate(
          InstanceId: reader.GetGuid(0),
          Mode: InstanceConnectionModes.Parse(reader.GetString(1)),
          AliveLockHeld: reader.GetBoolean(2),
          HeartbeatAge: reader.GetTimeSpan(3)));
      }
    }
    return candidates;
  }

  /// <inheritdoc />
  public async Task<PartitionAssignment?> PublishAsync(
      string role, Guid instanceId, long epoch, IReadOnlyList<Guid> members, TimeSpan lease, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrEmpty(role);
    ArgumentNullException.ThrowIfNull(members);
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @instance, @epoch, @members, @lease)";
      cmd.Parameters.AddWithValue(nameof(role), role);
      cmd.Parameters.AddWithValue("instance", instanceId);
      cmd.Parameters.AddWithValue(nameof(epoch), epoch);
      cmd.Parameters.Add(new NpgsqlParameter(nameof(members), NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = members.ToArray() });
      cmd.Parameters.Add(new NpgsqlParameter(nameof(lease), NpgsqlDbType.Interval) { Value = lease });
      try {
        _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      } catch (PostgresException ex) when (ex.SqlState == FENCE_SQLSTATE) {
        return null;
      }
    }
    // Read back what was recorded: the revision and the times are the database's.
    return (await ReadAsync(cancellationToken).ConfigureAwait(false))?.Assignment;
  }

  /// <inheritdoc />
  public async Task<bool> RenewAsync(Guid instanceId, long epoch, CancellationToken cancellationToken) {
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT wh_renew_partition_assignment(@instance, @epoch)";
      cmd.Parameters.AddWithValue("instance", instanceId);
      cmd.Parameters.AddWithValue(nameof(epoch), epoch);
      return ScalarResult.IsTrue(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }
  }

  private async ValueTask<NpgsqlConnection> _openAsync(CancellationToken cancellationToken) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    return await NotificationConnectionPlan.Create(_notificationDataSource, resolution).OpenAsync(cancellationToken).ConfigureAwait(false);
  }
}
