// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.MultiStream;
using Whizbang.Core.Notifications;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.MultiStream;

namespace Whizbang.Data.EFCore.Postgres.Tests.MultiStream;

/// <summary>
/// Migration 208 (#1358): a multi-stream command's run, its streams, and the ownership rule that decides which instance
/// works each stream. The rule is the work claim's: a stream leased to this instance is its own; a stream leased to a
/// live sibling is not; any other stream belongs to whoever owns its partition, under the published assignment or, with
/// none, the ranking of the live instances.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#ownership</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/208_MultiStreamCommands.sql</code-under-test>
[Category("Shard3")]
public class MultiStreamCommandStoreSqlTests : EFCoreTestBase {
  private const int PARTITIONS = 10_000;
  private const string COMMAND = "Probe.Command, Probe";
  private static readonly TimeSpan _lease = TimeSpan.FromMinutes(5);
  private static readonly TimeSpan _releaseDelay = TimeSpan.FromMinutes(1);
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(30);

  // --- recording a run -------------------------------------------------------------------------

  [Test]
  public async Task RecordRunAsync_ANewRun_IsPendingForEveryStreamAsync() {
    var store = _store();
    var run = _run(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

    var recorded = await store.RecordRunAsync(run, CancellationToken.None);

    await Assert.That(recorded).IsTrue();
    var status = await store.ReadRunAsync(run.RunId, CancellationToken.None);
    await Assert.That(status!.CommandType).IsEqualTo(COMMAND);
    await Assert.That(status.StreamCount).IsEqualTo(3);
    await Assert.That(status.Pending).IsEqualTo(3);
    await Assert.That(status.Succeeded).IsEqualTo(0);
    await Assert.That(status.Failed).IsEqualTo(0);
    await Assert.That(status.CompletedAt).IsNull();
  }

  [Test]
  public async Task RecordRunAsync_TheSameRunTwice_RecordsItOnceAndSaysSoAsync() {
    var store = _store();
    var run = _run(Guid.CreateVersion7(), Guid.CreateVersion7());
    _ = await store.RecordRunAsync(run, CancellationToken.None);

    var again = await store.RecordRunAsync(run with { StreamIds = [Guid.CreateVersion7()] }, CancellationToken.None);

    await Assert.That(again).IsFalse()
      .Because("a redelivered command, or one recorded before the outbox and again after the inbox, is one run");
    await Assert.That((await store.ReadRunAsync(run.RunId, CancellationToken.None))!.StreamCount).IsEqualTo(2);
  }

  [Test]
  public async Task RecordRunAsync_AStreamNamedTwice_IsOneStreamAsync() {
    var store = _store();
    var stream = Guid.CreateVersion7();
    var run = _run(stream, stream);

    _ = await store.RecordRunAsync(run, CancellationToken.None);

    await Assert.That((await store.ReadRunAsync(run.RunId, CancellationToken.None))!.StreamCount).IsEqualTo(1);
  }

  [Test]
  public async Task RecordRunAsync_ANewRun_WakesEveryInstanceAsync() {
    var store = _store();
    await using var listener = await _openAsync();
    await _execAsync(listener, $"LISTEN \"{PgMultiStreamCommandStore.CHANNEL}\"");
    string? payload = null;
    listener.Notification += (_, e) => payload = e.Payload;
    var run = _run(Guid.CreateVersion7());

    _ = await store.RecordRunAsync(run, CancellationToken.None);
    using (var deadline = new CancellationTokenSource(_signalDeadline)) {
      await listener.WaitAsync(deadline.Token);
    }

    await Assert.That(payload).IsEqualTo(run.RunId.ToString());
  }

  [Test]
  public async Task ReadRunAsync_AnUnknownRun_IsNullAsync() {
    await Assert.That(await _store().ReadRunAsync(Guid.CreateVersion7(), CancellationToken.None)).IsNull();
  }

  [Test]
  public async Task RecordRunAsync_UsesTheWorkClaimsPartitionCountAsync() {
    var store = new PgMultiStreamCommandStore(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString, SignalingMode = WorkSignalingMode.ListenNotify }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      Options.Create(new ClaimWorkerOptions { PartitionCount = 1 }));
    var run = _run(Guid.CreateVersion7(), Guid.CreateVersion7());

    _ = await store.RecordRunAsync(run, CancellationToken.None);

    await using var conn = await _openAsync();
    await Assert.That(await _scalarAsync<long>(conn, $"SELECT count(*) FROM wh_multi_stream_run_streams WHERE run_id = '{run.RunId}' AND partition_number = 0"))
      .IsEqualTo(2L)
      .Because("a stream's partition here must be the one its live work is claimed by, so the claim's count is used");
  }

  [Test]
  public async Task Constructor_NoOptionsOrConfiguration_ThrowsAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    await Assert.That(() => new PgMultiStreamCommandStore(null!, configuration)).Throws<ArgumentNullException>();
    await Assert.That(() => new PgMultiStreamCommandStore(Options.Create(new WhizbangNotificationOptions()), null!)).Throws<ArgumentNullException>();
  }

  // --- the ownership rule ----------------------------------------------------------------------

  [Test]
  public async Task ClaimAsync_UnderAPublishedAssignment_EachInstanceClaimsOnlyItsPartitionShareAsync() {
    var (a, b) = _twoInstancesInIdOrder();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a, b]);
    var streams = Enumerable.Range(0, 40).Select(_ => Guid.CreateVersion7()).ToArray();
    var store = _store();
    _ = await store.RecordRunAsync(_run(streams), CancellationToken.None);

    var byA = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);
    var byB = await store.ClaimAsync(new MultiStreamClaim(b, 100, _lease, version), CancellationToken.None);

    var partitions = await _partitionsAsync(conn, streams);
    var expectedA = streams.Where(s => partitions[s] % 2 == 0).ToHashSet();
    await Assert.That(byA.Select(i => i.StreamId).ToHashSet().SetEquals(expectedA)).IsTrue()
      .Because("member 0 owns the even partitions under a two-member assignment");
    await Assert.That(byB.Select(i => i.StreamId).ToHashSet().SetEquals(streams.Except(expectedA))).IsTrue();
  }

  /// <summary>
  /// The rows a claim returns for one run carry that run's payload once: every one of them shares the same string. The
  /// claim returns the payload on each row, and a payload materialized per row held one copy per stream of the batch,
  /// which for a run recorded with its stream ids (about 5 MB at 127,288 streams) was hundreds of megabytes a batch.
  /// </summary>
  [Test]
  public async Task ClaimAsync_TheRowsOfOneRun_ShareItsPayloadOnceAsync() {
    var (a, _) = _twoInstancesInIdOrder();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, a);
    var streams = Enumerable.Range(0, 20).Select(_ => Guid.CreateVersion7()).ToArray();
    var store = _store();
    _ = await store.RecordRunAsync(_run(streams), CancellationToken.None);

    var claimed = await store.ClaimAsync(new MultiStreamClaim(a, 50, _lease, null), CancellationToken.None);

    await Assert.That(claimed.Count).IsEqualTo(streams.Length);
    await Assert.That(claimed.Select(i => i.Payload).Distinct(ReferenceEqualityComparer.Instance).Count()).IsEqualTo(1)
      .Because("a run's payload is held once for the whole batch, not once per claimed stream");
  }

  /// <summary>
  /// The claim sends a run's payload on one of its rows, not on every row. Sent per row, a run recorded with its stream
  /// ids (about 5 MB at 127,288 streams) crossed the wire 50 times a batch, about 630 GB over one run, even when the
  /// client kept a single copy.
  /// </summary>
  [Test]
  public async Task TheClaimFunction_SendsEachRunsPayloadOnceAsync() {
    var (a, _) = _twoInstancesInIdOrder();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, a);
    var store = _store();
    _ = await store.RecordRunAsync(_run([.. Enumerable.Range(0, 20).Select(_ => Guid.CreateVersion7())]), CancellationToken.None);
    _ = await store.RecordRunAsync(_run([.. Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7())]), CancellationToken.None);

    var payloadRows = await _scalarAsync<long>(conn,
      $"SELECT count(*) FILTER (WHERE payload IS NOT NULL) FROM wh_claim_multi_stream_work('{a}'::uuid, 50, 300)");

    await Assert.That(payloadRows).IsEqualTo(2L)
      .Because("each run's payload is sent once per claim, on one of its rows");
  }

  [Test]
  public async Task ClaimAsync_WithNoAssignment_RanksTheLiveInstancesAsync() {
    var (a, b) = _twoInstancesInIdOrder();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, a);
    await _heartbeatAsync(conn, b);
    var streams = Enumerable.Range(0, 40).Select(_ => Guid.CreateVersion7()).ToArray();
    var store = _store();
    _ = await store.RecordRunAsync(_run(streams), CancellationToken.None);

    var byB = await store.ClaimAsync(new MultiStreamClaim(b, 100, _lease, null), CancellationToken.None);

    var partitions = await _partitionsAsync(conn, streams);
    await Assert.That(byB.Select(i => i.StreamId).ToHashSet().SetEquals(streams.Where(s => partitions[s] % 2 == 1))).IsTrue()
      .Because("with no assignment the live instances rank by id, as the work claim's fallback does");
  }

  [Test]
  public async Task ClaimAsync_WithNoAssignment_APeerNotYetReady_IsNotRankedAsync() {
    var (a, b) = _twoInstancesInIdOrder();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, a);
    await _heartbeatAsync(conn, b);
    // #1638: a is alive (a fresh heartbeat) but still migrating, so it is not eligible for work.
    await _execAsync(conn, $"SELECT record_instance_state('{a}'::uuid, 'Migrating')");
    var streams = Enumerable.Range(0, 40).Select(_ => Guid.CreateVersion7()).ToArray();
    var store = _store();
    _ = await store.RecordRunAsync(_run(streams), CancellationToken.None);

    var byB = await store.ClaimAsync(new MultiStreamClaim(b, 100, _lease, null), CancellationToken.None);

    await Assert.That(byB.Select(i => i.StreamId).ToHashSet().SetEquals(streams)).IsTrue()
      .Because("ranked with its migrating peer, b would leave the even partitions to an instance that cannot claim them");
  }

  [Test]
  public async Task ClaimAsync_AStaleCachedAssignment_FallsBackToRankingAsync() {
    var (a, b) = _twoInstancesInIdOrder();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a, b]);
    await _execAsync(conn, "UPDATE wh_published_partition_assignment SET lease_expires_at = clock_timestamp() - INTERVAL '1 second'");
    // Only b is live: ranked alone, it owns every partition.
    var stream = Guid.CreateVersion7();
    var store = _store();
    _ = await store.RecordRunAsync(_run(stream), CancellationToken.None);
    await _execAsync(conn, $"UPDATE wh_service_instances SET last_heartbeat_at = clock_timestamp() - INTERVAL '1 hour' WHERE instance_id = '{a}'");

    var byB = await store.ClaimAsync(new MultiStreamClaim(b, 100, _lease, version), CancellationToken.None);

    await Assert.That(byB.Select(i => i.StreamId)).IsEquivalentTo([stream]);
  }

  [Test]
  public async Task ClaimAsync_AStreamLeasedToThisInstance_IsClaimedWhateverItsPartitionAsync() {
    var (a, b) = _twoInstancesInIdOrder();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a, b]);
    var stream = await _streamInPartitionShareAsync(conn, rank: 1, count: 2);
    await _leaseStreamAsync(conn, stream, a);
    var store = _store();
    _ = await store.RecordRunAsync(_run(stream), CancellationToken.None);

    var byB = await store.ClaimAsync(new MultiStreamClaim(b, 100, _lease, version), CancellationToken.None);
    var byA = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    await Assert.That(byB).IsEmpty()
      .Because("the partition is b's, but a holds the stream's lease and is live, so the stream's live work is on a");
    await Assert.That(byA.Select(i => i.StreamId)).IsEquivalentTo([stream]);
  }

  [Test]
  public async Task ClaimAsync_AStreamLeasedToADeadInstance_GoesToItsPartitionOwnerAsync() {
    var (a, b) = _twoInstancesInIdOrder();
    var dead = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a, b]);
    var stream = await _streamInPartitionShareAsync(conn, rank: 0, count: 2);
    await _leaseStreamAsync(conn, stream, dead);
    var store = _store();
    _ = await store.RecordRunAsync(_run(stream), CancellationToken.None);

    var byA = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    await Assert.That(byA.Select(i => i.StreamId)).IsEquivalentTo([stream])
      .Because("an owner with no heartbeat holds nothing, so the partition decides");
  }

  [Test]
  public async Task ClaimAsync_AStreamWhoseLeaseExpired_GoesToItsPartitionOwnerAsync() {
    var (a, b) = _twoInstancesInIdOrder();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a, b]);
    var stream = await _streamInPartitionShareAsync(conn, rank: 0, count: 2);
    await _leaseStreamAsync(conn, stream, b, expired: true);
    var store = _store();
    _ = await store.RecordRunAsync(_run(stream), CancellationToken.None);

    var byB = await store.ClaimAsync(new MultiStreamClaim(b, 100, _lease, version), CancellationToken.None);
    var byA = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    await Assert.That(byB).IsEmpty().Because("an expired lease is not ownership");
    await Assert.That(byA.Select(i => i.StreamId)).IsEquivalentTo([stream]);
  }

  [Test]
  public async Task ClaimAsync_ReturnsTheRunsCommandAndScopeWithEachStreamAsync() {
    var a = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a]);
    var stream = Guid.CreateVersion7();
    var run = _run(stream) with { Scope = "{\"v\":{}}" };
    var store = _store();
    _ = await store.RecordRunAsync(run, CancellationToken.None);

    var claimed = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    var item = claimed.Single();
    await Assert.That(item.RunId).IsEqualTo(run.RunId);
    await Assert.That(item.StreamId).IsEqualTo(stream);
    await Assert.That(item.CommandType).IsEqualTo(COMMAND);
    await Assert.That(item.Payload).IsEqualTo(run.Payload)
      .Because("the payload is kept as written: a jsonb column would reorder a polymorphic discriminator");
    await Assert.That(item.Scope).IsEqualTo(run.Scope);
    await Assert.That(item.Attempt).IsEqualTo(1);
  }

  [Test]
  public async Task ClaimAsync_AClaimedStream_IsNotOfferedAgainWhileItsLeaseHoldsAsync() {
    var a = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a]);
    var store = _store();
    _ = await store.RecordRunAsync(_run(Guid.CreateVersion7()), CancellationToken.None);
    _ = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    var again = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    await Assert.That(again).IsEmpty();
  }

  [Test]
  public async Task ClaimAsync_AStreamWhoseClaimLapsed_IsOfferedAgainAsTheNextAttemptAsync() {
    var a = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a]);
    var store = _store();
    _ = await store.RecordRunAsync(_run(Guid.CreateVersion7()), CancellationToken.None);
    _ = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);
    await _execAsync(conn, "UPDATE wh_multi_stream_run_streams SET lease_expiry = clock_timestamp() - INTERVAL '1 second'");

    var again = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    await Assert.That(again.Single().Attempt).IsEqualTo(2)
      .Because("an instance that died mid-stream lets the lease lapse, and the stream is worked again");
  }

  [Test]
  public async Task ClaimAsync_TakesNoMoreThanTheBatchOldestRunFirstAsync() {
    var a = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a]);
    var store = _store();
    var older = _run(Guid.CreateVersion7(), Guid.CreateVersion7());
    _ = await store.RecordRunAsync(older, CancellationToken.None);
    _ = await store.RecordRunAsync(_run(Guid.CreateVersion7(), Guid.CreateVersion7()), CancellationToken.None);

    var claimed = await store.ClaimAsync(new MultiStreamClaim(a, 2, _lease, version), CancellationToken.None);

    await Assert.That(claimed.Select(i => i.RunId).Distinct()).IsEquivalentTo([older.RunId]);
    await Assert.That(claimed).Count().IsEqualTo(2);
  }

  // --- outcomes --------------------------------------------------------------------------------

  [Test]
  public async Task CompleteAsync_TheLastStream_CompletesTheRunOnceAsync() {
    var a = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a]);
    var store = _store();
    var run = _run(Guid.CreateVersion7(), Guid.CreateVersion7());
    _ = await store.RecordRunAsync(run, CancellationToken.None);
    var claimed = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    var first = await store.CompleteAsync(a, [new MultiStreamOutcome(run.RunId, claimed[0].StreamId, null)], 3, _releaseDelay, CancellationToken.None);
    var last = await store.CompleteAsync(a, [new MultiStreamOutcome(run.RunId, claimed[1].StreamId, null)], 3, _releaseDelay, CancellationToken.None);
    var repeat = await store.CompleteAsync(a, [new MultiStreamOutcome(run.RunId, claimed[1].StreamId, null)], 3, _releaseDelay, CancellationToken.None);

    await Assert.That(first).IsEmpty().Because("one stream is still pending");
    await Assert.That(last.Single()).IsEqualTo(new MultiStreamRunCompletion(run.RunId, COMMAND, 2, 2, 0));
    await Assert.That(repeat).IsEmpty().Because("a run completes exactly once");
    var status = await store.ReadRunAsync(run.RunId, CancellationToken.None);
    await Assert.That(status!.CompletedAt.HasValue).IsTrue();
    await Assert.That(status.Pending).IsEqualTo(0);
  }

  [Test]
  public async Task CompleteAsync_AFailureBelowTheAttemptLimit_ReleasesTheStreamForAnotherAttemptAsync() {
    var a = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a]);
    var store = _store();
    var run = _run(Guid.CreateVersion7());
    _ = await store.RecordRunAsync(run, CancellationToken.None);
    var claimed = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    var completed = await store.CompleteAsync(a, [new MultiStreamOutcome(run.RunId, claimed[0].StreamId, "TimeoutException: once")], 3, _releaseDelay, CancellationToken.None);
    var retried = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    await Assert.That(completed).IsEmpty();
    await Assert.That(retried.Single().Attempt).IsEqualTo(2);
    await Assert.That((await store.ReadRunAsync(run.RunId, CancellationToken.None))!.Pending).IsEqualTo(1);
  }

  [Test]
  public async Task CompleteAsync_AFailureAtTheAttemptLimit_RecordsTheStreamFailedWithItsErrorAsync() {
    var a = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a]);
    var store = _store();
    var run = _run(Guid.CreateVersion7());
    _ = await store.RecordRunAsync(run, CancellationToken.None);
    var claimed = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    var completed = await store.CompleteAsync(a, [new MultiStreamOutcome(run.RunId, claimed[0].StreamId, "InvalidOperationException: no")], 1, _releaseDelay, CancellationToken.None);

    await Assert.That(completed.Single()).IsEqualTo(new MultiStreamRunCompletion(run.RunId, COMMAND, 1, 0, 1));
    var error = await _scalarAsync<string>(conn, $"SELECT error FROM wh_multi_stream_run_streams WHERE run_id = '{run.RunId}'");
    await Assert.That(error).IsEqualTo("InvalidOperationException: no");
  }

  [Test]
  public async Task CompleteAsync_AReleasedStream_IsOfferedAgainAfterTheDelayWithoutCountingAnAttemptAsync() {
    var a = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a]);
    var store = _store();
    var run = _run(Guid.CreateVersion7());
    _ = await store.RecordRunAsync(run, CancellationToken.None);
    var claimed = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    var completed = await store.CompleteAsync(
      a, [MultiStreamOutcome.Release(run.RunId, claimed[0].StreamId, "no handler here")], 1, _releaseDelay, CancellationToken.None);
    var tooSoon = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);
    await _execAsync(conn, "UPDATE wh_multi_stream_run_streams SET lease_expiry = clock_timestamp() - INTERVAL '1 second'");
    var later = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    await Assert.That(completed).IsEmpty().Because("a released stream has no outcome, even at an attempt limit of one");
    await Assert.That(tooSoon).IsEmpty().Because("the release waits out its delay, so an instance without the handler does not spin on it");
    await Assert.That(later.Single().Attempt).IsEqualTo(1).Because("a release is not an attempt");
    await Assert.That((await store.ReadRunAsync(run.RunId, CancellationToken.None))!.Pending).IsEqualTo(1);
  }

  [Test]
  public async Task CompleteAsync_AStreamThisInstanceNoLongerHolds_IsNotRecordedAsync() {
    var (a, b) = _twoInstancesInIdOrder();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a]);
    var store = _store();
    var run = _run(Guid.CreateVersion7());
    _ = await store.RecordRunAsync(run, CancellationToken.None);
    var claimed = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);

    var completed = await store.CompleteAsync(b, [new MultiStreamOutcome(run.RunId, claimed[0].StreamId, null)], 3, _releaseDelay, CancellationToken.None);

    await Assert.That(completed).IsEmpty();
    await Assert.That((await store.ReadRunAsync(run.RunId, CancellationToken.None))!.Pending).IsEqualTo(1)
      .Because("an outcome counts only from the instance that holds the stream, so a stale worker cannot close it");
  }

  [Test]
  public async Task CompleteAsync_HoldsTheRunUntilItCommits_SoTwoLastOutcomesCannotBothMissTheCompletionAsync() {
    var (a, b) = _twoInstancesInIdOrder();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a, b]);
    var forA = await _streamInPartitionShareAsync(conn, rank: 0, count: 2);
    var forB = await _streamInPartitionShareAsync(conn, rank: 1, count: 2);
    var store = _store();
    var run = _run(forA, forB);
    _ = await store.RecordRunAsync(run, CancellationToken.None);
    _ = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);
    _ = await store.ClaimAsync(new MultiStreamClaim(b, 100, _lease, version), CancellationToken.None);
    await using var first = await _openAsync();
    await using var tx = await first.BeginTransactionAsync();
    await _execAsync(first,
      $"SELECT * FROM wh_complete_multi_stream_work('{a}'::uuid, ARRAY['{run.RunId}']::uuid[], ARRAY['{forA}']::uuid[], ARRAY[NULL]::text[], ARRAY[false], 3, 60)");

    // While a's outcome is uncommitted, b's completion must not be able to decide the run: it has to wait for a, or
    // each sees the other's stream as pending and the run is never completed. The run row is that serialization point.
    await using var second = await _openAsync();
    var refused = await Assert.ThrowsAsync<PostgresException>(
      () => _execAsync(second, $"SELECT 1 FROM wh_multi_stream_runs WHERE run_id = '{run.RunId}' FOR UPDATE NOWAIT"));
    await tx.CommitAsync();
    var completed = await store.CompleteAsync(b, [new MultiStreamOutcome(run.RunId, forB, null)], 3, _releaseDelay, CancellationToken.None);

    await Assert.That(refused!.SqlState).IsEqualTo("55P03");
    await Assert.That(completed.Single().Succeeded).IsEqualTo(2);
  }

  [Test]
  public async Task CompleteAsync_NoOutcomes_CompletesNothingAsync() {
    await Assert.That(await _store().CompleteAsync(Guid.CreateVersion7(), [], 3, _releaseDelay, CancellationToken.None)).IsEmpty();
  }

  // --- retention -------------------------------------------------------------------------------

  [Test]
  public async Task PurgeCompletedAsync_DeletesOnlyRunsCompletedBeforeTheCutoffAsync() {
    var a = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a]);
    var store = _store();
    var done = _run(Guid.CreateVersion7());
    var pending = _run(Guid.CreateVersion7());
    _ = await store.RecordRunAsync(done, CancellationToken.None);
    var claimed = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);
    _ = await store.CompleteAsync(a, [new MultiStreamOutcome(done.RunId, claimed[0].StreamId, null)], 3, _releaseDelay, CancellationToken.None);
    _ = await store.RecordRunAsync(pending, CancellationToken.None);

    var purged = await store.PurgeCompletedAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    await Assert.That(purged).IsEqualTo(1);
    await Assert.That(await store.ReadRunAsync(done.RunId, CancellationToken.None)).IsNull();
    await Assert.That(await store.ReadRunAsync(pending.RunId, CancellationToken.None)).IsNotNull()
      .Because("a run with pending streams is never purged, however old");
    await Assert.That(await _scalarAsync<long>(conn, $"SELECT count(*) FROM wh_multi_stream_run_streams WHERE run_id = '{done.RunId}'")).IsEqualTo(0L);
  }

  [Test]
  public async Task PurgeCompletedAsync_ACutoffBeforeTheCompletion_KeepsTheRunAsync() {
    var a = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var version = await _assignAsync(conn, [a]);
    var store = _store();
    var done = _run(Guid.CreateVersion7());
    _ = await store.RecordRunAsync(done, CancellationToken.None);
    var claimed = await store.ClaimAsync(new MultiStreamClaim(a, 100, _lease, version), CancellationToken.None);
    _ = await store.CompleteAsync(a, [new MultiStreamOutcome(done.RunId, claimed[0].StreamId, null)], 3, _releaseDelay, CancellationToken.None);

    await Assert.That(await store.PurgeCompletedAsync(DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None)).IsEqualTo(0);
  }

  // --- helpers ---------------------------------------------------------------------------------

  private PgMultiStreamCommandStore _store() =>
    new(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString, SignalingMode = WorkSignalingMode.ListenNotify }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build());

  private static MultiStreamRun _run(params Guid[] streams) =>
    new(Guid.CreateVersion7(), COMMAND, "{\"$type\":\"probe\",\"Label\":\"x\"}", streams, null);

  private static (Guid A, Guid B) _twoInstancesInIdOrder() {
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();
    return string.CompareOrdinal(first.ToString(), second.ToString()) < 0 ? (first, second) : (second, first);
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (T)Convert.ChangeType(await cmd.ExecuteScalarAsync(), typeof(T), CultureInfo.InvariantCulture)!;
  }

  private static async Task _heartbeatAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, '{}'::jsonb, NULL, NULL, NULL, @mode)";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue("mode", InstanceConnectionModes.POOLED);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Elects the first member, publishes <paramref name="members"/> in order, and returns the version to claim under.</summary>
  private static async Task<PartitionAssignmentVersion> _assignAsync(NpgsqlConnection conn, Guid[] members) {
    foreach (var member in members) {
      await _heartbeatAsync(conn, member);
    }
    var epoch = await _scalarAsync<long>(conn,
      $"SELECT epoch FROM wh_vote_role('{PartitionAssignerOptions.ROLE}', '{members[0]}'::uuid, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'");
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @inst, @epoch, @members, INTERVAL '90 seconds')";
    cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    cmd.Parameters.AddWithValue("inst", members[0]);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    cmd.Parameters.AddWithValue(nameof(members), members);
    var revision = Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    return new PartitionAssignmentVersion(epoch, revision);
  }

  private static async Task<Dictionary<Guid, int>> _partitionsAsync(NpgsqlConnection conn, Guid[] streams) {
    var partitions = new Dictionary<Guid, int>();
    foreach (var stream in streams) {
      partitions[stream] = await _scalarAsync<int>(conn, $"SELECT compute_partition('{stream}'::uuid, {PARTITIONS})");
    }
    return partitions;
  }

  /// <summary>A fresh stream id whose partition falls in the share of <paramref name="rank"/> of <paramref name="count"/>.</summary>
  private static async Task<Guid> _streamInPartitionShareAsync(NpgsqlConnection conn, int rank, int count) {
    while (true) {
      var stream = Guid.CreateVersion7();
      if (await _scalarAsync<int>(conn, $"SELECT compute_partition('{stream}'::uuid, {PARTITIONS})") % count == rank) {
        return stream;
      }
    }
  }

  private static async Task _leaseStreamAsync(NpgsqlConnection conn, Guid stream, Guid owner, bool expired = false) {
    var expiry = expired ? "clock_timestamp() - INTERVAL '1 second'" : "clock_timestamp() + INTERVAL '5 minutes'";
    await _execAsync(conn,
      "INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry) "
      + $"VALUES ('{stream}', compute_partition('{stream}'::uuid, {PARTITIONS}), '{owner}', clock_timestamp(), {expiry})");
  }
}
