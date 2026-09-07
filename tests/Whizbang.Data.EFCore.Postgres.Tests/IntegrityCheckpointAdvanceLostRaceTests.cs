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
}
