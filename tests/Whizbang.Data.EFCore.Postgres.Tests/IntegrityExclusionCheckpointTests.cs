// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2588: a continuity checkpoint counts an excluded event the origin holds in its window, and this side's count leaves
/// it out through the eligibility rule, so the checkpoint comparison adds the excluded events back (and the pending
/// ones, whose place it cannot know): an excluded event is never a gap. Positive control: the flag alone, with no mark,
/// leaves the window short and registers a gap.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityExclusionAdjustment.cs</code-under-test>
/// <docs>resilience/integrity-exclusions#cross-service-agreement</docs>
[Category("Integration")]
[NotInParallel("IntegrityCheckpoint")]   // the checkpoint receptor keeps a process-wide repair policy and ownership-conflict memory
[Category("Shard3")]
public class IntegrityExclusionCheckpointTests : EFCoreTestBase {
  /// <summary>When the exclusions API stamps marks, the log and requests: a fixed instant, never the host clock.</summary>
  private static readonly FakeTimeProvider _clock = new(new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero));

  private const string TYPE = "Contracts.CheckpointProbe, Contracts";
  private const int EXCLUDED_FLAG = (int)EventFlags.IntegrityExcluded;
  private const string TENANT = "tenant-c";

  [Test]
  public async Task AnExcludedEventTheCheckpointCounts_IsNotAGap_WhetherHeldOrPendingAsync() {
    var origin = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var held = await _receivedAsync(conn, origin, seq: 1);
    _ = await _receivedAsync(conn, origin, seq: 2);
    var pending = await _receivedAsync(conn, origin, seq: null);
    var outcomes = await _exclusions().ExcludeAsync(new IntegrityExclusionRequest {
      Reason = "unresolvable",
      RequestedBy = "operator-a",
      EventIds = [held, pending],
    });
    await Assert.That(outcomes.Outcomes.Sum(o => o.Count)).IsEqualTo(2);

    var tracker = new IntegrityGapTracker();
    await _checkpointAsync(tracker, origin, to: 3, count: 3);

    await Assert.That(tracker.PeekPending(origin)).IsEmpty()
      .Because("the origin counts three, this side counts one, and the two excluded events make up the rest");
  }

  [Test]
  public async Task AGapRegisteredBeforeTheMark_IsClearedByTheNextCheckpointsRecountAsync() {
    var origin = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var flagged = await _receivedAsync(conn, origin, seq: 1);
    _ = await _receivedAsync(conn, origin, seq: 2);
    await _flagAsync(conn, flagged);
    var tracker = new IntegrityGapTracker();
    var dispatcher = new IntegrityWireDispatcher();
    await _checkpointAsync(tracker, origin, to: 2, count: 2, dispatcher: dispatcher);
    await Assert.That(tracker.PeekPending(origin)).Count().IsEqualTo(1).Because("precondition: the window read one short");

    _ = await _exclusions().ExcludeAsync(new IntegrityExclusionRequest {
      Reason = "unresolvable",
      RequestedBy = "operator-a",
      EventIds = [flagged],
    });
    await _checkpointAsync(tracker, origin, to: 2, count: 0, emptyBuckets: true, dispatcher: dispatcher);

    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("the recount that would confirm the gap adds the now excluded event back, so no gap is confirmed");
    await Assert.That(tracker.PeekPending(origin)).IsEmpty();
  }

  [Test]
  public async Task PositiveControl_TheFlagWithoutItsMark_LeavesTheWindowShortAsync() {
    var origin = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var flagged = await _receivedAsync(conn, origin, seq: 1);
    _ = await _receivedAsync(conn, origin, seq: 2);
    await _flagAsync(conn, flagged);

    var tracker = new IntegrityGapTracker();
    await _checkpointAsync(tracker, origin, to: 2, count: 2);

    await Assert.That(tracker.PeekPending(origin)).Count().IsEqualTo(1)
      .Because("without the mark nothing adds the event back, so the window reads one short");
  }

  private static async Task _flagAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"UPDATE wh_event_store SET flags = flags | {EXCLUDED_FLAG} WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", eventId);
    _ = await cmd.ExecuteNonQueryAsync();
  }

  private async Task _checkpointAsync(IntegrityGapTracker tracker, Guid origin, long to, int count, bool emptyBuckets = false,
      IntegrityWireDispatcher? dispatcher = null) {
    var services = new ServiceCollection()
      .AddSingleton<IWorkCoordinator>(_ => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        CreateDbContext(), JsonContextRegistry.CreateCombinedOptions()))
      .AddSingleton<Whizbang.Core.IDispatcher>(dispatcher ?? new IntegrityWireDispatcher())
      .AddSingleton<ITransport>(new IntegrityWireTransport())
      .AddSingleton(tracker)
      .AddSingleton<IConsumerRegistry>(new Consumes())
      .AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(new IntegrityWireInstance("consumer-svc"))
      .AddSingleton(Options.Create(new StreamIntegrityOptions { PublishReportEvents = true }))
      .AddSingleton(_connection());
    await using var provider = services.BuildServiceProvider();
    var receptor = new IntegrityCheckpointReceptor(provider.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<IntegrityCheckpointReceptor>.Instance);
    await receptor.HandleAsync(new IntegrityCheckpoint {
      CheckpointStreamId = origin,
      OriginServiceId = origin,
      OriginServiceName = "origin-svc",
      FromCommitSequence = 0,
      ToCommitSequence = to,
      Buckets = emptyBuckets ? [] : [new CheckpointBucket { TenantScope = TENANT, EventType = TYPE, Count = count }],
      RuleFingerprint = IntegrityPairGuard.Fallback.Stamp(null),
    });
  }

  private PostgresIntegrityExclusions _exclusions() =>
    new(_connection(), new StreamIntegrityOptions(), _clock, "consumer-svc",
      NullLogger<PostgresIntegrityExclusions>.Instance);

  private PostgresArchiveConnection _connection() =>
    new(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, "public");

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task<Guid> _receivedAsync(NpgsqlConnection conn, Guid origin, long? seq) {
    var id = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence)
      VALUES (@id, @s, @s, 'TestAggregate', @t, jsonb_build_object('t', @tenant::text), 1, nextval('wh_commit_seq'), 0,
              NOW() - INTERVAL '2 hours', @o, @seq)
      """;
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("t", TYPE);
    cmd.Parameters.AddWithValue("o", origin);
    cmd.Parameters.AddWithValue("tenant", TENANT);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(seq), NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)seq ?? DBNull.Value });
    _ = await cmd.ExecuteNonQueryAsync();
    return id;
  }

  private sealed class Consumes : IConsumerRegistry {
    public bool HasAnyConsumer(string messageType) => true;
    public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() => [new ConsumedMessageInfo(TYPE, IsEvent: true)];
  }
}
