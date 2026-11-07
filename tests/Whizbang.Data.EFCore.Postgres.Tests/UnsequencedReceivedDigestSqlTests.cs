// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2102: the fold of the events a store holds from an origin without the origin's commit sequence. A windowed
/// comparison places them with <see cref="IntegrityDigestMath.PlaceHeld"/>; this is the read that finds them. It folds
/// what every digest fold folds (not ephemeral, not at-most-once, not exempt, settled), only rows with the origin and no
/// sequence, and costs the rows it returns: the named streams' rows, or the origin's sequence-less rows.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#events-held-without-an-origin-sequence</docs>
[Category("Integration")]
[Category("Shard2")]
public class UnsequencedReceivedDigestSqlTests : EFCoreTestBase {
  private const string TYPE = "Contracts.HeldProbe, Contracts";
  private const string OTHER_TYPE = "Contracts.OtherProbe, Contracts";

  // Through the interface: the read is an IWorkCoordinator capability.
  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _seedAsync(NpgsqlConnection conn, Guid? origin, Guid stream, Guid eventId, string type,
      long? originSeq, int flags = 0, string tenant = "tenant-a", string age = "2 hours") {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence)
      VALUES (@event, @stream, @stream, 'TestAggregate', @type, jsonb_build_object('t', @tenant),
              (SELECT COALESCE(MAX(version), 0) + 1 FROM wh_event_store WHERE stream_id = @stream),
              nextval('wh_commit_seq'), @flags, NOW() - INTERVAL '{age}', @origin, @oseq)
      """;
    cmd.Parameters.AddWithValue("event", eventId);
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    cmd.Parameters.AddWithValue(nameof(type), type);
    cmd.Parameters.AddWithValue(nameof(tenant), tenant);
    cmd.Parameters.AddWithValue(nameof(flags), flags);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(origin), NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)origin ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("oseq", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)originSeq ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<(long Lo, long Hi)> _foldAsync(NpgsqlConnection conn, params Guid[] eventIds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT bit_xor(hashtextextended(e::text, 0)), bit_xor(hashtextextended(e::text, 1)) FROM unnest(@ids) AS e
      """;
    cmd.Parameters.AddWithValue("ids", eventIds);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1));
  }

  [Test]
  public async Task ByStream_FoldsOnlyTheOriginsSequencelessEventsOfTheNamedStreams_AsTheDigestFoldsDoAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var otherStream = TrackedGuid.New().Value;
    var heldA = TrackedGuid.New().Value;
    var heldB = TrackedGuid.New().Value;
    await _seedAsync(conn, origin, stream, heldA, TYPE, originSeq: null);
    await _seedAsync(conn, origin, stream, heldB, TYPE, originSeq: null);
    await _seedAsync(conn, origin, stream, TrackedGuid.New().Value, TYPE, originSeq: 41);              // sequenced
    await _seedAsync(conn, TrackedGuid.New().Value, stream, TrackedGuid.New().Value, TYPE, null);      // other origin
    await _seedAsync(conn, null, stream, TrackedGuid.New().Value, TYPE, null);                         // local
    await _seedAsync(conn, origin, stream, TrackedGuid.New().Value, TYPE, null, flags: 8);             // ephemeral
    await _seedAsync(conn, origin, stream, TrackedGuid.New().Value, TYPE, null, flags: (int)EventFlags.AtMostOnce);
    await _seedAsync(conn, origin, stream, TrackedGuid.New().Value, TYPE, null, age: "1 second");      // unsettled
    await _seedAsync(conn, origin, stream, TrackedGuid.New().Value, OTHER_TYPE, null);                 // not asked
    await _seedAsync(conn, origin, otherStream, TrackedGuid.New().Value, TYPE, null);                  // not named

    var digests = await _coordinator(ctx).ComputeUnsequencedReceivedDigestsAsync(
      origin, [TYPE], [stream], TimeSpan.FromHours(1));

    var (lo, hi) = await _foldAsync(conn, heldA, heldB);
    var digest = digests.Single();
    await Assert.That(digest.StreamId).IsEqualTo(stream);
    await Assert.That(digest.TenantScope).IsEqualTo("tenant-a");
    await Assert.That(digest.EventType).IsEqualTo(TYPE);
    await Assert.That(digest.EventCount).IsEqualTo(2);
    await Assert.That(digest.DigestLo).IsEqualTo(lo).Because("the same fold every digest uses, so the lanes XOR together");
    await Assert.That(digest.DigestHi).IsEqualTo(hi);
  }

  [Test]
  public async Task ByType_FoldsPerTenantAndType_AcrossStreamsAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var origin = TrackedGuid.New().Value;
    await _seedAsync(conn, origin, TrackedGuid.New().Value, TrackedGuid.New().Value, TYPE, null);
    await _seedAsync(conn, origin, TrackedGuid.New().Value, TrackedGuid.New().Value, TYPE, null);
    await _seedAsync(conn, origin, TrackedGuid.New().Value, TrackedGuid.New().Value, TYPE, null, tenant: "tenant-b");
    await _seedAsync(conn, origin, TrackedGuid.New().Value, TrackedGuid.New().Value, OTHER_TYPE, null);

    var digests = await _coordinator(ctx).ComputeUnsequencedReceivedDigestsAsync(
      origin, [TYPE], streamIds: null, TimeSpan.FromHours(1));

    await Assert.That(digests.Select(d => $"{d.TenantScope}|{d.EventType}|{d.StreamId}|{d.EventCount}").ToList())
      .IsEquivalentTo([$"tenant-a|{TYPE}|{Guid.Empty}|2", $"tenant-b|{TYPE}|{Guid.Empty}|1"]);
  }

  [Test]
  public async Task NothingHeldWithoutASequence_ReturnsNothingAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await _seedAsync(conn, origin, stream, TrackedGuid.New().Value, TYPE, originSeq: 7);

    await Assert.That(await _coordinator(ctx).ComputeUnsequencedReceivedDigestsAsync(origin, [TYPE], [stream], TimeSpan.FromHours(1)))
      .IsEmpty();
    await Assert.That(await _coordinator(ctx).ComputeUnsequencedReceivedDigestsAsync(origin, [TYPE], null, TimeSpan.FromHours(1)))
      .IsEmpty();
  }

  [Test]
  public async Task NoTypesOrNoStreamsAsked_ReadsNothingAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await _seedAsync(conn, origin, stream, TrackedGuid.New().Value, TYPE, originSeq: null);

    await Assert.That(await _coordinator(ctx).ComputeUnsequencedReceivedDigestsAsync(origin, [], [stream], TimeSpan.FromHours(1)))
      .IsEmpty();
    await Assert.That(await _coordinator(ctx).ComputeUnsequencedReceivedDigestsAsync(origin, [TYPE], [], TimeSpan.FromHours(1)))
      .IsEmpty();
  }

  // ── plan shape: bounded by what it returns, under the generic plan a long-lived connection settles on ──

  private const int SEQUENCED_EVENTS = 20_000;
  private const int SEQUENCELESS_ELSEWHERE = 5_000;

  [Test]
  public async Task ByStream_UnderAGenericPlan_ReadsTheNamedStreamsRows_NotTheOriginsLaneAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var (origin, stream) = await _seedWideOriginAsync(conn);

    var nodes = await _plansOfAsync(conn, cmd => {
      cmd.CommandText = EFCoreWorkCoordinator<WorkCoordinationDbContext>.UnsequencedByStreamSql("public");
      _bind(cmd, origin, [TYPE], [stream]);
    });

    var reads = nodes.Where(n => n.Relation == "wh_event_store").ToList();
    await Assert.That(reads).IsNotEmpty().Because("precondition: the read was planned");
    await Assert.That(reads.Any(n => n.Type == "Seq Scan")).IsFalse();
    await Assert.That(reads.Max(n => n.RowsRead)).IsLessThanOrEqualTo(3)
      .Because($"the named stream holds 3 rows; reading the origin's {SEQUENCED_EVENTS + SEQUENCELESS_ELSEWHERE} rows "
        + "to find them would grow with the store");
  }

  [Test]
  public async Task ByType_UnderAGenericPlan_ReadsTheOriginsSequencelessRows_NotItsSequencedLaneAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var (origin, _) = await _seedWideOriginAsync(conn);

    var nodes = await _plansOfAsync(conn, cmd => {
      cmd.CommandText = EFCoreWorkCoordinator<WorkCoordinationDbContext>.UnsequencedByTypeSql("public");
      _bind(cmd, origin, [TYPE], streamIds: null);
    });

    var reads = nodes.Where(n => n.Relation == "wh_event_store").ToList();
    await Assert.That(reads).IsNotEmpty().Because("precondition: the read was planned");
    await Assert.That(reads.Any(n => n.Type == "Seq Scan")).IsFalse();
    await Assert.That(reads.Max(n => n.RowsRead)).IsLessThanOrEqualTo(SEQUENCELESS_ELSEWHERE + 2)
      .Because($"only the origin's sequence-less rows are read, never its {SEQUENCED_EVENTS} sequenced ones");
  }

  private static void _bind(NpgsqlCommand cmd, Guid origin, string[] types, Guid[]? streamIds) {
    cmd.Parameters.Add(new NpgsqlParameter("p_origin", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = origin });
    cmd.Parameters.Add(new NpgsqlParameter("p_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = types });
    if (streamIds is not null) {
      cmd.Parameters.Add(new NpgsqlParameter("p_streams", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = streamIds });
    }
    cmd.Parameters.Add(new NpgsqlParameter("p_settle", "3600 seconds"));
    cmd.Parameters.Add(new NpgsqlParameter("p_digest_exempt", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = Whizbang.Core.SystemEvents.DigestExemptTypes.TypeNames.ToArray()
    });
  }

  /// <summary>
  /// One origin with a wide sequenced lane on many streams, sequence-less rows on other streams, and one named stream
  /// holding two sequence-less rows and one sequenced row; analyzed.
  /// </summary>
  private static async Task<(Guid Origin, Guid Stream)> _seedWideOriginAsync(NpgsqlConnection conn) {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await _execAsync(conn, $"""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope,
                                  commit_sequence, created_at, origin_service_id, origin_commit_sequence)
      SELECT gen_random_uuid(), x.s, x.s, 'TestAggregate', 1, '{TYPE}', NULL, nextval('wh_commit_seq'),
             NOW() - INTERVAL '2 hours', '{origin}'::uuid, CASE WHEN x.g <= {SEQUENCED_EVENTS} THEN x.g END
      FROM (SELECT g, gen_random_uuid() AS s FROM generate_series(1, {SEQUENCED_EVENTS + SEQUENCELESS_ELSEWHERE}) AS g) x
      """);
    await _execAsync(conn, $"""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope,
                                  commit_sequence, created_at, origin_service_id, origin_commit_sequence)
      VALUES (gen_random_uuid(), '{stream}', '{stream}', 'TestAggregate', 1, '{TYPE}', NULL, nextval('wh_commit_seq'),
              NOW() - INTERVAL '2 hours', '{origin}', NULL),
             (gen_random_uuid(), '{stream}', '{stream}', 'TestAggregate', 2, '{TYPE}', NULL, nextval('wh_commit_seq'),
              NOW() - INTERVAL '2 hours', '{origin}', NULL),
             (gen_random_uuid(), '{stream}', '{stream}', 'TestAggregate', 3, '{TYPE}', NULL, nextval('wh_commit_seq'),
              NOW() - INTERVAL '2 hours', '{origin}', 99999)
      """);
    await _execAsync(conn, "ANALYZE wh_event_store");
    return (origin, stream);
  }

  /// <summary>Runs one statement under the generic plan with auto_explain, and returns its plan with actual rows.</summary>
  private static async Task<IReadOnlyList<PlanNode>> _plansOfAsync(NpgsqlConnection conn, Action<NpgsqlCommand> prepare) {
    var notices = new List<string>();
    void Collect(object? _, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
    await _execAsync(conn, """
      SET plan_cache_mode = force_generic_plan;
      LOAD 'auto_explain';
      SET auto_explain.log_min_duration = 0;
      SET auto_explain.log_analyze = on;
      SET auto_explain.log_timing = off;
      SET auto_explain.log_format = 'json';
      SET auto_explain.log_level = 'notice';
      """);
    conn.Notice += Collect;
    await using (var cmd = conn.CreateCommand()) {
      prepare(cmd);
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        // drained: the plan is logged when the statement completes
      }
    }
    conn.Notice -= Collect;
    await _execAsync(conn, "SET auto_explain.log_min_duration = -1");
    var nodes = new List<PlanNode>();
    foreach (var notice in notices.Where(n => n.Contains("\"Plan\"", StringComparison.Ordinal))) {
      using var document = JsonDocument.Parse(notice[notice.IndexOf('{', StringComparison.Ordinal)..]);
      _flatten(document.RootElement.GetProperty("Plan"), nodes);
    }
    return nodes;
  }

  private static void _flatten(JsonElement node, List<PlanNode> nodes) {
    if (node.TryGetProperty("Plans", out var children)) {
      foreach (var child in children.EnumerateArray()) {
        _flatten(child, nodes);
      }
    }
    nodes.Add(new PlanNode(
      node.GetProperty("Node Type").GetString()!,
      node.TryGetProperty("Relation Name", out var relation) ? relation.GetString() : null,
      // Rows read, not rows kept: a filter applied after an index range still paid for every row it removed.
      (long)((node.GetProperty("Actual Rows").GetDouble()
        + (node.TryGetProperty("Rows Removed by Filter", out var removed) ? removed.GetDouble() : 0))
        * node.GetProperty("Actual Loops").GetDouble())));
  }

  private sealed record PlanNode(string Type, string? Relation, long RowsRead);

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }
}
