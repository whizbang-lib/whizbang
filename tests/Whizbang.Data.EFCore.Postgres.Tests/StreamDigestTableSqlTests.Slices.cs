// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2361: the full-history compare's heal works in slices. One run takes the next <c>MaxPerRun</c> streams in key order
/// after a stored cursor, heals those written within <c>Lookback</c>, and stores where it stopped; the next run resumes
/// there, and a run that reaches the end completes the pass and starts the next one from the first stream.
/// </summary>
/// <docs>resilience/stream-integrity#full-history-compare</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/458_FullHistoryCompareRunsInSlices.sql</code-under-test>
public partial class StreamDigestTableSqlTests {
  private const string SLICE_TYPE = "Contracts.SliceProbe";
  private static readonly TimeSpan _settle = TimeSpan.FromMinutes(60);

  // Ordered as Postgres orders uuids (byte-wise), so the slices are predictable.
  private static readonly Guid _s1 = Guid.Parse("00000000-0000-7000-8000-000000000001");
  private static readonly Guid _s2 = Guid.Parse("00000000-0000-7000-8000-000000000002");
  private static readonly Guid _s3 = Guid.Parse("00000000-0000-7000-8000-000000000003");

  [Test]
  public async Task ASlice_HealsAtMostMaxPerRunStreams_AndTheNextResumesAfterItAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    foreach (var stream in new[] { _s1, _s2, _s3 }) {
      await _seedDriftedStreamAsync(connection, stream, writtenDaysAgo: 1);
    }
    var request = new FullHistoryCompareRequest(_settle, TimeSpan.FromDays(90), MaxPerRun: 2);

    var first = await coordinator.VerifyDigestTableSliceAsync(request);

    await Assert.That(first.StreamsChecked).IsEqualTo(2).Because("a run takes at most MaxPerRun streams");
    await Assert.That(first.DriftUpdated).IsEqualTo(2);
    await Assert.That(first.PassCompleted).IsFalse();
    await Assert.That((await _digestRowAsync(connection, _s3))!.Value.Count).IsEqualTo(9)
      .Because("the stream after the slice is left for the next run");

    var second = await coordinator.VerifyDigestTableSliceAsync(request);

    await Assert.That(second.StreamsChecked).IsEqualTo(1).Because("the next run resumes after the stored cursor");
    await Assert.That(second.DriftUpdated).IsEqualTo(1);
    await Assert.That(second.PassCompleted).IsTrue();
    await Assert.That(second.PassRuns).IsEqualTo(2).Because("the pass took two runs");

    var third = await coordinator.VerifyDigestTableSliceAsync(request);

    await Assert.That(third.StreamsChecked).IsEqualTo(2).Because("a completed pass starts the next from the first stream");
    await Assert.That(third.TotalDrift).IsEqualTo(0);
  }

  [Test]
  public async Task AStreamLastWrittenBeforeTheLookback_IsNotHealedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    await _seedDriftedStreamAsync(connection, _s1, writtenDaysAgo: 100);
    await _seedDriftedStreamAsync(connection, _s2, writtenDaysAgo: 1);

    var result = await coordinator.VerifyDigestTableSliceAsync(new FullHistoryCompareRequest(_settle, TimeSpan.FromDays(90), 10));

    await Assert.That(result.DriftUpdated).IsEqualTo(1).Because("only history written within the lookback is compared");
    await Assert.That((await _digestRowAsync(connection, _s1))!.Value.Count).IsEqualTo(9);
    await Assert.That((await _digestRowAsync(connection, _s2))!.Value.Count).IsEqualTo(1);
  }

  [Test]
  public async Task APhantomBucket_OfAStreamTheLedgerDoesNotRecord_IsRemovedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    await _seedDigestAsync(connection, ZERO_UUID, "", SLICE_TYPE, _s1, 1, 2, 1);

    var result = await coordinator.VerifyDigestTableSliceAsync(new FullHistoryCompareRequest(_settle, TimeSpan.FromDays(90), 10));

    await Assert.That(result.DriftRemoved).IsEqualTo(1)
      .Because("a bucket with no events and no ledger row is still in the slice: the page reads the digest table's streams too");
    await Assert.That(await _digestRowAsync(connection, _s1)).IsNull();
  }

  [Test]
  public async Task TheAsksStart_IsTheOldestEpochSealedWithinTheLookback_NeverAboveTheSealAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var origin = Guid.NewGuid();
    await using (var cmd = connection.CreateCommand()) {
      cmd.CommandText = """
        INSERT INTO wh_digest_epoch_seals (origin_service_id, epoch_id, sealed_at) VALUES
          (@origin, 3, NOW() - INTERVAL '200 days'), (@origin, 5, NOW() - INTERVAL '30 days'),
          (@origin, 6, NOW() - INTERVAL '10 days'), (@other, 1, NOW() - INTERVAL '1 day')
        """;
      cmd.Parameters.AddWithValue(nameof(origin), origin);
      cmd.Parameters.AddWithValue("other", Guid.NewGuid());
      await cmd.ExecuteNonQueryAsync();
    }

    await Assert.That(await coordinator.GetFullHistoryCompareStartAsync(origin, TimeSpan.FromDays(90), 100, seal: 2_000)).IsEqualTo(500L)
      .Because("the oldest epoch of this origin's lane sealed within the lookback is 5, which starts at 5 x 100");
    await Assert.That(await coordinator.GetFullHistoryCompareStartAsync(origin, TimeSpan.FromDays(90), 100, seal: 300)).IsEqualTo(300L)
      .Because("an ask above the seal could never advance it, so the start is never above the seal");
    await Assert.That(await coordinator.GetFullHistoryCompareStartAsync(origin, TimeSpan.FromDays(5), 100, seal: 2_000)).IsEqualTo(2_000L)
      .Because("with nothing sealed within the lookback, the compare asks what the routine audit asks: the window above the seal");
  }

  /// <summary>A stream with one settled event, a ledger row last written <paramref name="writtenDaysAgo"/> days ago, and a wrong bucket.</summary>
  private static async Task _seedDriftedStreamAsync(NpgsqlConnection connection, Guid stream, int writtenDaysAgo) {
    await _seedEventAsync(connection, stream, 1, SLICE_TYPE, Guid.NewGuid());
    await _seedDigestAsync(connection, ZERO_UUID, "", SLICE_TYPE, stream, 999, 999, 9);
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_stream_lifecycle (stream_id, stream_type, first_appended_at, last_appended_bucket, last_version)
      VALUES (@stream, 'SliceProbe', NOW() - make_interval(days => @days), NOW() - make_interval(days => @days), 1)
      ON CONFLICT (stream_id) DO UPDATE SET last_appended_bucket = EXCLUDED.last_appended_bucket
      """;
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    cmd.Parameters.AddWithValue("days", writtenDaysAgo);
    await cmd.ExecuteNonQueryAsync();
  }
}
