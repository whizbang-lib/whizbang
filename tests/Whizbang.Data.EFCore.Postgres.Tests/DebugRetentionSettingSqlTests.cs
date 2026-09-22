using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The debug-retention option must land in the row the maintenance sweep actually reads.
///
/// <para>
/// The sweep already guards its purge on <c>wh_settings.debug_mode</c>. Nothing wrote that row, so
/// enabling the documented option made completion retain rows and the sweep deleted them anyway on
/// its next pass — retention that evaporated, and counts that fell while being read.
/// </para>
///
/// <para>
/// Unit tests can pin the rendered value and the key, but only a real database proves the write
/// lands under the exact key the sweep looks up, survives a second call, and round-trips through
/// the <c>::BOOLEAN</c> cast the sweep applies. That cast is the part a string test cannot cover.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/maintenance</docs>
[Category("Integration")]
[Category("Shard3")]
public class DebugRetentionSettingSqlTests : EFCoreTestBase {

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private async Task<string?> _readSettingAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT setting_value FROM wh_settings WHERE setting_key = 'debug_mode'";
    return (await cmd.ExecuteScalarAsync()) as string;
  }

  private async Task<bool?> _readSettingAsBoolAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    // Exactly how the sweep consumes it.
    cmd.CommandText = "SELECT setting_value::BOOLEAN FROM wh_settings WHERE setting_key = 'debug_mode'";
    var v = await cmd.ExecuteScalarAsync();
    return v as bool?;
  }

  [Test]
  public async Task EnablingRetentionWritesTheSettingTheSweepReadsAsync() {
    await using var ctx = CreateDbContext();
    await _coordinator(ctx).SyncDebugRetentionSettingAsync(debugMode: true);

    await Assert.That(await _readSettingAsync()).IsEqualTo("true")
      .Because("the sweep looks up this exact key; a value written anywhere else leaves the purge "
             + "running while the operator believes retention is on");
    await Assert.That(await _readSettingAsBoolAsync()).IsEqualTo(true)
      .Because("the sweep casts the text to BOOLEAN — the written form has to survive that cast, "
             + "which is the one thing a string-level unit test cannot prove");
  }

  [Test]
  public async Task DisablingRetentionOverwritesAStaleTrueAsync() {
    await using var ctx = CreateDbContext();
    var coord = _coordinator(ctx);

    await coord.SyncDebugRetentionSettingAsync(debugMode: true);
    await coord.SyncDebugRetentionSettingAsync(debugMode: false);

    await Assert.That(await _readSettingAsBoolAsync()).IsEqualTo(false)
      .Because("a stale true would disable the purge permanently and grow the inbox without bound "
             + "— strictly worse than the problem debug retention was enabled to diagnose");
  }

  [Test]
  public async Task RepeatedSyncsAreIdempotentAsync() {
    await using var ctx = CreateDbContext();
    var coord = _coordinator(ctx);

    for (var i = 0; i < 3; i++) {
      await coord.SyncDebugRetentionSettingAsync(debugMode: true);
    }

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_settings WHERE setting_key = 'debug_mode'";
    var rows = Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

    await Assert.That(rows).IsEqualTo(1L)
      .Because("this runs every maintenance cycle, so an insert that did not upsert would either "
             + "fail on the primary key or accumulate duplicates the sweep would read arbitrarily");
  }
}
