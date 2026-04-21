using System.Text.Json;
using Dapper;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Partition consistency invariant: for any given stream_id, the partition_number
/// computed and stored in wh_inbox must equal the partition_number stored in
/// wh_active_streams. A mismatch causes claim_orphaned_inbox to deadlock — the
/// partition-modulo filter routes a message to instance rank A while the
/// stream-ownership NOT EXISTS clause is satisfied by a live instance at rank B,
/// leaving the message permanently unclaimable.
///
/// Observed in JDX BFF dev on 2026-04-20: 201 inbox rows wedged across three
/// live/heartbeating pods because wh_inbox.partition_number used p_partition_count=2
/// (IWorkCoordinator.StoreInboxMessagesAsync default) while wh_active_streams used
/// p_partition_count=10_000 (WorkCoordinatorPublisherOptions.PartitionCount default).
/// </summary>
[Category("Integration")]
public class PartitionConsistencyTests : PostgresTestBase {
  private DapperWorkCoordinator _sut = null!;
  private Guid _instanceId;
  private readonly Uuid7IdProvider _idProvider = new();
  private static readonly JsonSerializerOptions _jsonOptions;

  static PartitionConsistencyTests() {
    var baseOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    _jsonOptions = new JsonSerializerOptions(baseOptions) {
      TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(
        baseOptions.TypeInfoResolver!,
        TestEnvelopeJsonContext.Default
      )
    };
  }

  [Before(Test)]
  public new async Task SetupAsync() {
    await base.SetupAsync();
    _instanceId = _idProvider.NewGuid();
    await _insertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    _sut = new DapperWorkCoordinator(ConnectionString, _jsonOptions);
  }

  /// <summary>
  /// RED anchor. Reproduces the shipped bug by exercising the two store paths
  /// that TransportConsumerWorker and WorkCoordinatorPublisherWorker actually use:
  ///
  ///   - StoreInboxMessagesAsync (direct, fast path) — partitionCount default = 2
  ///   - ProcessWorkBatchAsync with NewInboxMessages (publisher path) — PartitionCount default = 10_000
  ///
  /// For the same stream_id, both wh_inbox.partition_number (from the first call)
  /// and wh_active_streams.partition_number (refreshed end-of-tick by the second)
  /// must agree — otherwise claim_orphaned_inbox cannot route the message to the
  /// instance that owns the stream.
  /// </summary>
  [Test]
  public async Task WhenStoreInboxAndProcessBatchUseDefaults_PartitionNumbersMustMatchAsync() {
    // Arrange — one stream, one message. Two separate store paths, both hitting defaults.
    var streamId = _idProvider.NewGuid();
    var messageId = _idProvider.NewGuid();
    var inboxMessage = _createInboxMessage(messageId, streamId);

    // Act — transport consumer path with the OLD shipped default (partitionCount=2)
    // to reproduce the dev BFF wedge condition exactly.
    await _sut.StoreInboxMessagesAsync([inboxMessage], partitionCount: 2);

    // Act — publisher worker path at the canonical PartitionCount=10_000.
    // This is what causes active_streams to be upserted for the stream on the next tick.
    await _sut.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = _instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
      OutboxCompletions = [],
      OutboxFailures = [],
      InboxCompletions = [],
      InboxFailures = [],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [],
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [],
      NewOutboxMessages = [],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = []
    });

    // Simulate a worker restart: production self-heals stale partition_number rows
    // via WorkCoordinatorPublisherWorker._recomputePartitionsOnStartupAsync, which
    // calls recompute_partition_numbers(). This test exercises the same API.
    await _sut.RecomputePartitionNumbersAsync(partitionCount: 10_000);

    // Assert — both partition_number values for the SAME stream_id must agree.
    var inboxPartition = await _getInboxPartitionNumberAsync(messageId);
    var activeStreamPartition = await _getActiveStreamPartitionNumberAsync(streamId);

    await Assert.That(inboxPartition).IsNotNull()
      .Because("wh_inbox should have a partition_number for a stream-bound message");
    await Assert.That(activeStreamPartition).IsNotNull()
      .Because("wh_active_streams should have a partition_number after a publisher tick");
    await Assert.That(inboxPartition).IsEqualTo(activeStreamPartition)
      .Because("partition_number must be identical across wh_inbox and wh_active_streams for the same stream_id — otherwise claim_orphaned_inbox deadlocks between the modulo filter and the stream-ownership check");
  }

  /// <summary>
  /// Test 2 — the production deadlock, faithfully reproduced. We can't get a clean
  /// wedge by orchestrating via the public API alone because the partition mismatch
  /// also blocks the would-be owner from claiming (so active_streams never gets
  /// populated through the normal path). In production it gets populated by a
  /// PRIOR successfully-claimed message under the new partition_count, then later
  /// inbox writes via the fast path use the old partition_count. Mirror that:
  /// pre-seed wh_active_streams with the partition_count=10_000 owner, then store
  /// the new inbox row via the fast path at partition_count=2, then have all
  /// instances tick.
  /// </summary>
  [Test]
  public async Task WhenActiveStreamsOwnerComputedForLargerPartitionCount_FastPathInboxIsClaimableAsync() {
    var instanceA = _instanceId; // already inserted by SetupAsync
    Guid instanceB = _idProvider.NewGuid();
    Guid instanceC = _idProvider.NewGuid();
    await _insertServiceInstanceAsync(instanceB, "TestService", "test-host-b", 22222);
    await _insertServiceInstanceAsync(instanceC, "TestService", "test-host-c", 33333);

    // Reproduce the dev pattern: smallRank != largeRank.
    var (streamId, messageId, _, _) = await _findDeadlockStreamAsync(instanceA, instanceB, instanceC);
    var owningInstance = await _computeStreamOwnerForLargePartitionAsync(streamId, instanceA, instanceB, instanceC);

    // Pre-seed active_streams as if a prior message had been processed by owningInstance
    // under partition_count=10_000.
    await _seedActiveStreamAsync(streamId, owningInstance, 10_000, leaseSecondsFromNow: 300);

    // Fast path stores the new inbox row at the OLD shipped partition_count=2
    // — reproducing the dev BFF wedge condition.
    var inboxMessage = _createInboxMessage(messageId, streamId);
    await _sut.StoreInboxMessagesAsync([inboxMessage], partitionCount: 2);

    // Simulate a worker restart: production self-heals stale partition_number rows
    // via WorkCoordinatorPublisherWorker._recomputePartitionsOnStartupAsync, which
    // calls recompute_partition_numbers(). This test exercises the same API.
    await _sut.RecomputePartitionNumbersAsync(partitionCount: 10_000);

    // Round-robin three full ticks per instance — plenty of opportunity to claim.
    foreach (var _ in Enumerable.Range(0, 3)) {
      foreach (var inst in new[] { instanceA, instanceB, instanceC }) {
        await _sut.ProcessWorkBatchAsync(_emptyTickRequest(inst));
      }
    }

    var unclaimed = await _countUnclaimedAsync([messageId]);
    await Assert.That(unclaimed).IsEqualTo(0)
      .Because("with active_streams pre-seeded under partition_count=10_000 and inbox stored under partition_count=2, the dev BFF wedge is reproduced — the message is unclaimable until partition_count is unified");
  }

  /// <summary>
  /// Test 3 — wedge reproduction at scale. N streams, N messages, mismatched
  /// partition_count. Drain via one tick per instance and assert every message
  /// got claimed. Today, roughly 2/3 of messages will remain unclaimed (the ones
  /// where the partition-2 routing instance doesn't match the partition-10000
  /// stream owner).
  /// </summary>
  [Test]
  public async Task WhenManyMessagesStoredWithMismatchedPartitionCount_AllAreClaimedWithinOneRoundOfTicksAsync() {
    var instanceA = _instanceId;
    Guid instanceB = _idProvider.NewGuid();
    Guid instanceC = _idProvider.NewGuid();
    await _insertServiceInstanceAsync(instanceB, "TestService", "test-host-b", 22222);
    await _insertServiceInstanceAsync(instanceC, "TestService", "test-host-c", 33333);

    // Store 30 messages via the fast path.
    var messageIds = new List<Guid>();
    for (var i = 0; i < 30; i++) {
      var streamId = _idProvider.NewGuid();
      var messageId = _idProvider.NewGuid();
      messageIds.Add(messageId);
      await _sut.StoreInboxMessagesAsync([_createInboxMessage(messageId, streamId)], partitionCount: 10_000);
    }

    // Round-robin a publisher tick across each instance to populate active_streams,
    // then a second round to claim. Each instance gets two ticks so the modulo
    // filter has a chance to fire.
    foreach (var _ in Enumerable.Range(0, 2)) {
      foreach (var inst in new[] { instanceA, instanceB, instanceC }) {
        await _sut.ProcessWorkBatchAsync(_emptyTickRequest(inst));
      }
    }

    var unclaimed = await _countUnclaimedAsync(messageIds);
    await Assert.That(unclaimed).IsEqualTo(0)
      .Because("after a full round of ticks across all live instances, every stored inbox message must have been claimed — anything left wedged is the production bug");
  }

  /// <summary>
  /// Test 4 — instance scale-up. Two instances drain cleanly, then a third joins,
  /// rank values shift, and any messages stored before the scale-up event must
  /// still drain on the next tick. Today fails because partition_number is fixed
  /// at storage time and the modulo target moves under it.
  /// </summary>
  [Test]
  public async Task WhenThirdInstanceJoinsMidFlight_PreviouslyStoredMessagesStillDrainAsync() {
    var instanceA = _instanceId;
    Guid instanceB = _idProvider.NewGuid();
    await _insertServiceInstanceAsync(instanceB, "TestService", "test-host-b", 22222);

    // Store with 2 active instances
    var messageIds = new List<Guid>();
    for (var i = 0; i < 20; i++) {
      var msgId = _idProvider.NewGuid();
      var streamId = _idProvider.NewGuid();
      messageIds.Add(msgId);
      await _sut.StoreInboxMessagesAsync([_createInboxMessage(msgId, streamId)], partitionCount: 10_000);
    }

    // Scale up: third instance arrives BEFORE any tick has populated active_streams.
    Guid instanceC = _idProvider.NewGuid();
    await _insertServiceInstanceAsync(instanceC, "TestService", "test-host-c", 33333);

    // Drain: two ticks per instance, all three.
    foreach (var _ in Enumerable.Range(0, 2)) {
      foreach (var inst in new[] { instanceA, instanceB, instanceC }) {
        await _sut.ProcessWorkBatchAsync(_emptyTickRequest(inst));
      }
    }

    var unclaimed = await _countUnclaimedAsync(messageIds);
    await Assert.That(unclaimed).IsEqualTo(0)
      .Because("a scale-up event mid-flight must not strand previously-stored messages");
  }

  /// <summary>
  /// Test 5 — instance scale-down past stale_cutoff. The dead instance's
  /// active_streams lease must NOT block survivors from claiming. The SQL comment
  /// in migration 025 lines 36–43 already promises this; pin it with a test.
  /// </summary>
  [Test]
  public async Task WhenStreamOwnerHeartbeatGoesStale_SurvivingInstanceCanClaimAsync() {
    var instanceA = _instanceId;
    Guid instanceB = _idProvider.NewGuid();
    await _insertServiceInstanceAsync(instanceB, "TestService", "test-host-b", 22222);

    var streamId = _idProvider.NewGuid();
    var messageId = _idProvider.NewGuid();

    // Store and let instanceB take the active_streams lease.
    await _sut.StoreInboxMessagesAsync([_createInboxMessage(messageId, streamId)], partitionCount: 10_000);
    await _sut.ProcessWorkBatchAsync(_emptyTickRequest(instanceB));

    // Stale instanceB beyond the threshold (default 30s; push 5 minutes back).
    await _setHeartbeatAsync(instanceB, DateTimeOffset.UtcNow.AddMinutes(-5));

    // Survivor (instanceA) ticks — should drain without waiting for the 5-min lease to expire.
    var batch = await _sut.ProcessWorkBatchAsync(_emptyTickRequest(instanceA));
    var claimedHere = batch.InboxWork.Any(w => w.MessageId == messageId);

    await Assert.That(claimedHere).IsTrue()
      .Because("a stale-heartbeat instance must not block claims via its active_streams lease — recovery is bounded by stale_cutoff, not lease_expiry");
  }

  /// <summary>
  /// Test 6 — PartitionCount changes across a restart. Seed inbox rows under one
  /// PartitionCount, then "restart" by ticking under a different PartitionCount.
  /// Today: nothing migrates the partition_number on existing rows, so the dev
  /// wedge can recur on every config change.
  /// </summary>
  [Test]
  public async Task WhenPartitionCountChangesAcrossRestart_PreviouslyStoredMessagesStillDrainAsync() {
    var instanceA = _instanceId;
    Guid instanceB = _idProvider.NewGuid();
    Guid instanceC = _idProvider.NewGuid();
    await _insertServiceInstanceAsync(instanceB, "TestService", "test-host-b", 22222);
    await _insertServiceInstanceAsync(instanceC, "TestService", "test-host-c", 33333);

    // Pre-restart: store messages under PartitionCount=10_000 via the publisher path.
    var ids = new List<Guid>();
    for (var i = 0; i < 30; i++) {
      var streamId = _idProvider.NewGuid();
      var msgId = _idProvider.NewGuid();
      ids.Add(msgId);
      await _sut.ProcessWorkBatchAsync(_emptyTickRequest(instanceA) with {
        Flags = WorkBatchOptions.SkipInboxClaiming,
        NewInboxMessages = [_createInboxMessage(msgId, streamId)],
        PartitionCount = 10_000
      });
    }

    // Post-restart: a new "version" of the worker comes online with PartitionCount=128.
    foreach (var _ in Enumerable.Range(0, 3)) {
      foreach (var inst in new[] { instanceA, instanceB, instanceC }) {
        await _sut.ProcessWorkBatchAsync(_emptyTickRequest(inst) with { PartitionCount = 128 });
      }
    }

    var unclaimed = await _countUnclaimedAsync(ids);
    await Assert.That(unclaimed).IsEqualTo(0)
      .Because("a PartitionCount change must not strand pre-existing inbox rows — Fix 2 (drain window or recompute migration) is required");
  }

  /// <summary>
  /// Test 7 — cold start. All workers stopped with messages pending. A single
  /// worker starts; it must drain everything regardless of stored partition_number.
  /// Guards the bootstrap path where active_instance_count transitions 0 → 1.
  /// </summary>
  [Test]
  public async Task WhenSingleInstanceStartsCold_DrainsAllPendingMessagesRegardlessOfStoredPartitionAsync() {
    var ids = new List<Guid>();
    for (var i = 0; i < 25; i++) {
      var streamId = _idProvider.NewGuid();
      var msgId = _idProvider.NewGuid();
      ids.Add(msgId);
      await _sut.StoreInboxMessagesAsync([_createInboxMessage(msgId, streamId)], partitionCount: 10_000);
    }

    // Only the single instance from SetupAsync is alive. Tick.
    await _sut.ProcessWorkBatchAsync(_emptyTickRequest(_instanceId));

    var unclaimed = await _countUnclaimedAsync(ids);
    await Assert.That(unclaimed).IsEqualTo(0)
      .Because("a single live instance must drain every pending inbox row in one tick — modulo-by-1 is always 0");
  }

  /// <summary>
  /// Test 8 — NULL partition_number fallback. Messages with no stream_id end up
  /// with partition_number IS NULL. Migration 025 line 31 explicitly tolerates
  /// these. Pin the behavior so a future "tighten the filter" change cannot
  /// regress it.
  /// </summary>
  [Test]
  public async Task WhenInboxRowHasNullPartitionNumber_AnyInstanceCanClaimAsync() {
    Guid instanceB = _idProvider.NewGuid();
    await _insertServiceInstanceAsync(instanceB, "TestService", "test-host-b", 22222);

    using (var connection = new Npgsql.NpgsqlConnection(ConnectionString)) {
      await connection.OpenAsync();
      var nullPartitionMsgId = _idProvider.NewGuid();
      await connection.ExecuteAsync(@"
        INSERT INTO wh_message_deduplication (message_id, first_seen_at) VALUES (@id, NOW());
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, stream_id, partition_number, is_event, status, attempts, received_at, instance_id, lease_expiry)
        VALUES (@id, 'TestHandler', 'TestMessage, TestAssembly', '{}'::jsonb, '{}'::jsonb, 'null'::jsonb, NULL, NULL, false, 1, 0, NOW(), NULL, NULL);",
        new { id = nullPartitionMsgId });

      await _sut.ProcessWorkBatchAsync(_emptyTickRequest(instanceB));

      var unclaimed = await _countUnclaimedAsync([nullPartitionMsgId]);
      await Assert.That(unclaimed).IsEqualTo(0)
        .Because("inbox rows with NULL partition_number (no stream binding) must be claimable by any instance — guards the explicit fallback in migration 025 line 31");
    }
  }

  /// <summary>
  /// Test 9 — WorkBatchCoordinator currently hardcodes PartitionCount=16
  /// (`WorkBatchCoordinator.cs:61`, marked "FUTURE: Make configurable"). Any path
  /// that runs through it inserts inbox/active_streams rows under a third
  /// PartitionCount. Reproduce by exercising both the WorkBatchCoordinator-style
  /// store (PartitionCount=16) and the WorkCoordinatorPublisherWorker-style tick
  /// (PartitionCount=10_000) for the same stream and assert partition consistency.
  /// </summary>
  [Test]
  public async Task WhenStoredViaWorkBatchCoordinatorPartitionCount_ActiveStreamsMustAgreeAsync() {
    var streamId = _idProvider.NewGuid();
    var messageId = _idProvider.NewGuid();
    var inboxMessage = _createInboxMessage(messageId, streamId);

    // Simulate WorkBatchCoordinator's hardcoded PartitionCount=16
    await _sut.ProcessWorkBatchAsync(_emptyTickRequest(_instanceId) with {
      Flags = WorkBatchOptions.SkipInboxClaiming,
      NewInboxMessages = [inboxMessage],
      PartitionCount = 16
    });

    // Now publisher worker tick at the canonical 10_000.
    await _sut.ProcessWorkBatchAsync(_emptyTickRequest(_instanceId) with { PartitionCount = 10_000 });

    var inboxPartition = await _getInboxPartitionNumberAsync(messageId);
    var activeStreamPartition = await _getActiveStreamPartitionNumberAsync(streamId);
    await Assert.That(inboxPartition).IsEqualTo(activeStreamPartition)
      .Because("WorkBatchCoordinator's hardcoded PartitionCount=16 disagrees with WorkCoordinatorPublisherOptions.PartitionCount=10_000 — same class of bug as the StoreInboxMessagesAsync default=2");
  }

  /// <summary>
  /// Test 10 — single-instance fallthrough property. With active_instance_count=1
  /// the modulo filter degenerates: any partition_number % 1 == 0 == rank. Seed
  /// inbox rows with deliberately-stale partition values (computed under a
  /// different PartitionCount) and confirm a single live instance claims all of
  /// them on the first tick. This pins the "scale to one to unwedge" property
  /// so it survives the Fix 2 modulo refactor.
  /// </summary>
  [Test]
  public async Task SingleInstance_ClaimsEverythingRegardlessOfStoredPartitionNumberAsync() {
    var ids = new List<Guid>();
    using (var connection = new Npgsql.NpgsqlConnection(ConnectionString)) {
      await connection.OpenAsync();
      // Synthesize 20 messages with deliberately weird partition_number values
      // (simulating rows produced by every PartitionCount the repo ever shipped).
      var weirdPartitions = new[] { 0, 1, 7, 15, 31, 99, 5000, 9_999 };
      for (var i = 0; i < 20; i++) {
        var msgId = _idProvider.NewGuid();
        var streamId = _idProvider.NewGuid();
        ids.Add(msgId);
        var part = weirdPartitions[i % weirdPartitions.Length];
        await connection.ExecuteAsync(@"
          INSERT INTO wh_message_deduplication (message_id, first_seen_at) VALUES (@id, NOW());
          INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, stream_id, partition_number, is_event, status, attempts, received_at, instance_id, lease_expiry)
          VALUES (@id, 'TestHandler', 'TestMessage, TestAssembly', '{}'::jsonb, '{}'::jsonb, 'null'::jsonb, @streamId, @part, false, 1, 0, NOW(), NULL, NULL);",
          new { id = msgId, streamId, part });
      }
    }

    await _sut.ProcessWorkBatchAsync(_emptyTickRequest(_instanceId));

    var unclaimed = await _countUnclaimedAsync(ids);
    await Assert.That(unclaimed).IsEqualTo(0)
      .Because("with one live instance, the partition modulo filter is a no-op (anything % 1 == 0) — every row must be claimed regardless of stored partition_number");
  }

  /// <summary>
  /// Test 11 — proves <see cref="IWorkCoordinator.RecomputePartitionNumbersAsync"/>
  /// (Fix 2) self-heals a wedged database. Reproduces the dev BFF state
  /// (active_streams under partition_count=10_000, inbox under partition_count=2),
  /// calls recompute, and asserts every wedged row drains on the next tick.
  /// </summary>
  [Test]
  public async Task RecomputePartitionNumbers_SelfHealsTheDevWedgeAsync() {
    var instanceA = _instanceId;
    Guid instanceB = _idProvider.NewGuid();
    Guid instanceC = _idProvider.NewGuid();
    await _insertServiceInstanceAsync(instanceB, "TestService", "test-host-b", 22222);
    await _insertServiceInstanceAsync(instanceC, "TestService", "test-host-c", 33333);

    // Reproduce wedge: smallRank != largeRank.
    var (streamId, messageId, _, _) = await _findDeadlockStreamAsync(instanceA, instanceB, instanceC);
    var owningInstance = await _computeStreamOwnerForLargePartitionAsync(streamId, instanceA, instanceB, instanceC);

    await _seedActiveStreamAsync(streamId, owningInstance, 10_000, leaseSecondsFromNow: 300);

    // Store via the OLD shipped default to wedge the row.
    var inboxMessage = _createInboxMessage(messageId, streamId);
    await _sut.StoreInboxMessagesAsync([inboxMessage], partitionCount: 2);

    // Sanity check the wedge: a round of ticks shouldn't drain it.
    foreach (var inst in new[] { instanceA, instanceB, instanceC }) {
      await _sut.ProcessWorkBatchAsync(_emptyTickRequest(inst));
    }
    await Assert.That(await _countUnclaimedAsync([messageId])).IsEqualTo(1)
      .Because("setup verification — the wedge must be present before recompute");

    // Act — recompute against the canonical PartitionCount.
    var result = await _sut.RecomputePartitionNumbersAsync(partitionCount: 10_000);

    await Assert.That(result.AnyRecomputed).IsTrue()
      .Because("recompute must report the row(s) it healed");
    await Assert.That(result.InboxRowsRecomputed).IsGreaterThanOrEqualTo(1)
      .Because("the wedged inbox row's partition_number must have been re-hashed under PartitionCount=10_000");

    // After recompute, ticks should drain the previously-wedged row.
    foreach (var _ in Enumerable.Range(0, 3)) {
      foreach (var inst in new[] { instanceA, instanceB, instanceC }) {
        await _sut.ProcessWorkBatchAsync(_emptyTickRequest(inst));
      }
    }

    var stillUnclaimed = await _countUnclaimedAsync([messageId]);
    await Assert.That(stillUnclaimed).IsEqualTo(0)
      .Because("the wedged message must drain after recompute aligns wh_inbox.partition_number with wh_active_streams.partition_number");

    // Idempotency — a second recompute on a now-consistent database is a no-op.
    var secondRun = await _sut.RecomputePartitionNumbersAsync(partitionCount: 10_000);
    await Assert.That(secondRun.AnyRecomputed).IsFalse()
      .Because("recompute must be idempotent — re-running with the same PartitionCount on a consistent database changes no rows");
  }

  // ========================================
  // Helpers — mirror InboxNullLeaseTests.cs
  // ========================================

  private ProcessWorkBatchRequest _emptyTickRequest(Guid instanceId) {
    return new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = $"test-host-{instanceId:N}",
      ProcessId = 12345,
      OutboxCompletions = [],
      OutboxFailures = [],
      InboxCompletions = [],
      InboxFailures = [],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [],
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [],
      NewOutboxMessages = [],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = []
    };
  }

  /// <summary>
  /// Generates stream ids until it finds one whose mod-2 routing rank differs from
  /// its mod-10000-then-mod-3 routing rank, given the three instance ids supplied.
  /// Returns (streamId, messageId, smallRank, largeRank).
  /// </summary>
  private async Task<(Guid streamId, Guid messageId, int smallRank, int largeRank)> _findDeadlockStreamAsync(
    Guid instanceA, Guid instanceB, Guid instanceC) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    for (var attempt = 0; attempt < 200; attempt++) {
      var streamId = _idProvider.NewGuid();
      var smallPart = await connection.QuerySingleAsync<int>(
        "SELECT compute_partition(@s, 2)", new { s = streamId });
      var largePart = await connection.QuerySingleAsync<int>(
        "SELECT compute_partition(@s, 10000)", new { s = streamId });
      var smallRank = smallPart % 3;
      var largeRank = largePart % 3;
      if (smallRank != largeRank) {
        return (streamId, _idProvider.NewGuid(), smallRank, largeRank);
      }
    }
    throw new InvalidOperationException("Could not synthesize a deadlock stream after 200 tries — UUID7 distribution unexpectedly tight");
  }

  /// <summary>
  /// Returns whichever of the three supplied instance ids has the rank
  /// (`compute_partition(stream, 10000) % 3`) that determines stream ownership
  /// when active_streams is upserted at partition_count=10_000.
  /// </summary>
  private async Task<Guid> _computeStreamOwnerForLargePartitionAsync(
    Guid streamId, Guid a, Guid b, Guid c) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var largePart = await connection.QuerySingleAsync<int>(
      "SELECT compute_partition(@s, 10000)", new { s = streamId });
    var ranks = new[] { a, b, c }.OrderBy(x => x).ToArray();
    return ranks[largePart % 3];
  }

  private async Task<int> _countUnclaimedAsync(List<Guid> messageIds) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return await connection.QuerySingleAsync<int>(
      "SELECT COUNT(*) FROM wh_inbox WHERE message_id = ANY(@ids) AND instance_id IS NULL",
      new { ids = messageIds.ToArray() });
  }

  private async Task _seedActiveStreamAsync(Guid streamId, Guid ownerInstanceId, int partitionCount, int leaseSecondsFromNow) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_active_streams (stream_id, assigned_instance_id, lease_expiry, partition_number, last_activity_at)
      VALUES (@streamId, @ownerInstanceId, NOW() + (@leaseSecondsFromNow || ' seconds')::interval, compute_partition(@streamId, @partitionCount), NOW())
      ON CONFLICT ON CONSTRAINT wh_active_streams_pkey DO UPDATE SET
        assigned_instance_id = EXCLUDED.assigned_instance_id,
        lease_expiry = EXCLUDED.lease_expiry,
        partition_number = EXCLUDED.partition_number,
        last_activity_at = EXCLUDED.last_activity_at",
      new { streamId, ownerInstanceId, partitionCount, leaseSecondsFromNow });
  }

  private async Task _setHeartbeatAsync(Guid instanceId, DateTimeOffset heartbeat) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(
      "UPDATE wh_service_instances SET last_heartbeat_at = @heartbeat WHERE instance_id = @instanceId",
      new { instanceId, heartbeat });
  }

  private InboxMessage _createInboxMessage(Guid messageId, Guid streamId) {
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = streamId,
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    };
  }

  private static MessageEnvelope<JsonElement> _createTestEnvelope(Guid messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private async Task _insertServiceInstanceAsync(Guid instanceId, string serviceName, string hostName, int processId) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@instanceId, @serviceName, @hostName, @processId, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()",
      new { instanceId, serviceName, hostName, processId });
  }

  private async Task<int?> _getInboxPartitionNumberAsync(Guid messageId) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return await connection.QueryFirstOrDefaultAsync<int?>(
      "SELECT partition_number FROM wh_inbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<int?> _getActiveStreamPartitionNumberAsync(Guid streamId) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return await connection.QueryFirstOrDefaultAsync<int?>(
      "SELECT partition_number FROM wh_active_streams WHERE stream_id = @streamId",
      new { streamId });
  }
}
