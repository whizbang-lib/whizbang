// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What judging a role holder by the one liveness rule costs the coordination statements (#2140): the vote's fast path,
/// the vote that finds the holder past its lease, the renewal, the fence and the grant watch's read, each measured with
/// 355's definitions and again with 184's (the lease alone), in a fleet of registered instances.
/// </summary>
/// <remarks>
/// The buffers each statement touches are the same on every machine and carry the gate: past its lease the rule reads the
/// holder's instance row and the alive-locks, and nothing scales with the fleet beyond that one lookup. Timings are
/// recorded, never gated.
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
[Category("Benchmark")]
[NotInParallel] // A measurement: runs alone so it measures the code, not its neighbors.
[Category("Performance")]
public class RoleHolderLivenessCostScenarioTests : EFCoreTestBase {
  private const string ROLE = "maintainer";
  private const int FLEET = 500;
  private const int RUNS = 200;

  /// <summary>184's _role_lapse_reason and fence, word for word: the lease alone.</summary>
  private const string LEASE_ALONE_SQL = """
    CREATE OR REPLACE FUNCTION _role_lapse_reason(p_row wh_role_assignments) RETURNS TEXT LANGUAGE sql STABLE AS $$
      SELECT CASE
        WHEN EXISTS (SELECT 1 FROM wh_instance_evictions e WHERE e.instance_id = p_row.holder_instance_id) THEN 'evicted'
        WHEN NOT EXISTS (SELECT 1 FROM wh_service_instances s WHERE s.instance_id = p_row.holder_instance_id) THEN 'unregistered'
        WHEN (p_row.lease_expires_at IS NULL OR p_row.lease_expires_at <= now())
             AND NOT _role_backend_active(p_row.duty_backend_pid, p_row.duty_backend_started_at) THEN 'lapsed'
        ELSE NULL
      END
    $$;
    """;

  [Test]
  [Timeout(600000)]
  public async Task RoleStatements_JudgedByTheRule_VersusTheLeaseAlone_ReportAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _execAsync(conn, $"""
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode, liveness_window_seconds)
      SELECT gen_random_uuid(), 'fleet', 'host', g, now(), now() - interval '1 hour',
             CASE WHEN g % 2 = 0 THEN 'direct' ELSE 'pooled' END, 30
        FROM generate_series(1, {FLEET}) g;
      ANALYZE wh_service_instances;
      """);
    var holder = await _registerAsync(conn, cancellationToken);
    var voter = await _registerAsync(conn, cancellationToken);
    var epoch = (long)(await _scalarAsync(conn, $"SELECT epoch FROM wh_vote_role('{ROLE}', '{holder}', interval '15 seconds', interval '15 seconds', NULL, NULL, NULL)"))!;
    await _execAsync(conn, $"SELECT wh_declare_role_progress_window('{ROLE}', '{holder}', {epoch}, interval '2 minutes')");

    var statements = new (string Name, string Sql)[] {
      ("vote by another instance", $"SELECT * FROM wh_vote_role('{ROLE}', '{voter}', interval '15 seconds', interval '15 seconds', NULL, NULL, NULL)"),
      ("renewal by the holder", $"SELECT wh_renew_role_lease('{ROLE}', '{holder}', {epoch})"),
      ("fence", $"SELECT wh_assert_role_epoch('{ROLE}', '{holder}', {epoch})"),
      ("status", "SELECT * FROM wh_role_assignment_status()"),
    };
    var report = new StringBuilder("Role statements: 355 (the one liveness rule) against 184 (the lease alone)\n");
    var buffers = new Dictionary<string, long>(StringComparer.Ordinal);
    foreach (var lease in new[] { "inside the lease", "past the lease, kept live" }) {
      foreach (var (name, sql) in statements) {
        if (lease != "inside the lease") {
          // Past the lease but inside the window, with a fresh heartbeat: what a busy holder looks like.
          await _execAsync(conn, $"UPDATE wh_role_assignments SET lease_expires_at = now() - interval '1 second', renewed_at = now() - interval '30 seconds' WHERE role = '{ROLE}'");
        }
        var (ms, hits) = await _measureAsync(conn, sql, cancellationToken);
        buffers[$"355 {lease} {name}"] = hits;
        report.Append(CultureInfo.InvariantCulture, $"  355  {lease,-26} {name,-26} {ms,8:F3} ms  {hits,6} buffers\n");
      }
    }
    var (watchMs, watchBuffers) = await _measureAsync(conn, $"SELECT wh_role_is_held('{ROLE}', '{holder}', {epoch})", cancellationToken);
    report.Append(CultureInfo.InvariantCulture, $"  355  {"past the lease, kept live",-26} {"grant watch read",-26} {watchMs,8:F3} ms  {watchBuffers,6} buffers\n");

    await _execAsync(conn, LEASE_ALONE_SQL);
    await _execAsync(conn, $"UPDATE wh_role_assignments SET lease_expires_at = now() + interval '15 seconds', renewed_at = now() WHERE role = '{ROLE}'");
    foreach (var (name, sql) in statements.Where(s => s.Name != "fence")) {
      var (ms, hits) = await _measureAsync(conn, sql, cancellationToken);
      report.Append(CultureInfo.InvariantCulture, $"  184  {"inside the lease",-26} {name,-26} {ms,8:F3} ms  {hits,6} buffers\n");
    }
    Console.WriteLine(report.ToString());

    await Assert.That(buffers["355 past the lease, kept live vote by another instance"]).IsLessThan(200L)
      .Because("past the lease the rule reads one instance row and the alive-locks; nothing scales with the fleet");
  }

  private static async Task<(double Ms, long Buffers)> _measureAsync(NpgsqlConnection conn, string sql, CancellationToken ct) {
    // Warm, then the mean of RUNS executions inside one transaction that is rolled back, so the row never moves.
    await using (var tx = await conn.BeginTransactionAsync(ct)) {
      for (var i = 0; i < 10; i++) {
        await _execAsync(conn, sql);
      }
      await tx.RollbackAsync(ct);
    }
    var total = 0.0;
    long buffers = 0;
    for (var i = 0; i < RUNS; i++) {
      await using var tx = await conn.BeginTransactionAsync(ct);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + sql;
      var plan = new List<string>();
      await using (var reader = await cmd.ExecuteReaderAsync(ct)) {
        while (await reader.ReadAsync(ct)) {
          plan.Add(reader.GetString(0));
        }
      }
      await tx.RollbackAsync(ct);
      total += _number(plan, "Execution Time: ");
      buffers = Math.Max(buffers, (long)_number(plan, "Buffers: shared hit="));
    }
    return (total / RUNS, buffers);
  }

  private static double _number(List<string> plan, string label) {
    var line = plan.FirstOrDefault(l => l.Contains(label, StringComparison.Ordinal));
    if (line is null) {
      return 0;
    }
    var text = line[(line.IndexOf(label, StringComparison.Ordinal) + label.Length)..];
    var end = 0;
    while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) {
      end++;
    }
    return double.Parse(text[..end], CultureInfo.InvariantCulture);
  }

  private static async Task<Guid> _registerAsync(NpgsqlConnection conn, CancellationToken ct) {
    var id = (Guid)TrackedGuid.New();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode, liveness_window_seconds)
      VALUES (@id, 'role-svc', 'role-host', 1, now(), now(), 'pooled', 30)
      """;
    cmd.Parameters.AddWithValue("id", id);
    await cmd.ExecuteNonQueryAsync(ct);
    return id;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<object?> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return await cmd.ExecuteScalarAsync();
  }
}
