// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2351 item 3c: the Dapper driver folds the held-without-sequence change log too, so a Dapper service's log stays
/// bounded whoever asks for an integrity answer. The SQL is covered by the EF Core <c>UnsequencedDigestCompactionSqlTests</c>;
/// these lock this driver's step and its registration.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperUnsequencedDigestCompactionStep.cs</code-under-test>
/// <docs>resilience/stream-integrity#the-change-log-stays-bounded</docs>
public class DapperUnsequencedDigestCompactionStepTests : PostgresTestBase {
  [Test]
  public async Task ARun_FoldsAtMostItsBatchesOfChanges_AndTheNextRunTakesTheRestAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedHeldAsync(conn, TrackedGuid.New().Value, 250);
    var step = new DapperUnsequencedDigestCompactionStep(
      ConnectionString, NullLogger<DapperUnsequencedDigestCompactionStep>.Instance, batchSize: 100, maxBatchesPerRun: 2);

    await step.RunAsync(new ServiceCollection().BuildServiceProvider(), CancellationToken.None);
    var afterFirst = await _pendingAsync(conn);
    await step.RunAsync(new ServiceCollection().BuildServiceProvider(), CancellationToken.None);

    await Assert.That(step.Name).IsEqualTo("unsequenced-digest-compaction");
    await Assert.That(afterFirst).IsEqualTo(50L).Because("one run folds at most its batches, so the rest waits for the next");
    await Assert.That(await _pendingAsync(conn)).IsEqualTo(0L).Because("the next run folds what was left");
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT COALESCE(SUM(event_count), 0) FROM wh_unsequenced_digest_totals"))
      .IsEqualTo(250L).Because("every folded change lands in the totals");
  }

  [Test]
  public async Task ARun_WithNoLogger_StillFoldsTheLogAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedHeldAsync(conn, TrackedGuid.New().Value, 30);
    var step = new DapperUnsequencedDigestCompactionStep(ConnectionString);

    await step.RunAsync(new ServiceCollection().BuildServiceProvider(), CancellationToken.None);

    await Assert.That(await _pendingAsync(conn)).IsEqualTo(0L)
      .Because("a step registered without a logger folds and reports what it folded to nothing, never refusing to run");
  }

  [Test]
  public async Task TheStep_RefusesWhatItCannotRunAsync() {
    await Assert.That(() => new DapperUnsequencedDigestCompactionStep(" ")).Throws<ArgumentException>();
    await Assert.That(async () => await new DapperUnsequencedDigestCompactionStep(ConnectionString)
      .RunAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }

  /// <summary>Both registrations of the Dapper driver add the step, so a Dapper service compacts without opting in.</summary>
  [Test]
  public async Task AddWhizbangPostgres_RegistersTheStepAsync() {
    var withSql = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    var withEntries = new ServiceCollection().AddWhizbangPostgres(
      ConnectionString, new JsonSerializerOptions(), false, [], null);

    foreach (var services in new[] { withSql, withEntries }) {
      await using var provider = services.BuildServiceProvider();
      await using var scope = provider.CreateAsyncScope();
      var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<DapperUnsequencedDigestCompactionStep>().ToList();
      await Assert.That(steps.Count).IsEqualTo(1);
    }
  }

  private static async Task<long> _pendingAsync(NpgsqlConnection conn) =>
    await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_unsequenced_digest_changes");

  private static async Task _seedHeldAsync(NpgsqlConnection conn, Guid origin, int rows) {
    await using var cmd = new NpgsqlCommand("""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope,
                                  commit_sequence, created_at, origin_service_id)
      SELECT gen_random_uuid(), x.s, x.s, 'TestAggregate', 1, 'Contracts.HeldProbe, Contracts', jsonb_build_object('t', 'tenant-a'),
             nextval('wh_commit_seq'), NOW() - INTERVAL '2 hours', @heldFrom
      FROM (SELECT gen_random_uuid() AS s FROM generate_series(1, @heldCount)) x
      """, conn);
    cmd.Parameters.AddWithValue("heldFrom", origin);
    cmd.Parameters.AddWithValue("heldCount", rows);
    await cmd.ExecuteNonQueryAsync();
  }
}
