// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 616 (#2595, phase 3 of #1328): each instance's assignment is one readable fact, derived from the tables that
/// decide it and stored nowhere else. <c>wh_instance_assignments()</c> gives one row per registered instance with its
/// roles, its share of the partitions and the kind of assignment it has: starting, standard-worker, role-holder,
/// awaiting-share, draining, ineligible or evicted.
/// </summary>
/// <remarks>Database time is never advanced here: every kind follows from rows seeded as the framework writes them.</remarks>
/// <docs>operations/startup/startup-status#instance-assignments</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/616_EachInstanceAssignmentIsOneFact.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class InstanceAssignmentsSqlTests : EFCoreTestBase {
  private const string SUPPORTS_MAP = """{"partition_ownership_level": 1}""";

  [Test]
  [Timeout(60000)]
  public async Task Assignments_InMapMode_NameEveryKind_FromTheTablesThatDecideItAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await _execAsync(conn, "INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('partition_ownership_mode', 'map', 'string')", cancellationToken);
    var starting = await _registerAsync(conn, 40, "Migrating", SUPPORTS_MAP, cancellationToken);
    var worker = await _registerAsync(conn, 70, "Running", SUPPORTS_MAP, cancellationToken);
    var holder = await _registerAsync(conn, 70, "Running", SUPPORTS_MAP, cancellationToken);
    var awaiting = await _registerAsync(conn, 60, "AcceptingCommands", SUPPORTS_MAP, cancellationToken);
    var draining = await _registerAsync(conn, 70, "Stopping", SUPPORTS_MAP, cancellationToken);
    var older = await _registerAsync(conn, 70, "Running", "{}", cancellationToken);
    var evicted = await _registerAsync(conn, 70, "Running", SUPPORTS_MAP, cancellationToken);
    _ = await _execAsync(conn, "INSERT INTO wh_instance_evictions (instance_id, reason) VALUES (@id, 'test')", cancellationToken, ("id", evicted));
    await _ownAsync(conn, worker, 0, 6000, cancellationToken);
    await _ownAsync(conn, holder, 6000, 10000, cancellationToken);
    var epoch = await _grantAsync(conn, holder, PartitionAssignerOptions.ROLE, cancellationToken);

    var rows = await _assignmentsAsync(conn, cancellationToken);

    await Assert.That(rows[starting].Kind).IsEqualTo("starting");
    await Assert.That(rows[worker].Kind).IsEqualTo("standard-worker");
    await Assert.That(rows[worker].Owned).IsEqualTo(6000);
    await Assert.That(rows[worker].Roles).IsEmpty();
    await Assert.That(rows[holder].Kind).IsEqualTo("role-holder");
    await Assert.That(rows[holder].Owned).IsEqualTo(4000).Because("a role holder is a standard worker as well");
    await Assert.That(rows[holder].Roles).IsEquivalentTo([PartitionAssignerOptions.ROLE]);
    await Assert.That(rows[holder].Epochs).IsEquivalentTo([epoch]);
    await Assert.That(rows[awaiting].Kind).IsEqualTo("awaiting-share")
      .Because("eligible for work and owning no partition is the symptom worth seeing; no Running gate waits for it");
    await Assert.That(rows[awaiting].Eligible).IsTrue();
    await Assert.That(rows[draining].Kind).IsEqualTo("draining");
    await Assert.That(rows[older].Kind).IsEqualTo("ineligible")
      .Because("once the service claims by the map, an instance that does not support it owns nothing");
    await Assert.That(rows[evicted].Kind).IsEqualTo("evicted");
    await Assert.That(rows[evicted].Evicted).IsTrue();
    await Assert.That(rows[worker].IsLive).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task Assignments_BeforeTheMapSwitch_CountTheShareFromThePublishedMapAsync(CancellationToken cancellationToken) {
    // Off the switch, a claim ranks by the published map's members: the explicit map when one was published, the
    // modulo of the member list otherwise.
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, 70, "Running", "{}", cancellationToken);
    var b = await _registerAsync(conn, 70, "Running", "{}", cancellationToken);
    var c = await _registerAsync(conn, 70, "Running", "{}", cancellationToken);
    await _publishAsync(conn, [a, b, c], owners: null, revision: 7, cancellationToken);

    var modulo = await _assignmentsAsync(conn, cancellationToken);
    await Assert.That(modulo[a].Owned).IsEqualTo(3334);
    await Assert.That(modulo[b].Owned).IsEqualTo(3333);
    await Assert.That(modulo[c].Owned).IsEqualTo(3333);
    await Assert.That(modulo[a].MapRevision).IsEqualTo(7L);
    await Assert.That(modulo[a].Kind).IsEqualTo("standard-worker");

    // An explicit map gives every partition to the first member.
    await _publishAsync(conn, [a, b, c], owners: [.. Enumerable.Repeat((short)0, 10000)], revision: 8, cancellationToken);
    var mapped = await _assignmentsAsync(conn, cancellationToken);
    await Assert.That(mapped[a].Owned).IsEqualTo(10000);
    await Assert.That(mapped[b].Kind).IsEqualTo("awaiting-share");
    await Assert.That(mapped[b].MapRevision).IsEqualTo(8L);
  }

  [Test]
  [Timeout(60000)]
  public async Task Assignments_WithNoMapPublished_AreAwaitingTheirShare_AndALapsedRoleIsNotHeldAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, 70, "Running", "{}", cancellationToken);
    var b = await _registerAsync(conn, 70, "Running", "{}", cancellationToken);
    _ = await _grantAsync(conn, a, PartitionAssignerOptions.ROLE, cancellationToken);
    // A live peer is a candidate, so a's lapsed lease is a verdict (#2593).
    _ = await _execAsync(conn, """
      INSERT INTO wh_role_candidates (role, instance_id, version_key, last_voted_at) VALUES (@role, @id, '{}', now());
      UPDATE wh_role_assignments SET lease_expires_at = now() - interval '1 second', renewed_at = now() - interval '1 minute'
       WHERE role = @role
      """, cancellationToken, ("role", PartitionAssignerOptions.ROLE), ("id", b));

    var rows = await _assignmentsAsync(conn, cancellationToken);

    await Assert.That(rows[a].Kind).IsEqualTo("awaiting-share").Because("no map is published, so nobody owns a partition yet");
    await Assert.That(rows[a].Roles).IsEmpty().Because("a role whose assignment is no longer valid is not held");
    await Assert.That(rows[a].MapRevision).IsNull();
  }

  [Test]
  [Timeout(60000)]
  public async Task Assignments_FollowTheDecidingTables_WithNoWriteOfTheirOwnAsync(CancellationToken cancellationToken) {
    // D3: moving a partition or a role changes the row; nothing else is written.
    await using var conn = await _openAsync(cancellationToken);
    await _execAsync(conn, "INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('partition_ownership_mode', 'map', 'string')", cancellationToken);
    var a = await _registerAsync(conn, 70, "Running", SUPPORTS_MAP, cancellationToken);
    var b = await _registerAsync(conn, 70, "Running", SUPPORTS_MAP, cancellationToken);
    await _ownAsync(conn, a, 0, 10000, cancellationToken);
    await Assert.That((await _assignmentsAsync(conn, cancellationToken))[b].Kind).IsEqualTo("awaiting-share");

    _ = await _execAsync(conn, "UPDATE wh_partition_ownership SET owner_instance_id = @b, epoch = epoch + 1 WHERE partition_number < 5000",
      cancellationToken, ("b", b));
    _ = await _grantAsync(conn, b, PartitionAssignerOptions.ROLE, cancellationToken);
    var after = await _assignmentsAsync(conn, cancellationToken);

    await Assert.That(after[a].Owned).IsEqualTo(5000);
    await Assert.That(after[b].Owned).IsEqualTo(5000);
    await Assert.That(after[b].Kind).IsEqualTo("role-holder");
    await Assert.That(after.Count).IsEqualTo(2);
  }

  // --- helpers ----------------------------------------------------------------------------------

  private sealed record Row(string Kind, bool Eligible, bool IsLive, bool Evicted, string[] Roles, long[] Epochs, int Owned, long? MapRevision);

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private static async Task<int> _execAsync(NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return await cmd.ExecuteNonQueryAsync(ct);
  }

  /// <summary>A live, pooled registration at a startup stage and lifecycle phase, with registry metadata.</summary>
  private static async Task<Guid> _registerAsync(NpgsqlConnection conn, short stage, string phase, string metadata, CancellationToken ct) {
    var id = (Guid)TrackedGuid.New();
    _ = await _execAsync(conn, """
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode,
         liveness_window_seconds, startup_stage, lifecycle_phase, metadata, library_version)
      VALUES (@id, 'assign-svc', 'assign-host', 1, NOW(), NOW() - INTERVAL '1 hour', 'pooled', 30, @stage, @phase,
              @metadata::jsonb, '1.2.3')
      """, ct, ("id", id), ("stage", stage), ("phase", phase), ("metadata", metadata));
    return id;
  }

  private static Task<int> _ownAsync(NpgsqlConnection conn, Guid owner, int from, int to, CancellationToken ct) =>
    _execAsync(conn, """
      INSERT INTO wh_partition_ownership (partition_number, owner_instance_id, epoch)
      SELECT g, @owner, 1 FROM generate_series(@from, @to - 1) g
      ON CONFLICT (partition_number) DO UPDATE SET owner_instance_id = EXCLUDED.owner_instance_id
      """, ct, ("owner", owner), ("from", from), ("to", to));

  private static Task<int> _publishAsync(NpgsqlConnection conn, Guid[] members, short[]? owners, long revision, CancellationToken ct) {
    var sql = """
      INSERT INTO wh_published_partition_assignment
        (singleton, role, epoch, revision, assigner_instance_id, members, published_at, lease, lease_expires_at, partition_owners)
      VALUES (TRUE, 'partition-assigner', 1, @revision, @assigner, @members, now(), interval '1 minute', now() + interval '1 minute', @owners)
      ON CONFLICT (singleton) DO UPDATE SET revision = EXCLUDED.revision, members = EXCLUDED.members,
                                            partition_owners = EXCLUDED.partition_owners
      """;
    return owners is null
      ? _execAsync(conn, sql.Replace("@owners", "NULL", StringComparison.Ordinal), ct,
        ("revision", revision), ("assigner", members[0]), ("members", members))
      : _execAsync(conn, sql, ct, ("revision", revision), ("assigner", members[0]), ("members", members), ("owners", owners));
  }

  private static async Task<long> _grantAsync(NpgsqlConnection conn, Guid id, string role, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT outcome, epoch FROM wh_vote_role(@role, @id, interval '15 seconds', interval '15 seconds', NULL, NULL, NULL)";
    cmd.Parameters.AddWithValue("role", role);
    cmd.Parameters.AddWithValue("id", id);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    _ = await reader.ReadAsync(ct);
    if (reader.GetString(0) != "granted") {
      throw new InvalidOperationException($"expected a grant for {id}, got {reader.GetString(0)}");
    }
    return reader.GetInt64(1);
  }

  private static async Task<Dictionary<Guid, Row>> _assignmentsAsync(NpgsqlConnection conn, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT instance_id, assignment, eligible, is_live, evicted, roles, role_epochs, owned_partitions, map_revision
        FROM wh_instance_assignments()
      """;
    var rows = new Dictionary<Guid, Row>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      rows[reader.GetGuid(0)] = new Row(reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4),
        reader.GetFieldValue<string[]>(5), reader.GetFieldValue<long[]>(6), reader.GetInt32(7),
        await reader.IsDBNullAsync(8, ct) ? null : reader.GetInt64(8));
    }
    return rows;
  }
}
