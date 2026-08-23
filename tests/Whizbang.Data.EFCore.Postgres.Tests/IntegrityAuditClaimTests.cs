using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// One integrity audit per SERVICE per cycle: <c>TryClaimIntegrityAuditCycleAsync</c> is an atomic
/// settings CAS (the deep-prune watermark pattern) — racing replicas resolve at the row lock,
/// exactly one wins the cycle, a sibling's fresh claim suppresses the rest, and an aged watermark
/// re-opens the claim.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[NotInParallel("IntegrityAuditClaim")]
[Category("Shard1")]
public class IntegrityAuditClaimTests : EFCoreTestBase {

  private const string CLAIM_KEY = "integrity_audit_last_run";

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task FirstClaimWins_SiblingWithinWindowLosesAsync() {
    await using var ctx = CreateDbContext();
    await _resetAsync(ctx);
    var coordinator = _coordinator(ctx);

    await Assert.That(await coordinator.TryClaimIntegrityAuditCycleAsync(TimeSpan.FromMinutes(30))).IsTrue()
      .Because("the first instance to reach the watermark runs the cycle.");
    await Assert.That(await coordinator.TryClaimIntegrityAuditCycleAsync(TimeSpan.FromMinutes(30))).IsFalse()
      .Because("a sibling arriving within the claim window skips — the audit is per-service work, " +
               "and every replica re-running the full-store digest recompute multiplies fleet load.");
  }

  [Test]
  public async Task AgedClaim_ReopensAsync() {
    await using var ctx = CreateDbContext();
    await _resetAsync(ctx);
    var coordinator = _coordinator(ctx);
    await Assert.That(await coordinator.TryClaimIntegrityAuditCycleAsync(TimeSpan.FromMinutes(30))).IsTrue();

    // Age the watermark past the window (no wall-clock sleeps — the DB row is the clock).
    var conn = await _openAsync(ctx);
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "UPDATE wh_settings SET setting_value = (NOW() - INTERVAL '31 minutes')::text WHERE setting_key = @k";
      cmd.Parameters.AddWithValue("k", CLAIM_KEY);
      await cmd.ExecuteNonQueryAsync();
    }

    await Assert.That(await coordinator.TryClaimIntegrityAuditCycleAsync(TimeSpan.FromMinutes(30))).IsTrue()
      .Because("an aged watermark means no sibling ran recently — the next cycle claims normally.");
  }

  [Test]
  public async Task ConcurrentClaims_ExactlyOneWinsAsync() {
    await using var ctxA = CreateDbContext();
    await using var ctxB = CreateDbContext();
    await _resetAsync(ctxA);

    var results = await Task.WhenAll(
      _coordinator(ctxA).TryClaimIntegrityAuditCycleAsync(TimeSpan.FromMinutes(30)),
      _coordinator(ctxB).TryClaimIntegrityAuditCycleAsync(TimeSpan.FromMinutes(30)));

    await Assert.That(results.Count(r => r)).IsEqualTo(1)
      .Because("racing replicas resolve at the settings row lock — exactly one runs the cycle.");
  }

  private static async Task _resetAsync(WorkCoordinationDbContext ctx) {
    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM wh_settings WHERE setting_key = @k";
    cmd.Parameters.AddWithValue("k", CLAIM_KEY);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }
}
