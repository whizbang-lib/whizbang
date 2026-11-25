// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The fleet and each instance's assignment, read the same way by both Postgres drivers (#2595): the EF Core driver
/// over its context's connection, the Dapper driver over its connection string. One reader, so the two cannot drift.
/// </summary>
/// <docs>operations/startup/startup-status#instance-assignments</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceAssignmentDriverParityTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupFleetStatusSourceTests.cs</tests>
internal static class PostgresFleetStatus {
  /// <summary>The function the assignment is read from (616).</summary>
  private const string ASSIGNMENTS_FUNCTION = "wh_instance_assignments()";

  /// <summary>
  /// Every registered instance (the 200 most recently heard from), with its holdings, liveness and, once the database
  /// has <c>wh_instance_assignments()</c>, its assignment and share.
  /// </summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="prefix">The schema prefix (<see cref="PgIdentifier.QualifyPrefix"/>).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The fleet, most recently heard from first.</returns>
  internal static async Task<IReadOnlyList<FleetInstanceStatus>> ReadFleetAsync(
      NpgsqlConnection connection, string prefix, CancellationToken cancellationToken) {
    var rows = await _readInstancesAsync(connection, prefix, cancellationToken).ConfigureAwait(false);
    // The stand-by handshake reads the fleet in Preflight, ahead of this release's main pass, which is where the
    // assignment function is created: the fleet must read without it.
    if (!await _assignmentsExistAsync(connection, prefix, cancellationToken).ConfigureAwait(false)) {
      return rows;
    }
    var assignments = (await ReadAssignmentsAsync(connection, prefix, cancellationToken).ConfigureAwait(false))
      .ToDictionary(a => a.InstanceId);
    return [.. rows.Select(row => assignments.TryGetValue(row.InstanceId, out var a)
      ? row with { Assignment = a.Kind, OwnedPartitions = a.OwnedPartitions }
      : row)];
  }

  /// <summary>Every registered instance's assignment, from <c>wh_instance_assignments()</c>.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="prefix">The schema prefix (<see cref="PgIdentifier.QualifyPrefix"/>).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The assignments, by instance id.</returns>
  internal static async Task<IReadOnlyList<InstanceAssignment>> ReadAssignmentsAsync(
      NpgsqlConnection connection, string prefix, CancellationToken cancellationToken) {
    await using var cmd = connection.CreateCommand();
#pragma warning disable S2077 // the schema is escaped through PgIdentifier; nothing else is interpolated
    cmd.CommandText = $"SELECT instance_id, service_name, host_name, library_version, eligible, is_live, roles, "
      + $"owned_partitions, map_revision, assignment FROM {prefix}{ASSIGNMENTS_FUNCTION}";
#pragma warning restore S2077
    var assignments = new List<InstanceAssignment>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      assignments.Add(new InstanceAssignment(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
        reader.GetBoolean(4),
        reader.GetBoolean(5),
        await reader.GetFieldValueAsync<string[]>(6, cancellationToken).ConfigureAwait(false),
        reader.GetInt32(7),
        await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(8),
        KindOf(reader.GetString(9))));
    }
    return assignments;
  }

  /// <summary>The kind <c>wh_instance_assignments()</c> names.</summary>
  /// <param name="name">The SQL name.</param>
  /// <returns>The kind.</returns>
  /// <exception cref="InvalidOperationException">A name this release does not know.</exception>
  internal static InstanceAssignmentKind KindOf(string name) => name switch {
    "starting" => InstanceAssignmentKind.Starting,
    "standard-worker" => InstanceAssignmentKind.StandardWorker,
    "role-holder" => InstanceAssignmentKind.RoleHolder,
    "awaiting-share" => InstanceAssignmentKind.AwaitingShare,
    "draining" => InstanceAssignmentKind.Draining,
    "ineligible" => InstanceAssignmentKind.Ineligible,
    "evicted" => InstanceAssignmentKind.Evicted,
    _ => throw new InvalidOperationException($"wh_instance_assignments() named an assignment this release does not know: '{name}'"),
  };

  private static async Task<bool> _assignmentsExistAsync(NpgsqlConnection connection, string prefix, CancellationToken cancellationToken) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT to_regprocedure(@fn) IS NOT NULL";
    _ = cmd.Parameters.AddWithValue("fn", prefix + ASSIGNMENTS_FUNCTION);
    return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }

  private static async Task<List<FleetInstanceStatus>> _readInstancesAsync(
      NpgsqlConnection connection, string prefix, CancellationToken cancellationToken) {
    await using var cmd = connection.CreateCommand();
#pragma warning disable S2077 // the schema is escaped through PgIdentifier; values are bound parameters
    // Capabilities ride along as a join, not a fan-out: "which instance is the migrator right now" answered from the
    // recorded holdings (derived state: the vote decides, the row reports).
    cmd.CommandText = $@"
      SELECT i.instance_id, i.service_name, i.host_name, i.last_heartbeat_at,
             COALESCE(array_agg(c.capability ORDER BY c.capability) FILTER (WHERE c.capability IS NOT NULL), '{{}}') AS capabilities,
             i.lifecycle_phase, i.library_version,
             (e.instance_id IS NOT NULL) AS evicted,
             -- #2027: the one liveness rule. It is Preflight's (316's bootstrap region), so it exists before the
             -- handshake reads the fleet, ahead of this release's main pass.
             {prefix}wh_instance_is_live(i.instance_id) AS is_live
      FROM {prefix}wh_service_instances i
      LEFT JOIN {prefix}wh_instance_capabilities c ON c.instance_id = i.instance_id
      LEFT JOIN {prefix}wh_instance_evictions e ON e.instance_id = i.instance_id
      GROUP BY i.instance_id, i.service_name, i.host_name, i.last_heartbeat_at, i.lifecycle_phase, i.library_version, e.instance_id
      ORDER BY i.last_heartbeat_at DESC
      LIMIT 200";
#pragma warning restore S2077
    var rows = new List<FleetInstanceStatus>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var heardAt = await reader.GetFieldValueAsync<DateTime>(3, cancellationToken).ConfigureAwait(false);
      rows.Add(new FleetInstanceStatus(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        new DateTimeOffset(DateTime.SpecifyKind(heardAt, DateTimeKind.Utc)),
        await reader.GetFieldValueAsync<string[]>(4, cancellationToken).ConfigureAwait(false),
        await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
        await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
        reader.GetBoolean(7),
        reader.GetBoolean(8)));
    }
    return rows;
  }
}
