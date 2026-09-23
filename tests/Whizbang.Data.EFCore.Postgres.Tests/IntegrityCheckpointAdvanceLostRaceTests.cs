using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What the instance that LOSES the integrity-checkpoint advance does with the cycle.
/// </summary>
/// <remarks>
/// <para>
/// The advance is a compare-and-set on a single watermark row: every instance reads the same prior
/// value and each tries to move it, so exactly one conditional UPDATE matches a row and the rest
/// match none. The losers must skip the cycle entirely. Returning an empty-but-non-null window
/// instead would let every instance in the fleet publish a checkpoint for the same range on the
/// same tick — the liveness beat would read as N beats, and each of those checkpoints re-registers
/// the same window with every consumer, turning one deficit into a fleet-sized fan-out of identical
/// repair requests.
/// </para>
/// <para>
/// The loss cannot be produced by racing two coordinators: whether they interleave inside the
/// window between the read and the CAS is thread-pool timing, and if they serialize both of them
/// win. Instead the observable the loser sees — its conditional UPDATE matched zero rows — is
/// produced directly, by a BEFORE UPDATE trigger standing in for the instance that already took the
/// watermark. From the coordinator's side the two are the same event: <c>ExecuteNonQuery</c>
/// returns 0.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[NotInParallel("IntegrityCheckpoint")]
[Category("Shard3")]
public class IntegrityCheckpointAdvanceLostRaceTests : EFCoreTestBase {

  private const string WATERMARK_KEY = "integrity_checkpoint_watermark";

  [Test]
  public async Task Advance_WhenTheWatermarkCompareAndSetMatchesNoRow_SkipsTheCycleAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var coordinator = (IWorkCoordinator)new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    // First run baselines and INSERTs the watermark row, so the next run takes the compare-and-set
    // path rather than the baseline path.
    var baseline = await coordinator.AdvanceIntegrityCheckpointAsync();
    await Assert.That(baseline).IsNotNull()
      .Because("the baseline must land first, or the next call would be exercising the insert path "
             + "and this test would prove nothing about the compare-and-set");

    var watermarkBefore = await _readWatermarkAsync(conn);
    await Assert.That(watermarkBefore).IsNotNull()
      .Because("the compare-and-set has nothing to lose to unless the baseline row exists");

    await _suppressWatermarkUpdatesAsync(conn);

    var lost = await coordinator.AdvanceIntegrityCheckpointAsync();

    await Assert.That(lost).IsNull()
      .Because("an instance whose compare-and-set matched no row did not take this window — "
             + "returning an empty window instead would have every loser in the fleet publish a "
             + "checkpoint for a range another instance owns");
    await Assert.That(await _readWatermarkAsync(conn)).IsEqualTo(watermarkBefore)
      .Because("the watermark must be exactly where the winner left it — a loser that advanced it "
             + "anyway would skip the window for everyone");
  }

  /// <summary>
  /// Stands in for the instance that already advanced the watermark: the conditional UPDATE from
  /// any later instance matches no row.
  /// </summary>
  private static async Task _suppressWatermarkUpdatesAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      CREATE FUNCTION wh_test_watermark_already_taken() RETURNS TRIGGER AS $$
      BEGIN
        IF OLD.setting_key = '{WATERMARK_KEY}' THEN
          RETURN NULL;
        END IF;
        RETURN NEW;
      END; $$ LANGUAGE plpgsql;

      CREATE TRIGGER wh_test_watermark_already_taken
        BEFORE UPDATE ON wh_settings
        FOR EACH ROW EXECUTE FUNCTION wh_test_watermark_already_taken();
      """;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<string?> _readWatermarkAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT setting_value FROM wh_settings WHERE setting_key = '{WATERMARK_KEY}'";
    return await cmd.ExecuteScalarAsync() as string;
  }

  /// <summary>
  /// Refuses the baseline INSERT outright. Standing in for the settings write failing — a revoked
  /// grant, a full disk, a constraint added by an operator — because the coordinator sees only that
  /// the statement threw, and nothing about which of those it was changes what it must do.
  /// </summary>
  private static async Task _rejectWatermarkInsertsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      CREATE FUNCTION wh_test_reject_watermark_insert() RETURNS TRIGGER AS $$
      BEGIN
        IF NEW.setting_key = '{WATERMARK_KEY}' THEN
          RAISE EXCEPTION 'watermark baseline refused by test';
        END IF;
        RETURN NEW;
      END; $$ LANGUAGE plpgsql;

      CREATE TRIGGER wh_test_reject_watermark_insert
        BEFORE INSERT ON wh_settings
        FOR EACH ROW EXECUTE FUNCTION wh_test_reject_watermark_insert();
      """;
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Suppresses the baseline INSERT the way a row already being there suppresses it: the statement
  /// succeeds and affects nothing. <c>ON CONFLICT DO NOTHING</c> reports the same zero when another
  /// instance inserted the watermark between this instance's read and its write, and that race
  /// cannot be produced by running two coordinators — whether they interleave inside that window is
  /// thread-pool timing.
  /// </summary>
  private static async Task _suppressWatermarkInsertsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      CREATE FUNCTION wh_test_watermark_baseline_taken() RETURNS TRIGGER AS $$
      BEGIN
        IF NEW.setting_key = '{WATERMARK_KEY}' THEN
          RETURN NULL;
        END IF;
        RETURN NEW;
      END; $$ LANGUAGE plpgsql;

      CREATE TRIGGER wh_test_watermark_baseline_taken
        BEFORE INSERT ON wh_settings
        FOR EACH ROW EXECUTE FUNCTION wh_test_watermark_baseline_taken();
      """;
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// The very first cycle is a race too: every instance in a fresh fleet reads no watermark and
  /// each tries to insert one. The one whose insert affects no row lost, and must skip the cycle.
  /// Handing back a window anyway would have the whole fleet publish a checkpoint for the same
  /// baseline range on the same tick — the identical fan-out the compare-and-set path exists to
  /// prevent, happening on the one cycle where nothing has been checkpointed yet.
  /// </summary>
  [Test]
  public async Task Advance_WhenAnotherInstanceBaselinedFirst_SkipsTheCycleAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }

    await _suppressWatermarkInsertsAsync(conn);

    var coordinator = (IWorkCoordinator)new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    await Assert.That(await coordinator.AdvanceIntegrityCheckpointAsync()).IsNull()
      .Because("the instance whose baseline insert affected no row did not take the window, and only "
             + "the one that did may publish a checkpoint for it");
  }

  /// <summary>
  /// The baseline distinguishes "I inserted the row" from "someone else did" by the affected-row
  /// count, and answers null for the second — a normal, silent skip. A write that FAILED must not
  /// arrive at that same answer: no row exists, so every later cycle re-enters the baseline and
  /// fails the same way, and a stream-integrity sweep that never checkpoints looks exactly like a
  /// fleet with nothing to report. The failure has to reach the caller.
  /// </summary>
  [Test]
  public async Task Advance_WhenTheBaselineWriteIsRefused_SurfacesTheFailureRatherThanASilentSkipAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }

    await _rejectWatermarkInsertsAsync(conn);

    var coordinator = (IWorkCoordinator)new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    await Assert.That(async () => await coordinator.AdvanceIntegrityCheckpointAsync())
      .Throws<PostgresException>()
      .Because("a refused baseline is not the same event as losing the baseline race, and reporting "
             + "it as one hides a sweep that can never start");

    await Assert.That(await _readWatermarkAsync(conn)).IsNull()
      .Because("nothing was written, so a later cycle must still see no watermark and try again");
  }
}
