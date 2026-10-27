// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Registry;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The operator repair for composite children a receiver filed under the composite's stream (#1798), against a real
/// database: <see cref="PostgresFanoutChildRefiler"/> finds each stored fan-out child whose payload names another
/// stream, <c>wh_refile_events</c> (migration 247) moves it there with its bookkeeping, and the composite streams it
/// empties are purged with their ghost rows.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/247_FanoutChildrenAreFiledUnderTheirOwnStreams.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresFanoutChildRefiler.cs</code-under-test>
/// <docs>operations/infrastructure/purging-streams#refiling-composite-children</docs>
[Category("Integration")]
[Category("Shard4")]
public class FanoutChildRefileSqlTests : EFCoreTestBase {
  private const string BALANCE_TABLE = "wh_per_rebuild_balance";

  [Test]
  public async Task Refile_MovesAChildToTheStreamItsPayloadNames_AfterThatStreamsTailAsync() {
    var own = Guid.NewGuid();
    var composite = Guid.NewGuid();
    await _appendAsync(own, new RebuildBalanceSetEvent { StreamId = own, Balance = 1m });
    await _appendAsync(own, new RebuildBalanceSetEvent { StreamId = own, Balance = 2m });
    var child = await _appendChildAsync(composite, new RebuildLabelSetEvent { StreamId = own, Label = "x" });
    var tail = await _tailAsync(own);

    var report = await _refiler().RefileAsync(_request());

    await Assert.That(report.Moves).Count().IsEqualTo(1);
    await Assert.That(report.Moves[0]).IsEqualTo(new RefiledEvent(child, composite, own, tail + 1));
    await Assert.That(report.StreamsToRebuild).IsEquivalentTo([own]);
    await using var conn = await _openAsync();
    await Assert.That(await _scalarAsync<Guid>(conn, "SELECT stream_id FROM wh_event_store WHERE event_id = $1", child)).IsEqualTo(own);
    await Assert.That(await _scalarAsync<Guid>(conn, "SELECT aggregate_id FROM wh_event_store WHERE event_id = $1", child)).IsEqualTo(own);
    await Assert.That(await _scalarAsync<int>(conn, "SELECT version FROM wh_event_store WHERE event_id = $1", child))
      .IsEqualTo(tail + 1).Because("the child takes the next version of its stream, after everything already there");
    await Assert.That(await _versionsAsync(conn, own)).IsEquivalentTo([tail - 1, tail, tail + 1]);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_event_store WHERE stream_id = $1", composite)).IsEqualTo(0L);
  }

  [Test]
  public async Task Refile_PurgesTheGhostRowOfTheCompositeStreamItEmpties_AndNoOtherRowAsync() {
    var own = Guid.NewGuid();
    var composite = Guid.NewGuid();
    await _appendAsync(own, new RebuildBalanceSetEvent { StreamId = own, Balance = 1m });
    await _appendChildAsync(composite, new RebuildLabelSetEvent { StreamId = own, Label = "x" });
    await _registerBalanceTableAsync();
    await _seedRowAsync(own);
    await _seedRowAsync(composite);

    var report = await _refiler().RefileAsync(_request());

    await Assert.That(report.EmptiedStreams).IsEquivalentTo([composite]);
    await Assert.That(report.PurgedRows[BALANCE_TABLE]).IsEqualTo(1L);
    await using var ctx = CreateDbContext();
    var rows = await ctx.Set<PerspectiveRow<RebuildBalanceModel>>().AsNoTracking().Select(r => r.Id).ToListAsync();
    await Assert.That(rows).Contains(own).Because("the row the child is about stays, for the rebuild to repair");
    await Assert.That(rows).DoesNotContain(composite).Because("a row keyed by an emptied composite stream is a ghost");
  }

  [Test]
  public async Task Refile_FromAStreamThatKeepsEventsOfItsOwn_RenumbersItContiguouslyAndPurgesNothingAsync() {
    var own = Guid.NewGuid();
    var shared = Guid.NewGuid();
    await _appendAsync(own, new RebuildBalanceSetEvent { StreamId = own, Balance = 1m });
    var first = await _appendChildAsync(shared, new RebuildBalanceSetEvent { StreamId = shared, Balance = 7m });
    await _appendChildAsync(shared, new RebuildLabelSetEvent { StreamId = own, Label = "x" });
    var third = await _appendChildAsync(shared, new RebuildLabelSetEvent { StreamId = shared, Label = "y" });

    var report = await _refiler().RefileAsync(_request());

    await Assert.That(report.Moves).Count().IsEqualTo(1);
    await Assert.That(report.EmptiedStreams).IsEmpty();
    await using var conn = await _openAsync();
    await Assert.That(await _scalarAsync<int>(conn, "SELECT version FROM wh_event_store WHERE event_id = $1", first)).IsEqualTo(1);
    await Assert.That(await _scalarAsync<int>(conn, "SELECT version FROM wh_event_store WHERE event_id = $1", third))
      .IsEqualTo(2).Because("the stream left behind keeps contiguous versions in its old order");
  }

  [Test]
  public async Task Refile_MovesTheIntegrityDigestWithTheEventAsync() {
    var own = Guid.NewGuid();
    var composite = Guid.NewGuid();
    await _appendAsync(own, new RebuildBalanceSetEvent { StreamId = own, Balance = 1m });
    await _appendChildAsync(composite, new RebuildLabelSetEvent { StreamId = own, Label = "x" });
    await using var conn = await _openAsync();
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_stream_digests WHERE stream_id = $1", composite))
      .IsEqualTo(1L).Because("setup: the emit chain folded the child into the composite stream's bucket");

    await _refiler().RefileAsync(_request());

    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_stream_digests WHERE stream_id = $1", composite))
      .IsEqualTo(0L).Because("the emptied stream's bucket goes with the purge, and its hash had already left it");
    await Assert.That(await _scalarAsync<long>(conn, """
      SELECT count(*) FROM (
        SELECT event_type, bit_xor(hashtextextended(event_id::text, 0)) AS lo, bit_xor(hashtextextended(event_id::text, 1)) AS hi, count(*)::int AS n
        FROM wh_event_store WHERE stream_id = $1 GROUP BY event_type
      ) e
      JOIN wh_stream_digests d ON d.stream_id = $1 AND d.event_type = e.event_type
      WHERE d.digest_lo = e.lo AND d.digest_hi = e.hi AND d.event_count = e.n
      """, own)).IsEqualTo(2L).Because("each of the stream's buckets equals a recompute over its events, the moved one included");
  }

  [Test]
  public async Task Refile_CarriesTheChildsInboxAndLedgerRows_AndDropsWorkAndFoldsThatNoLongerHoldAsync() {
    var own = Guid.NewGuid();
    var composite = Guid.NewGuid();
    var keptOn = Guid.NewGuid();
    var ownEvent = await _appendAsync(own, new RebuildBalanceSetEvent { StreamId = own, Balance = 1m });
    var keptOnEvent = await _appendChildAsync(keptOn, new RebuildBalanceSetEvent { StreamId = keptOn, Balance = 1m });
    var child = await _appendChildAsync(composite, new RebuildLabelSetEvent { StreamId = own, Label = "x" });
    var stays = await _appendChildAsync(composite, new RebuildLabelSetEvent { StreamId = composite, Label = "stays" });
    await using var conn = await _openAsync();
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_applied (event_id, perspective_name, stream_id, applied_at) VALUES ($1, 'P', $2, NOW())", child, composite);
    await _executeAsync(conn, """
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
      VALUES (gen_random_uuid(), $2, 'P', $1, 1, 1, 0, NOW())
      """, child, composite);
    await _executeAsync(conn, "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id) VALUES ($1, 'P', $2)", composite, child);
    await _executeAsync(conn, "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id) VALUES ($1, 'Q', $2)", composite, stays);
    foreach (var (stream, at) in new[] { (composite, stays), (own, ownEvent), (keptOn, keptOnEvent) }) {
      await _executeAsync(conn, """
        INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number)
        VALUES ($1, 'P', $2, '{}'::jsonb, 1)
        """, stream, at);
    }

    await _refiler().RefileAsync(_request());

    await Assert.That(await _scalarAsync<Guid>(conn, "SELECT stream_id FROM wh_perspective_applied WHERE event_id = $1", child)).IsEqualTo(own);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_perspective_events WHERE event_id = $1", child))
      .IsEqualTo(0L).Because("pending work would apply the child to the composite's row; the rebuild applies it to its own");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = $1 AND perspective_name = 'P'", composite))
      .IsEqualTo(0L).Because("a cursor naming a moved event names a position its stream no longer has");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = $1 AND perspective_name = 'Q'", composite))
      .IsEqualTo(1L);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_perspective_snapshots WHERE stream_id = ANY($1)", new[] { own, composite }))
      .IsEqualTo(0L).Because("each snapshot folds a history the move changed");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_perspective_snapshots WHERE stream_id = $1", keptOn))
      .IsEqualTo(1L).Because("a stream the move did not touch keeps its snapshots");
  }

  [Test]
  public async Task Refile_MovesTheChildsInboxRows_SoThePurgeOfTheCompositeStreamKeepsThemAsync() {
    var own = Guid.NewGuid();
    var composite = Guid.NewGuid();
    await _appendAsync(own, new RebuildBalanceSetEvent { StreamId = own, Balance = 1m });
    var child = await _appendChildAsync(composite, new RebuildLabelSetEvent { StreamId = own, Label = "x" });
    await using var conn = await _openAsync();
    await Assert.That(await _scalarAsync<Guid>(conn, "SELECT stream_id FROM wh_inbox WHERE message_id = $1", child))
      .IsEqualTo(composite).Because("setup: the receiver kept the child's inbox row under the composite's stream");

    await _refiler().RefileAsync(_request());

    await Assert.That(await _scalarAsync<Guid>(conn, "SELECT stream_id FROM wh_inbox WHERE message_id = $1", child)).IsEqualTo(own);
    await Assert.That(await _scalarAsync<Guid>(conn, "SELECT stream_id FROM wh_inbox_state WHERE message_id = $1", child)).IsEqualTo(own);
  }

  [Test]
  public async Task Refile_IsSafeToRepeat_ASecondRunMovesNothingAsync() {
    var own = Guid.NewGuid();
    var composite = Guid.NewGuid();
    await _appendAsync(own, new RebuildBalanceSetEvent { StreamId = own, Balance = 1m });
    await _appendChildAsync(composite, new RebuildLabelSetEvent { StreamId = own, Label = "x" });
    await _refiler().RefileAsync(_request());

    var again = await _refiler().RefileAsync(_request());

    await Assert.That(again.Moves).IsEmpty();
    await Assert.That(again.EmptiedStreams).IsEmpty();
    await Assert.That(again.Scanned).IsEqualTo(1).Because("the child is still a fan-out child, now on its own stream");
  }

  [Test]
  public async Task Refile_DryRun_ReportsExactlyWhatTheRepairWouldDo_AndChangesNothingAsync() {
    var own = Guid.NewGuid();
    var composite = Guid.NewGuid();
    await _appendAsync(own, new RebuildBalanceSetEvent { StreamId = own, Balance = 1m });
    var child = await _appendChildAsync(composite, new RebuildLabelSetEvent { StreamId = own, Label = "x" });
    var tail = await _tailAsync(own);

    var report = await _refiler().RefileAsync(_request() with { DryRun = true });

    await Assert.That(report.DryRun).IsTrue();
    await Assert.That(report.Moves).IsEquivalentTo([new RefiledEvent(child, composite, own, tail + 1)]);
    await Assert.That(report.EmptiedStreams).IsEquivalentTo([composite]);
    await Assert.That(report.Format()).Contains("Dry run: would move 1 composite child(ren)");
    await using var conn = await _openAsync();
    await Assert.That(await _scalarAsync<Guid>(conn, "SELECT stream_id FROM wh_event_store WHERE event_id = $1", child)).IsEqualTo(composite);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_stream_purge_markers")).IsEqualTo(0L);
  }

  [Test]
  public async Task Refile_InBatches_NeverSplitsACompositeStreamAsync() {
    var own = Guid.NewGuid();
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    await _appendAsync(own, new RebuildBalanceSetEvent { StreamId = own, Balance = 1m });
    await _appendChildAsync(first, new RebuildLabelSetEvent { StreamId = own, Label = "a" });
    await _appendChildAsync(first, new RebuildLabelSetEvent { StreamId = own, Label = "b" });
    await _appendChildAsync(second, new RebuildLabelSetEvent { StreamId = own, Label = "c" });
    var tail = await _tailAsync(own);
    var logger = new ListLogger();

    var report = await _refiler(logger).RefileAsync(_request() with { BatchSize = 1 });

    await Assert.That(report.Moves).Count().IsEqualTo(3);
    await Assert.That(report.EmptiedStreams).IsEquivalentTo([first, second])
      .Because("a stream larger than a batch still moves whole, so the batch that empties it also purges it");
    await Assert.That(logger.Messages.Count(m => m.Contains("batch", StringComparison.Ordinal))).IsEqualTo(2);
    await using var conn = await _openAsync();
    await Assert.That(await _versionsAsync(conn, own)).IsEquivalentTo([tail, tail + 1, tail + 2, tail + 3])
      .Because("the three children follow the stream's tail with no gap");
  }

  [Test]
  public async Task Refile_LeavesAChildItCannotRead_AndCountsItAsync() {
    var composite = Guid.NewGuid();
    var unreadable = Guid.NewGuid();
    var unkeyed = await _appendChildAsync(composite, new RebuildLabelSetEvent { StreamId = composite, Label = "x" });
    await using var conn = await _openAsync();
    await _executeAsync(conn, """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at, flags)
      VALUES ($1, $2, $2, 'Elsewhere', 'Elsewhere.UnknownHere, Elsewhere', 2, NOW(), 4)
      """, unreadable, composite);
    await _executeAsync(conn, "INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES ($1, '{}'::jsonb, '{}'::jsonb)", unreadable);

    var report = await _refiler().RefileAsync(_request());

    await Assert.That(report.Scanned).IsEqualTo(2);
    await Assert.That(report.Undecidable).IsEqualTo(1).Because("a type this service cannot read names no stream it can trust");
    await Assert.That(report.Moves).IsEmpty();
    await Assert.That(await _scalarAsync<Guid>(conn, "SELECT stream_id FROM wh_event_store WHERE event_id = $1", unkeyed)).IsEqualTo(composite);
  }

  [Test]
  public async Task Refile_LeavesAChildWithoutABodyOrWithAnUnreadableOne_AndCountsBothAsync() {
    var composite = Guid.NewGuid();
    var bodiless = Guid.NewGuid();
    var garbled = Guid.NewGuid();
    var labelType = await _storedTypeOfAsync(await _appendChildAsync(composite, new RebuildLabelSetEvent { StreamId = composite, Label = "x" }));
    await using var conn = await _openAsync();
    foreach (var (id, version) in new[] { (bodiless, 2), (garbled, 3) }) {
      await _executeAsync(conn, """
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at, flags)
        VALUES ($1, $2, $2, 'T', $3, $4, NOW(), 4)
        """, id, composite, labelType, version);
    }
    await _executeAsync(conn, "INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES ($1, '{\"Label\": [1]}'::jsonb, '{}'::jsonb)", garbled);

    var report = await _refiler().RefileAsync(_request());

    await Assert.That(report.Scanned).IsEqualTo(3);
    await Assert.That(report.Undecidable).IsEqualTo(2)
      .Because("a child whose body is missing or does not read as its type names no stream that can be trusted");
    await Assert.That(report.Moves).IsEmpty();
  }

  [Test]
  public async Task Refile_LeavesAChildWhosePayloadNamesNoStream_WithoutCountingItUnreadableAsync() {
    var composite = Guid.NewGuid();
    await _appendChildAsync(composite, new RebuildLabelSetEvent { StreamId = Guid.Empty, Label = "x" });

    var report = await _refiler().RefileAsync(_request());

    await Assert.That(report.Scanned).IsEqualTo(1);
    await Assert.That(report.Undecidable).IsEqualTo(0).Because("the payload was read; it simply names no stream, so it belongs where it is");
    await Assert.That(report.Moves).IsEmpty();
  }

  /// <summary>
  /// An event the integrity re-delivery repaired before #1798 was filed under the bundle envelope's own id, a random
  /// stream, because the bundle named no stream. Its payload names the stream its origin stored it on, so the repair
  /// moves it there like any misfiled composite child.
  /// </summary>
  [Test]
  public async Task Refile_ARedeliveredEventFiledUnderItsBundlesId_MovesToTheStreamItsPayloadNamesAsync() {
    var own = Guid.NewGuid();
    var bundle = Guid.NewGuid();
    await _appendAsync(own, new RebuildBalanceSetEvent { StreamId = own, Balance = 1m });
    var repaired = await _appendChildAsync(bundle, new RebuildLabelSetEvent { StreamId = own, Label = "repaired" }, causedBy: bundle);
    var tail = await _tailAsync(own);

    var report = await _refiler().RefileAsync(_request());

    await Assert.That(report.Moves.Single()).IsEqualTo(new RefiledEvent(repaired, bundle, own, tail + 1));
    await Assert.That(report.EmptiedStreams).IsEquivalentTo([bundle]);
  }

  /// <summary>
  /// A re-delivered event whose payload names no stream was stored by its origin on its own id, as every message whose
  /// hop names no stream is, and live delivery stores it there too. Filed under the bundle's id instead (its lineage
  /// hop's causation), it is moved onto its own id.
  /// </summary>
  [Test]
  public async Task Refile_ARedeliveredEventNamingNoStream_MovesOntoItsOwnIdAsync() {
    var bundle = Guid.NewGuid();
    var repaired = await _appendChildAsync(bundle, new RebuildLabelSetEvent { StreamId = Guid.Empty, Label = "repaired" }, causedBy: bundle);

    var report = await _refiler().RefileAsync(_request());

    await Assert.That(report.Moves.Single()).IsEqualTo(new RefiledEvent(repaired, bundle, repaired, 1));
    await using var conn = await _openAsync();
    await Assert.That(await _scalarAsync<Guid>(conn, "SELECT stream_id FROM wh_event_store WHERE event_id = $1", repaired)).IsEqualTo(repaired);
  }

  /// <summary>
  /// A fan-out child naming no stream that sits on its composite's stream, not on its composite's envelope id, was filed
  /// where its composite named, and stays.
  /// </summary>
  [Test]
  public async Task Refile_AChildNamingNoStreamOnItsCompositesStream_StaysAsync() {
    var compositeStream = Guid.NewGuid();
    await _appendChildAsync(compositeStream, new RebuildLabelSetEvent { StreamId = Guid.Empty, Label = "x" }, causedBy: Guid.NewGuid());

    var report = await _refiler().RefileAsync(_request());

    await Assert.That(report.Moves).IsEmpty();
  }

  [Test]
  public async Task Refile_WithASchemaOfItsOwn_QualifiesEveryTableAsync() {
    var own = Guid.NewGuid();
    var composite = Guid.NewGuid();
    await _appendAsync(own, new RebuildBalanceSetEvent { StreamId = own, Balance = 1m });
    var child = await _appendChildAsync(composite, new RebuildLabelSetEvent { StreamId = own, Label = "x" });
    string schema;
    await using (var conn = await _openAsync()) {
      schema = await _scalarAsync<string>(conn, "SELECT n.nspname::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.oid = 'wh_event_store'::regclass");
    }

    var report = await new PostgresFanoutChildRefiler(
        NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, streamIds: null, jsonOptions: null, schema: schema)
      .RefileAsync(_request());

    await Assert.That(report.Moves.Single().EventId).IsEqualTo(child);
  }

  [Test]
  public async Task Refile_CallsTheFunctionWithTheEmptyStream_IsRefusedAsync() {
    await using var conn = await _openAsync();
    await using var call = new NpgsqlCommand("SELECT * FROM wh_refile_events($1, $2)", conn);
    call.Parameters.Add(new NpgsqlParameter { Value = new[] { Guid.NewGuid() }, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    call.Parameters.Add(new NpgsqlParameter { Value = new[] { Guid.Empty }, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });

    var refused = await Assert.ThrowsAsync<PostgresException>(async () => await call.ExecuteNonQueryAsync());

    await Assert.That(refused!.MessageText).Contains("names no stream");
  }

  [Test]
  public async Task Refile_CallsTheFunctionWithUnpairedArrays_IsRefusedAsync() {
    await using var conn = await _openAsync();
    await using var call = new NpgsqlCommand("SELECT * FROM wh_refile_events($1, $2)", conn);
    call.Parameters.Add(new NpgsqlParameter { Value = new[] { Guid.NewGuid() }, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    call.Parameters.Add(new NpgsqlParameter { Value = Array.Empty<Guid>(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });

    var refused = await Assert.ThrowsAsync<PostgresException>(async () => await call.ExecuteNonQueryAsync());

    await Assert.That(refused!.MessageText).Contains("target streams");
  }

  [Test]
  public async Task Request_WithoutWhoOrWhyOrAPositiveBatch_IsRefusedAsync() {
    await Assert.That(() => (_request() with { RequestedBy = " " }).Validate()).Throws<ArgumentException>();
    await Assert.That(() => (_request() with { Reason = "" }).Validate()).Throws<ArgumentException>();
    await Assert.That(() => (_request() with { BatchSize = 0 }).Validate()).Throws<ArgumentException>();
    await Assert.ThrowsAsync<ArgumentNullException>(async () => await _refiler().RefileAsync(null!));
  }

  [Test]
  public async Task Report_Format_ListsThePurgedTablesThatLostRowsAsync() {
    var report = new FanoutChildRefileReport(Guid.Empty, false, 3, 1,
      [new RefiledEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 4)], [Guid.NewGuid()],
      new Dictionary<string, long>(StringComparer.Ordinal) { [BALANCE_TABLE] = 2, ["wh_outbox"] = 0 });

    var text = report.Format();

    await Assert.That(text).StartsWith("Moved 1 composite child(ren) of 3 examined onto 1 stream(s); 1 composite stream(s) emptied; 1 unreadable");
    await Assert.That(text).Contains(BALANCE_TABLE);
    await Assert.That(text).DoesNotContain("wh_outbox").Because("a table that lost nothing is not listed");
  }

  private static FanoutChildRefileRequest _request() => new() { RequestedBy = "operator-1", Reason = "composite children filed under the composite" };

  private PostgresFanoutChildRefiler _refiler(ListLogger? logger = null) =>
    new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, StreamIdExtractorRegistry.GetComposite(), logger: logger);

  /// <summary>Appends an event to its stream as a local write does, and stamps its commit sequence.</summary>
  private async Task<Guid> _appendAsync<TEvent>(Guid streamId, TEvent payload) where TEvent : IEvent {
    var id = MessageId.New();
    await using (var ctx = CreateDbContext()) {
      await new EFCoreEventStore<WorkCoordinationDbContext>(ctx).AppendAsync(streamId, new MessageEnvelope<TEvent> {
        MessageId = id,
        Payload = payload,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = [],
      });
    }
    await using var conn = await _openAsync();
    await _executeAsync(conn,
      "UPDATE wh_event_store SET commit_sequence = (SELECT COALESCE(MAX(commit_sequence), 0) + 1 FROM wh_event_store) WHERE event_id = $1", id.Value);
    return id.Value;
  }

  /// <summary>
  /// Stores an event as a receiver stored a composite's child before the fix: an inbox row under
  /// <paramref name="storedOn"/> flagged as a fan-out child, copied into the event store by the inbox emit chain.
  /// </summary>
  private async Task<Guid> _appendChildAsync<TEvent>(Guid storedOn, TEvent payload, Guid? causedBy = null) where TEvent : IEvent {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var id = MessageId.New();
    // The lineage hop a fan-out gives every child: its causation is the composite envelope's id.
    List<MessageHop> hops = causedBy is { } composite
      ? [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown, CausationId = new MessageId(composite) }]
      : [];
    var serialized = new EnvelopeSerializer(options).SerializeEnvelope(new MessageEnvelope<TEvent> {
      MessageId = id,
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [],
    });
    await using (var ctx = CreateDbContext()) {
      await new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, options).StoreInboxMessagesAsync([new InboxMessage {
        MessageId = id.Value,
        HandlerName = typeof(TEvent).Name + "Handler",
        Envelope = serialized.JsonEnvelope,
        EnvelopeType = serialized.EnvelopeType,
        MessageType = serialized.MessageType,
        StreamId = storedOn,
        IsEvent = true,
        Flags = EventFlags.NoRebroadcast,
        Metadata = new EnvelopeMetadata { MessageId = id, Hops = hops },
      }], partitionCount: 100);
    }
    await using var conn = await _openAsync();
    var instanceId = Guid.NewGuid();
    await _executeAsync(conn,
      "UPDATE wh_inbox_state SET instance_id = $1, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = $2", instanceId, id.Value);
    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox($1, NOW() + INTERVAL '5 minutes', NOW(), 4)", instanceId);
    await _executeAsync(conn,
      "UPDATE wh_event_store SET commit_sequence = (SELECT COALESCE(MAX(commit_sequence), 0) + 1 FROM wh_event_store) WHERE event_id = $1", id.Value);
    await Assert.That(await _scalarAsync<int>(conn, "SELECT flags & 4 FROM wh_event_store WHERE event_id = $1", id.Value))
      .IsEqualTo(4).Because("setup: the child is stored as a fan-out child");
    return id.Value;
  }

  private async Task<int> _tailAsync(Guid stream) {
    await using var conn = await _openAsync();
    return await _scalarAsync<int>(conn, "SELECT MAX(version) FROM wh_event_store WHERE stream_id = $1", stream);
  }

  private async Task<string> _storedTypeOfAsync(Guid eventId) {
    await using var conn = await _openAsync();
    return await _scalarAsync<string>(conn, "SELECT event_type FROM wh_event_store WHERE event_id = $1", eventId);
  }

  private async Task _registerBalanceTableAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, """
      INSERT INTO wh_perspective_registry (id, clr_type_name, table_name, schema_json, schema_hash, service_name, created_at, updated_at)
      VALUES (gen_random_uuid(), $1, $2, '{}'::jsonb, 'h', 'service-b', NOW(), NOW())
      ON CONFLICT DO NOTHING
      """, typeof(RebuildBalancePerspective).FullName!, BALANCE_TABLE);
  }

  private async Task _seedRowAsync(Guid id) {
    await using var ctx = CreateDbContext();
    var now = DateTime.UtcNow;
    ctx.Set<PerspectiveRow<RebuildBalanceModel>>().Add(new PerspectiveRow<RebuildBalanceModel> {
      Id = id,
      Data = new RebuildBalanceModel { Id = id, Balance = 1m },
      Metadata = new PerspectiveMetadata { EventId = Guid.NewGuid().ToString(), EventType = "T", Timestamp = now },
      Scope = new PerspectiveScope(),
      CreatedAt = now,
      UpdatedAt = now,
      Version = 1,
    });
    _ = await ctx.SaveChangesAsync();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task<int[]> _versionsAsync(NpgsqlConnection conn, Guid stream) {
    await using var command = new NpgsqlCommand("SELECT version FROM wh_event_store WHERE stream_id = $1 ORDER BY version", conn);
    command.Parameters.AddWithValue(stream);
    var versions = new List<int>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      versions.Add(reader.GetInt32(0));
    }
    return [.. versions];
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params object[] args) {
    await using var batch = new NpgsqlCommand(sql, conn);
    foreach (var arg in args) {
      batch.Parameters.AddWithValue(arg);
    }
    _ = await batch.ExecuteNonQueryAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var command = new NpgsqlCommand(sql, conn);
    foreach (var arg in args) {
      command.Parameters.AddWithValue(arg);
    }
    return (T)(await command.ExecuteScalarAsync())!;
  }

  /// <summary>Records what the refiler logs at Information and above.</summary>
  private sealed class ListLogger : ILogger<PostgresFanoutChildRefiler> {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Messages.Add(formatter(state, exception));
  }
}
