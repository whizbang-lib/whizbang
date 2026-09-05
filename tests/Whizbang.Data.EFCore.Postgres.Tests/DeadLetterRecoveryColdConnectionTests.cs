using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.DeadLetters;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Every entry point on the dead-letter recovery service, called against a context whose
/// connection is closed.
/// </summary>
/// <remarks>
/// <para>Each method opens the connection itself if it finds it shut. That guard existed on
/// sixteen methods and had never run: every other test reaches them through a context EF Core has
/// already opened, so the branch was dead in the suite while being the first thing that executes
/// in production. A scoped DbContext resolved fresh for a maintenance pass arrives closed, and if
/// the guard were wrong on any one of these the failure would be a flat throw on first use of
/// that method — not a subtle one, but invisible until the code path ran in anger.</para>
///
/// <para>Asserting "does not throw" is the whole point here. These are thin wrappers over SQL
/// functions covered elsewhere; what is unproven is that each can establish its own connection.
/// The return values are checked only where a cold call has a defined answer worth naming.</para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreDeadLetterRecoveryService.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard2")]
public class DeadLetterRecoveryColdConnectionTests : EFCoreTestBase {

  private static EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext> _svc(
      WorkCoordinationDbContext ctx) =>
    new(ctx, NullLogger<EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>>.Instance, null);

  /// <summary>Closes the context's connection so the next call has to open it for itself.</summary>
  private static async Task _goColdAsync(WorkCoordinationDbContext ctx) {
    var conn = ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Closed) {
      await conn.CloseAsync();
    }
  }

  private async Task<Guid> _seedDeadLetterAsync(CancellationToken ct) {
    var id = (Guid)TrackedGuid.NewMedo();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_dead_letters
        (dead_letter_id, source_table, source_id, message_type, envelope, failure_reason,
         attempts_when_dlq, dead_lettered_at, recovery_status, generation, error_fingerprint,
         error_fingerprint_version)
      VALUES (@id, 'wh_inbox', @src, 'Test.Event', '{""p"":1}'::jsonb, 5, 3,
              NOW() - INTERVAL '1 hour', 0, 'cold/1', 'fp-cold', 1)";
    ins.Parameters.AddWithValue("id", id);
    ins.Parameters.AddWithValue("src", (Guid)TrackedGuid.NewMedo());
    await ins.ExecuteNonQueryAsync(ct);
    return id;
  }

  [Test]
  [Timeout(120000)]
  public async Task EveryReadPath_OpensItsOwnConnectionWhenTheContextIsColdAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var svc = _svc(ctx);

    await _goColdAsync(ctx);
    var due = await svc.FetchDueAsync(10, cancellationToken);
    await Assert.That(due).IsNotNull();

    await _goColdAsync(ctx);
    var cohorts = await svc.ListHeldCohortsAsync(cancellationToken);
    await Assert.That(cohorts).IsNotNull();

    await _goColdAsync(ctx);
    var unstacked = await svc.FetchUnstackedAsync(10, cancellationToken);
    await Assert.That(unstacked).IsNotNull();

    await _goColdAsync(ctx);
    var verdict = await svc.EvaluateCampaignAsync("fp-none", "gen-none", cancellationToken);
    await Assert.That(verdict.Kind).IsEqualTo(CanaryVerdictKind.Pending)
      .Because("a campaign nobody has started has no probes to judge, and Pending is what tells "
             + "the caller to look again rather than to release or hold a cohort on no evidence");
  }

  [Test]
  [Timeout(120000)]
  public async Task EveryWritePath_OpensItsOwnConnectionWhenTheContextIsColdAsync(
      CancellationToken cancellationToken) {
    var deadLetterId = await _seedDeadLetterAsync(cancellationToken);
    await using var ctx = CreateDbContext();
    var svc = _svc(ctx);

    await _goColdAsync(ctx);
    await Assert.That(await svc.PurgeUndeliverableHeldAsync(cancellationToken))
      .IsGreaterThanOrEqualTo(0);

    await _goColdAsync(ctx);
    await Assert.That(await svc.BeginCanaryProbesAsync("fp-cold", "cold/1", 1, 1, cancellationToken))
      .IsGreaterThanOrEqualTo(0);

    await _goColdAsync(ctx);
    await Assert.That(
      await svc.ReleaseHeldCohortAsync("fp-cold", TimeSpan.FromMinutes(1), cancellationToken))
      .IsGreaterThanOrEqualTo(0);

    await _goColdAsync(ctx);
    await Assert.That(await svc.PruneStackHistoryAsync(30, cancellationToken))
      .IsGreaterThanOrEqualTo(0);

    await _goColdAsync(ctx);
    await Assert.That(
      await svc.BeginTrickleWaveAsync("fp-cold", "cold/1", 1, cancellationToken))
      .IsGreaterThanOrEqualTo(0);

    await _goColdAsync(ctx);
    await Assert.That(
      await svc.CountWaveRequarantinesAsync("fp-cold", "cold/1", cancellationToken))
      .IsGreaterThanOrEqualTo(0);

    await _goColdAsync(ctx);
    await Assert.That(await svc.ResetForGenerationAsync("cold/2", 5, cancellationToken))
      .IsGreaterThanOrEqualTo(0);

    var stack = new StackIdentity(["Frame.One", "Frame.Two"], "hash-cold", IsProse: false);

    await _goColdAsync(ctx);
    await svc.RecordStackAsync(deadLetterId, stack, cancellationToken);

    await _goColdAsync(ctx);
    await Assert.That(
      await svc.RecordStacksAsync([(deadLetterId, stack)], cancellationToken))
      .IsGreaterThanOrEqualTo(0);

    await _goColdAsync(ctx);
    await svc.ScheduleNextAttemptAsync(
      deadLetterId, DateTimeOffset.UtcNow.AddMinutes(5), cancellationToken);

    // Both terminal transitions route through one shared helper, so either proves its guard.
    await _goColdAsync(ctx);
    await svc.MarkHoldingAsync(deadLetterId, cancellationToken);

    // RecoverAsync is deliberately absent: re-driving a dead letter writes it back to wh_inbox,
    // which needs a fully formed envelope rather than the marker payload seeded here, and its own
    // cold path is already exercised by the recovery tests that seed one. The point of this test
    // is the guard, and covering it does not require re-driving anything.
    await Assert.That(ctx.Database.GetDbConnection().State)
      .IsEqualTo(System.Data.ConnectionState.Open)
      .Because("the last call arrived on a closed connection and had to open one to reach the "
             + "database at all; a connection still closed here would mean the work never landed");
  }

  [Test]
  [Timeout(60000)]
  public async Task RecordStacks_WithNothingToRecord_NeverTouchesTheDatabaseAsync(
      CancellationToken cancellationToken) {
    // The backfill calls this once per scan whether or not the scan found anything. Opening a
    // connection and building a JSON payload to tell the database about zero rows is a round-trip
    // per idle cycle, on a maintenance path that exists to be cheap.
    await using var ctx = CreateDbContext();
    var svc = _svc(ctx);
    await _goColdAsync(ctx);

    var recorded = await svc.RecordStacksAsync([], cancellationToken);

    await Assert.That(recorded).IsEqualTo(0);
    await Assert.That(ctx.Database.GetDbConnection().State)
      .IsEqualTo(System.Data.ConnectionState.Closed)
      .Because("an empty batch short-circuits before the connection guard, so the cold context is "
             + "still cold — that is the observable difference between a no-op and a round-trip");
  }
}
