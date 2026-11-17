// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2365: the seal gauges' reading (<c>wh_integrity_seal_reading</c>, migration 490), through the coordinator: per lane,
/// how far epochs are sealed and how long ago the frontier last moved, and how much of the recent sealed history the
/// re-check covered.
/// </summary>
/// <docs>resilience/stream-integrity#integrity-instruments</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/490_IntegritySealReading.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class IntegritySealReadingSqlTests : EFCoreTestBase {
  private static readonly DateTimeOffset _now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

  private async Task _executeAsync(string sql, params (string Name, object Value)[] parameters) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private Task<Whizbang.Core.Observability.IntegritySealReading> _readAsync() {
    var ctx = new WorkCoordinationDbContext(DbContextOptions);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions())
      .GetIntegritySealReadingAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(7), _now);
  }

  [Test]
  public async Task TheReading_IsEachLanesFrontierAndAge_AndTheRecheckCoverageAsync() {
    var origin = TrackedGuid.New().Value;
    // The local lane sealed through epoch 2 of width 100, ten minutes ago; an origin's lane with nothing sealed yet.
    await _executeAsync("""
      INSERT INTO wh_digest_epoch_frontiers (origin_service_id, closed_through_epoch, epoch_width, updated_at)
      VALUES ('00000000-0000-0000-0000-000000000000', 2, 100, @local), (@origin, -1, 100, @fresh)
      """, ("local", _now.AddMinutes(-10)), ("origin", origin), ("fresh", _now));
    // Four epochs sealed within the lookback: one re-checked within the interval, one before it, two never. One sealed
    // before the lookback, re-checked recently, which the share does not count.
    await _executeAsync("DELETE FROM wh_digest_epoch_seals");
    await _executeAsync("""
      INSERT INTO wh_digest_epoch_seals (origin_service_id, epoch_id, sealed_at, rechecked_at) VALUES
        (@o, 0, @in, @recent), (@o, 1, @in, @stale), (@o, 2, @in, NULL), (@o, 3, @in, NULL), (@o, 4, @old, @recent)
      """, ("o", origin), ("in", _now.AddDays(-5)), ("recent", _now.AddDays(-1)), ("stale", _now.AddDays(-10)),
      ("old", _now.AddDays(-60)));

    var reading = await _readAsync();

    await Assert.That(reading.Reported).IsTrue();
    var local = reading.Lanes.Single(l => l.OriginServiceId == Guid.Empty);
    await Assert.That(local.SealedThrough).IsEqualTo(300L).Because("epochs 0 to 2 of width 100 are sealed");
    await Assert.That(local.AgeSeconds).IsEqualTo(600d);
    var unsealed = reading.Lanes.Single(l => l.OriginServiceId == origin);
    await Assert.That(unsealed.SealedThrough).IsEqualTo(0L).Because("a frontier of -1 seals nothing");
    await Assert.That(unsealed.AgeSeconds).IsEqualTo(0d);
    await Assert.That(reading.SealRecheckCoverageRatio).IsEqualTo(0.25);
  }

  [Test]
  public async Task WithNoLane_OneRowCarriesTheCoverage_AndNothingSealedInTheLookbackHasNoneAsync() {
    await _executeAsync("DELETE FROM wh_digest_epoch_frontiers");
    await _executeAsync("DELETE FROM wh_digest_epoch_seals");

    var empty = await _readAsync();
    await Assert.That(empty.Reported).IsTrue();
    await Assert.That(empty.Lanes).IsEmpty();
    await Assert.That(empty.SealRecheckCoverageRatio).IsNull()
      .Because("with nothing sealed within the lookback the share is undefined, not zero");

    await _executeAsync("""
      INSERT INTO wh_digest_epoch_seals (origin_service_id, epoch_id, sealed_at, rechecked_at)
      VALUES ('00000000-0000-0000-0000-000000000000', 0, @in, @recent)
      """, ("in", _now.AddDays(-1)), ("recent", _now.AddHours(-1)));

    await Assert.That((await _readAsync()).SealRecheckCoverageRatio).IsEqualTo(1d)
      .Because("with no lane, the one row still carries the share");
  }

  /// <summary>
  /// #2365: the reading also counts each lane's events above its frontier, reads this service's origin generation, and
  /// counts the stream buckets carrying a cut's digest.
  /// </summary>
  [Test]
  public async Task TheReading_CountsEachLanesOpenWindow_ReadsTheGeneration_AndCountsCarriedBucketsAsync() {
    var origin = TrackedGuid.New().Value;
    await _executeAsync("DELETE FROM wh_digest_epoch_frontiers");
    await _executeAsync("""
      INSERT INTO wh_digest_epoch_frontiers (origin_service_id, closed_through_epoch, epoch_width, updated_at)
      VALUES ('00000000-0000-0000-0000-000000000000', 0, 100, @now), (@origin, 1, 100, @now)
      """, ("now", _now), ("origin", origin));
    // Local lane sealed through 100: events at 50 (sealed), 150 and 160 (open). Origin lane sealed through 200: 150 sealed,
    // 250 open. A received event never counts in the local lane.
    foreach (var (seq, lane, laneSeq) in new (long, Guid?, long?)[] { (50, null, null), (150, null, null), (160, null, null),
                                                                       (170, origin, 150), (180, origin, 250) }) {
      await _executeAsync("""
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
          commit_sequence, origin_service_id, origin_commit_sequence, flags, created_at)
        VALUES (@e, @s, @s, 'T', 'Contracts.OpenWindowProbe', 'null'::jsonb, 1, @seq, @lane, @laneSeq, 0, NOW())
        """, ("e", TrackedGuid.New().Value), ("s", TrackedGuid.New().Value), ("seq", seq),
        ("lane", (object?)lane ?? DBNull.Value), ("laneSeq", (object?)laneSeq ?? DBNull.Value));
    }
    await _executeAsync("UPDATE wh_settings SET setting_value = '5' WHERE setting_key = 'integrity_origin_generation'");
    await _executeAsync("""
      INSERT INTO wh_stream_digests (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi,
        event_count, carried_lo, carried_hi, carried_count, carried_at)
      VALUES (@o, '', 'Contracts.OpenWindowProbe', @s1, 1, 1, 1, 1, 1, 1, @now),
             (@o, '', 'Contracts.OpenWindowProbe', @s2, 1, 1, 1, NULL, NULL, NULL, NULL)
      """, ("o", origin), ("s1", TrackedGuid.New().Value), ("s2", TrackedGuid.New().Value), ("now", _now));

    var reading = await _readAsync();

    var local = reading.Lanes.Single(l => l.OriginServiceId == Guid.Empty);
    await Assert.That(local.OpenWindowEvents).IsEqualTo(2L).Because("150 and 160 are above the local frontier of 100");
    await Assert.That(local.OpenWindowCapped).IsFalse();
    await Assert.That(reading.Lanes.Single(l => l.OriginServiceId == origin).OpenWindowEvents).IsEqualTo(1L)
      .Because("the origin lane counts by the origin's sequence: only 250 is above 200");
    await Assert.That(reading.OriginGeneration).IsEqualTo(5L);
    await Assert.That(reading.CarriedStreams).IsEqualTo(1L).Because("only a bucket still carrying a cut's detail counts");
    await Assert.That(reading.CarriedStreamsCapped).IsFalse();
  }
}
