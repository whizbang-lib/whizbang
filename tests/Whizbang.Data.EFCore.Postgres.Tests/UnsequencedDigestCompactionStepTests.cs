// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2351: the maintenance step folds the held-without-sequence change log whoever asked for an answer, a bounded number
/// of batches per run, so the log never grows across cycles.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/UnsequencedDigestCompactionStep.cs</code-under-test>
/// <docs>resilience/stream-integrity#the-change-log-stays-bounded</docs>
[Category("Integration")]
[Category("Shard2")]
public class UnsequencedDigestCompactionStepTests : EFCoreTestBase {
  [Test]
  public async Task ARun_FoldsAtMostItsBatchesOfChanges_AndTheNextRunTakesTheRestAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var rows = (UnsequencedDigestCompactionStep.BATCH_SIZE * UnsequencedDigestCompactionStep.MAX_BATCHES_PER_RUN) + 500;
    await _seedHeldAsync(conn, TrackedGuid.New().Value, rows);
    var step = new UnsequencedDigestCompactionStep(typeof(WorkCoordinationDbContext));

    await _runAsync(step);
    var afterFirst = await _pendingAsync(conn);
    await _runAsync(step);

    await Assert.That(step.Name).IsEqualTo("unsequenced-digest-compaction");
    await Assert.That(afterFirst).IsEqualTo(500L).Because("one run folds at most its batches, so the rest waits for the next");
    await Assert.That(await _pendingAsync(conn)).IsEqualTo(0L).Because("the next run folds what was left");
  }

  [Test]
  public async Task ARunOverAnEmptyLog_FoldsNothing_AndRefusesNoServicesAsync() {
    var step = new UnsequencedDigestCompactionStep(typeof(WorkCoordinationDbContext));

    await _runAsync(step);

    await Assert.That(() => step.RunAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
    await Assert.That(() => new UnsequencedDigestCompactionStep(null!)).Throws<ArgumentNullException>();
  }

  // A context that maps none of the framework's entities has no schema to read: the fold runs on the search path's tables.
  [Test]
  public async Task ARun_ThroughAContextThatMapsNoFrameworkEntity_FoldsTheDefaultSchemasLogAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedHeldAsync(conn, TrackedGuid.New().Value, 25);
    var step = new UnsequencedDigestCompactionStep(typeof(BareContext));

    var services = new ServiceCollection();
    services.AddScoped(_ => new BareContext(new DbContextOptionsBuilder<BareContext>().UseNpgsql(ConnectionString).Options));
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    await step.RunAsync(scope.ServiceProvider, CancellationToken.None);

    await Assert.That(await _pendingAsync(conn)).IsEqualTo(0L)
      .Because("with no mapped schema the step folds the log in the default schema rather than skipping it");
  }

  /// <summary>A DbContext that maps none of the framework's entities.</summary>
  public sealed class BareContext(DbContextOptions<BareContext> options) : DbContext(options);

  private async Task _runAsync(UnsequencedDigestCompactionStep step) {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    await step.RunAsync(scope.ServiceProvider, CancellationToken.None);
  }

  private static async Task<long> _pendingAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM wh_unsequenced_digest_changes", conn);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _seedHeldAsync(NpgsqlConnection conn, Guid origin, int rows) {
    await using var cmd = new NpgsqlCommand($"""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope,
                                  commit_sequence, created_at, origin_service_id)
      SELECT gen_random_uuid(), x.s, x.s, 'TestAggregate', 1, 'Contracts.HeldProbe, Contracts', jsonb_build_object('t', 'tenant-a'),
             nextval('wh_commit_seq'), NOW() - INTERVAL '2 hours', '{origin}'::uuid
      FROM (SELECT gen_random_uuid() AS s FROM generate_series(1, {rows})) x
      """, conn);
    await cmd.ExecuteNonQueryAsync();
  }
}
