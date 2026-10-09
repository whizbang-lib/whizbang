// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.Postgres.Schema;

/// <summary>
/// What the other running instances of a schema still declare, so a drop waits until none of them does.
/// </summary>
/// <remarks>
/// <para>
/// Under a rolling update the instances on the previous release keep running, and keep querying, until the new
/// ones are ready. Dropping an index the previous release still declares would take it out from under them. So
/// each instance records, at its reconcile, every object it declares, and an object is dropped only once no
/// live instance (one that has heartbeated within the window) declares it.
/// </para>
/// <para>
/// An instance that is alive and has recorded nothing is on a release that predates the ledger, and what it
/// relies on is unknown, so nothing is dropped while it runs. The records of instances that are no longer
/// alive are removed.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/managed-schema-objects#settings</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaReconcilerTests.cs</tests>
internal static class ManagedSchemaFleet {
  /// <summary>How recent a heartbeat has to be for an instance to count as running.</summary>
  internal static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(2);

  /// <summary>
  /// Records what this instance declares (when it is known), and returns the objects, as <c>table:name</c>,
  /// other live instances declare; null when a live instance has recorded nothing, so the fleet is unknown.
  /// </summary>
  internal static async Task<IReadOnlySet<string>?> ReportAndReadAsync(
      NpgsqlConnection connection, string schema, Guid? self, ManagedSchemaObjectSet declared,
      TimeSpan liveWindow, CancellationToken cancellationToken) {
    var quoted = PgIdentifier.Quote(schema);
    var declarations = $"{quoted}.wh_managed_object_declarations";
    var instances = $"{quoted}.wh_service_instances";

    if (self is { } instanceId) {
      await using var report = new NpgsqlCommand(
        $"INSERT INTO {declarations} (instance_id, objects, reported_at) VALUES (@id, @objects, now()) "
        + "ON CONFLICT (instance_id) DO UPDATE SET objects = EXCLUDED.objects, reported_at = EXCLUDED.reported_at",
        connection);
      report.Parameters.AddWithValue("id", instanceId);
      report.Parameters.AddWithValue("objects", declared.Objects.Select(o => $"{o.Table}:{o.Name}").Distinct().ToArray());
      await report.ExecuteNonQueryAsync(cancellationToken);
    }

    if (await _scalarAsync(connection, "SELECT to_regclass(@t) IS NOT NULL", ("t", instances), cancellationToken) is not true) {
      return new HashSet<string>(StringComparer.Ordinal);
    }

    await using (var forget = new NpgsqlCommand(
      $"DELETE FROM {declarations} d WHERE NOT EXISTS (SELECT 1 FROM {instances} i "
      + "WHERE i.instance_id = d.instance_id AND i.last_heartbeat_at > now() - @window)", connection)) {
      forget.Parameters.AddWithValue("window", liveWindow);
      await forget.ExecuteNonQueryAsync(cancellationToken);
    }

    await using var read = new NpgsqlCommand(
      $"SELECT d.objects FROM {instances} i LEFT JOIN {declarations} d ON d.instance_id = i.instance_id "
      + "WHERE i.last_heartbeat_at > now() - @window AND i.instance_id IS DISTINCT FROM @self", connection);
    read.Parameters.AddWithValue("window", liveWindow);
    read.Parameters.Add(new NpgsqlParameter("self", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)self ?? DBNull.Value });

    var fleet = new HashSet<string>(StringComparer.Ordinal);
    await using var reader = await read.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      if (reader.IsDBNull(0)) {
        return null;
      }
      fleet.UnionWith(reader.GetFieldValue<string[]>(0));
    }
    return fleet;
  }

  private static async Task<object?> _scalarAsync(
      NpgsqlConnection connection, string sql, (string Name, object Value) parameter, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.AddWithValue(parameter.Name, parameter.Value);
    return await command.ExecuteScalarAsync(cancellationToken);
  }
}
