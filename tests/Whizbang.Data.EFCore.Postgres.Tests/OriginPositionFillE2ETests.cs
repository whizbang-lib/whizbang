// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2544, end to end over two real Postgres stores and the real integrity receptors (<see cref="TwoServiceHarness"/>): a
/// consumer holds an origin's events without the origin's commit sequence (its outbox published them before its stamper
/// reached them). The windowed audit cannot decide the window those events belong to, so the consumer's seal for the
/// origin stays at 0, and with the cut gate on, the stream that also holds a positioned event from the origin can never
/// be archived. The consumer's position fill asks the origin, the origin answers from its store, the consumer fills the
/// positions, and then the audit confirms the windows, the seal advances, and the archive that waited proceeds.
/// </summary>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
[Category("Integration")]
[Category("Shard4")]
[NotInParallel("IntegrityManifestGates")]   // the manifest receptors' per-process gates are shared state
public class OriginPositionFillE2ETests : EFCoreTestBase {
  private const string DURABLE = "TwoService.Contracts.OrderPlaced, TwoService.Contracts";
  private const string PAYLOAD = """{"k":"v"}""";

  [Test]
  public async Task UnpositionedHistory_HoldsTheSealAndTheCut_UntilTheFillAsksTheOrigin_ThenTheAuditConfirmsAndTheCutProceedsAsync() {
    // Narrow audit windows, as a deployed audit's capped windows are narrow next to its history: a window then holds
    // some of the consumer's unpositioned events and not others, so it cannot be confirmed.
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, [],
      configure: o => o.MaxAuditWindowSequences = 2);
    var stream = (Guid)TrackedGuid.New();
    var ids = await h.EmitAsync([.. Enumerable.Range(0, 6).Select(_ => new TwoServiceEmit(stream, DURABLE, 0, null, PAYLOAD))]);
    await Assert.That((await h.DeliverAsync(ids[..5], withSequence: false)).Count).IsEqualTo(5);
    await Assert.That((await h.DeliverAsync(ids[5..])).Count).IsEqualTo(1);
    // Later history of the origin on another stream, positioned: an epoch closes only below the lane's newest position,
    // so the stream's events need a successor to be sealable at all.
    var later = await h.EmitAsync(
      new TwoServiceEmit((Guid)TrackedGuid.New(), DURABLE, 0, null, PAYLOAD),
      new TwoServiceEmit((Guid)TrackedGuid.New(), DURABLE, 0, null, PAYLOAD));
    await Assert.That((await h.DeliverAsync(later)).Count).IsEqualTo(2);
    await h.CheckpointAsync();
    await _setAsync(h, "integrity_cut_waits_for_seal", "true", "boolean");
    await _setAsync(h, "integrity_epoch_width", "2", "integer");

    await _auditAsync(h);
    await _sealEpochsAsync(h);

    await Assert.That(await h.SealAsync()).IsEqualTo(0L)
      .Because("the first window holds events the consumer has only without a position: it stays undecided");
    await Assert.That(await _archiveAsync(h, stream)).IsEqualTo(ArchiveStatus.AWAITING_SEAL)
      .Because("the stream holds a positioned event from the origin above the seal, and the seal cannot move");

    var cycle = await h.RunConsumerPositionFillAsync();

    await Assert.That(cycle.Requested).IsEqualTo(5L);
    await Assert.That(await TwoServiceHarness.CountAsync(h.Consumer,
      "SELECT COUNT(*) FROM wh_event_store WHERE origin_service_id IS NOT NULL AND origin_commit_sequence IS NULL")).IsEqualTo(0L)
      .Because("the origin answered every position from its store, and the fill wrote each");
    var originSequences = await _sequencesAsync(h.Origin, ids);
    await Assert.That(await _sequencesAsync(h.Consumer, ids, origin: true)).IsEqualTo(originSequences)
      .Because("each filled position is the origin's own commit sequence for that event");

    await _auditAsync(h);
    await _sealEpochsAsync(h);

    await Assert.That(await h.SealAsync()).IsGreaterThan(originSequences.Split(',').Max(s => long.Parse(s, CultureInfo.InvariantCulture)))
      .Because("with every position known the windows confirm, and the seal advances past the stream's events");
    await Assert.That(await _archiveAsync(h, stream)).IsEqualTo("archived").Because("the cut that waited now proceeds");
    await Assert.That((await h.RunConsumerPositionFillAsync()).Requested).IsEqualTo(0L).Because("nothing is left to ask for");
  }

  [Test]
  public async Task AnEventTheOriginNoLongerHolds_IsAnsweredUnknown_AndNeverAskedAgainAsync() {
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, []);
    var ids = await h.EmitAsync(
      new TwoServiceEmit((Guid)TrackedGuid.New(), DURABLE, 0, null, PAYLOAD),
      new TwoServiceEmit((Guid)TrackedGuid.New(), DURABLE, 0, null, PAYLOAD));
    _ = await h.DeliverAsync(ids, withSequence: false);
    await h.CheckpointAsync();
    await TwoServiceHarness.ExecuteAsync(h.Origin, "DELETE FROM wh_event_store WHERE event_id = @id", ("id", ids[0]));

    _ = await h.RunConsumerPositionFillAsync();
    await TwoServiceHarness.ExecuteAsync(h.Consumer, "UPDATE wh_origin_position_walk SET next_lap_at = NULL");
    var second = await h.RunConsumerPositionFillAsync();

    await Assert.That(await TwoServiceHarness.CountAsync(h.Consumer,
      "SELECT COUNT(*) FROM wh_origin_position_unknown WHERE event_id = @id", ("id", ids[0]))).IsEqualTo(1L);
    await Assert.That(await TwoServiceHarness.CountAsync(h.Consumer,
      "SELECT COUNT(*) FROM wh_event_store WHERE event_id = @id AND origin_commit_sequence IS NOT NULL", ("id", ids[1]))).IsEqualTo(1L);
    await Assert.That(second.Requested).IsEqualTo(0L)
      .Because("the next lap leaves out the event the origin cannot place, and the other is filled");
  }

  private static async Task _auditAsync(TwoServiceHarness h) {
    // One audit walks up to its window budget; a few cycles reach the origin's head.
    for (var i = 0; i < 5; i++) {
      await h.AuditAsync(TwoServiceAudit.TypesWindowed, DURABLE);
    }
  }

  private static async Task _sealEpochsAsync(TwoServiceHarness h) {
    await TwoServiceHarness.ExecuteAsync(h.Consumer, "UPDATE wh_stream_digests SET updated_at = NOW() - INTERVAL '2 hours'");
    await TwoServiceHarness.ExecuteAsync(h.Consumer, "SELECT close_digest_epochs(NOW(), 0, 1000)");
  }

  private static Task _setAsync(TwoServiceHarness h, string key, string value, string type) =>
    TwoServiceHarness.ExecuteAsync(h.Consumer, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES (@k, @v, @t, 'test')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """, ("k", key), ("v", value), ("t", type));

  private static async Task<string> _archiveAsync(TwoServiceHarness h, Guid stream) {
    await using var conn = new Npgsql.NpgsqlConnection(h.Consumer.ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT archive_status FROM wh_archive_stream(@s, NULL, 'test://archive')";
    cmd.Parameters.AddWithValue("s", stream);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<string> _sequencesAsync(TwoServiceStore store, Guid[] ids, bool origin = false) {
    await using var conn = new Npgsql.NpgsqlConnection(store.ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    var column = origin ? "origin_commit_sequence" : "commit_sequence";
    cmd.CommandText = $"SELECT string_agg({column}::text, ',' ORDER BY event_id) FROM wh_event_store WHERE event_id = ANY(@ids)";
    cmd.Parameters.AddWithValue(nameof(ids), ids);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }
}
