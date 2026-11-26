// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres.TestSupport;

/// <summary>
/// Publishes a partition map straight to the database, the way the elected assigner does: the assigner instance is
/// registered, wins the assigner's role, and publishes under that role's epoch (#2559). The assigner always runs in
/// every driver, so a test that needs a known map publishes one here rather than turning the assigner off; one helper,
/// so no fixture hand-rolls the vote and the publish. Shared by the Dapper and EF Core test projects.
/// </summary>
internal static class PublishedPartitionMap {
  /// <summary>The lease a map published here holds for.</summary>
  public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(90);

  /// <summary>Registers <paramref name="assigner"/>, wins the assigner role for it, and publishes <paramref name="members"/>.</summary>
  /// <param name="connection">An open connection to the test database.</param>
  /// <param name="assigner">The instance that publishes; registered here as a pooled instance.</param>
  /// <param name="members">The map's members, in rank order.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The published version.</returns>
  public static async Task<PartitionAssignmentVersion> PublishAsync(
      NpgsqlConnection connection, Guid assigner, IReadOnlyList<Guid> members, CancellationToken cancellationToken = default) {
    var epoch = await ElectAsync(connection, assigner, cancellationToken);
    var revision = await PublishAtAsync(connection, assigner, epoch, members, DefaultLease, cancellationToken);
    return new PartitionAssignmentVersion(epoch, revision);
  }

  /// <summary>Registers <paramref name="instance"/> and wins the assigner role for it; returns the role's epoch.</summary>
  /// <param name="connection">An open connection to the test database.</param>
  /// <param name="instance">The instance.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The epoch of the tenure won.</returns>
  public static async Task<long> ElectAsync(NpgsqlConnection connection, Guid instance, CancellationToken cancellationToken = default) {
    await RegisterAsync(connection, instance, InstanceConnectionModes.POOLED, cancellationToken);
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT epoch FROM wh_vote_role(@role, @instance, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v "
      + "WHERE v.outcome = 'granted'";
    cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    cmd.Parameters.AddWithValue(nameof(instance), instance);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
  }

  /// <summary>Publishes <paramref name="members"/> under <paramref name="epoch"/>; returns the revision recorded.</summary>
  /// <param name="connection">An open connection to the test database.</param>
  /// <param name="instance">The publishing instance, which must hold the role at <paramref name="epoch"/>.</param>
  /// <param name="epoch">The role's epoch, which fences the publish.</param>
  /// <param name="members">The map's members, in rank order.</param>
  /// <param name="lease">How long the map holds without renewal.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The revision.</returns>
  public static async Task<long> PublishAtAsync(
      NpgsqlConnection connection, Guid instance, long epoch, IReadOnlyList<Guid> members, TimeSpan lease,
      CancellationToken cancellationToken = default) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @instance, @epoch, @members, @lease)";
    cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    cmd.Parameters.AddWithValue(nameof(instance), instance);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(members), NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = members.ToArray() });
    cmd.Parameters.Add(new NpgsqlParameter(nameof(lease), NpgsqlDbType.Interval) { Value = lease });
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
  }

  /// <summary>Registers <paramref name="instance"/> with a fresh heartbeat in <paramref name="mode"/>.</summary>
  /// <param name="connection">An open connection to the test database.</param>
  /// <param name="instance">The instance.</param>
  /// <param name="mode">Its connection mode, or null to keep what is recorded.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public static async Task RegisterAsync(NpgsqlConnection connection, Guid instance, string? mode, CancellationToken cancellationToken = default) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT record_heartbeat(@instance, 'test', 'test-host', 1, '{}'::jsonb, NULL, NULL, NULL, @mode)";
    cmd.Parameters.AddWithValue(nameof(instance), instance);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(mode), NpgsqlDbType.Text) { Value = (object?)mode ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }
}
