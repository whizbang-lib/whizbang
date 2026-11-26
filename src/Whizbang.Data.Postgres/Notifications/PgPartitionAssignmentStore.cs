// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
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
/// <c>wh_publish_partition_assignment</c> and <c>wh_renew_partition_assignment</c>, with the partition map added by
/// migration 564 (#2558) and migration 565's <c>wh_open_partition_ownership_by_map</c> (#2555). Each call is one
/// statement on a
/// connection of its own, so it works through a transaction pooler.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Store_PublishReadAndRenew_RoundTripAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_WithAMap_StoresIt_AndTheReadReturnsItAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_OfTheEmptyMap_StoresNoMembersAndNoMapAsync</tests>
public sealed class PgPartitionAssignmentStore : IPartitionAssignmentStore {
  private const string FENCE_SQLSTATE = "WHF01";
  private const string INSTANCE_PARAMETER = "instance";

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
    await using var connectionScope = connection.ConfigureAwait(false);
    await using var cmd = connection.CreateWhizbangCommand();
    cmd.CommandText = "SELECT epoch, revision, assigner_instance_id, members, published_at, lease_expires_at, lease_remaining, "
      + "partition_owners FROM wh_read_partition_assignment()";
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
      LeaseExpiresAt: reader.GetFieldValue<DateTimeOffset>(5),
      // #2558: the partition map; null for an assignment of the older shape, whose claimers rank by modulo.
      Owners: await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetFieldValue<short[]>(7));
    return new PartitionAssignmentRead(assignment, reader.GetTimeSpan(6));
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<PartitionAssignmentCandidate>> ReadCandidatesAsync(CancellationToken cancellationToken) {
    var candidates = new List<PartitionAssignmentCandidate>();
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateWhizbangCommand();
      cmd.CommandText = "SELECT instance_id, connection_mode, alive_lock_held, heartbeat_age, is_live FROM wh_partition_assignment_candidates()";
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        candidates.Add(new PartitionAssignmentCandidate(
          InstanceId: reader.GetGuid(0),
          Mode: InstanceConnectionModes.Parse(reader.GetString(1)),
          AliveLockHeld: reader.GetBoolean(2),
          HeartbeatAge: reader.GetTimeSpan(3),
          IsLive: reader.GetBoolean(4)));
      }
    }
    return candidates;
  }

  /// <inheritdoc />
  public async Task<PartitionAssignment?> PublishAsync(
      string role, Guid instanceId, long epoch, PartitionMap map, TimeSpan lease, TimeSpan handoverDeadline,
      CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrEmpty(role);
    ArgumentNullException.ThrowIfNull(map);
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateWhizbangCommand();
      cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @instance, @epoch, @members, @lease, @owners, @deadline)";
      cmd.Parameters.AddWithValue(nameof(role), role);
      cmd.Parameters.AddWithValue(INSTANCE_PARAMETER, instanceId);
      cmd.Parameters.AddWithValue(nameof(epoch), epoch);
      cmd.Parameters.Add(new NpgsqlParameter("members", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = map.Members.ToArray() });
      cmd.Parameters.Add(new NpgsqlParameter(nameof(lease), NpgsqlDbType.Interval) { Value = lease });
      // #2558: the map, one member index per partition; the empty map (no members) publishes none.
      cmd.Parameters.Add(new NpgsqlParameter("owners", NpgsqlDbType.Array | NpgsqlDbType.Smallint) {
        Value = map.Owners.Count == 0 ? DBNull.Value : (object)map.Owners.ToArray(),
      });
      // #2557: in map mode a partition moving away from a live owner hands over for at most this long.
      cmd.Parameters.Add(new NpgsqlParameter("deadline", NpgsqlDbType.Interval) { Value = handoverDeadline });
      try {
        _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      } catch (PostgresException ex) when (ex.SqlState == FENCE_SQLSTATE) {
        return null;
      }
    }
    // Read back what was recorded: the revision and the times are the database's. The publish just upserted the one
    // singleton row, and nothing deletes it, so the read finds it.
    var recorded = await ReadAsync(cancellationToken).ConfigureAwait(false);
    return recorded!.Assignment;
  }

  /// <inheritdoc />
  public async Task<bool> RenewAsync(Guid instanceId, long epoch, CancellationToken cancellationToken) {
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using var connectionScope = connection.ConfigureAwait(false);
    await using var cmd = connection.CreateWhizbangCommand();
    cmd.CommandText = "SELECT wh_renew_partition_assignment(@instance, @epoch)";
    cmd.Parameters.AddWithValue(INSTANCE_PARAMETER, instanceId);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    return ScalarResult.IsTrue(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Store_OpenOwnershipByMap_TurnsTheSwitchOn_AndAnswersNullToADeposedAssignerAsync</tests>
  public async Task<string?> OpenOwnershipByMapAsync(Guid instanceId, long epoch, CancellationToken cancellationToken) {
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using var connectionScope = connection.ConfigureAwait(false);
    await using var cmd = connection.CreateWhizbangCommand();
    cmd.CommandText = "SELECT wh_open_partition_ownership_by_map(@role, @instance, @epoch)";
    cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    cmd.Parameters.AddWithValue(INSTANCE_PARAMETER, instanceId);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    try {
      return (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    } catch (PostgresException ex) when (ex.SqlState == FENCE_SQLSTATE) {
      return null;
    }
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Store_CompleteExpiredHandovers_FlipsWhatIsPastItsDeadline_AndAnswersNullToADeposedAssignerAsync</tests>
  public async Task<int?> CompleteExpiredHandoversAsync(Guid instanceId, long epoch, CancellationToken cancellationToken) {
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using var connectionScope = connection.ConfigureAwait(false);
    await using var cmd = connection.CreateWhizbangCommand();
    cmd.CommandText = "SELECT wh_complete_expired_handovers(@role, @instance, @epoch)";
    cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    cmd.Parameters.AddWithValue(INSTANCE_PARAMETER, instanceId);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    try {
      return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    } catch (PostgresException ex) when (ex.SqlState == FENCE_SQLSTATE) {
      return null;
    }
  }

  private async ValueTask<NpgsqlConnection> _openAsync(CancellationToken cancellationToken) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    return await NotificationConnectionPlan.Create(_notificationDataSource, resolution).OpenAsync(cancellationToken).ConfigureAwait(false);
  }
}
