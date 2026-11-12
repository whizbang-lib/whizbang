// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Two contracts appending to one physical stream; the perspective under test folds only
/// <see cref="HandledEvent"/>. Nested on purpose: the stored <c>event_type</c> is the wire form
/// with <c>+</c> for nested types, and the match must go through the framework helper.
/// </summary>
public static class HistoryProbeContracts {
  public sealed record HandledEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; }
  }

  public sealed record OtherEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; }
  }
}

/// <summary>
/// Integration tests for <c>EFCoreEventStore.HasStreamEventsBeforeAsync</c> — the
/// resurrection-on-wake history probe (perspective row retention). On the row-null branch of a
/// row-TTL Sourced perspective, the generated runner asks: does this stream hold events ordered
/// before the incoming batch? True means the row was reaped and the stream woke — re-fold via the
/// rewind core; false means a genuinely new stream — apply normally. Verified against a real
/// Postgres so the LINQ Guid comparison provably translates to the uuid ordering the store uses.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention</docs>
[Category("Shard3")]
public class EventStoreHistoryProbeSqlTests : EFCoreTestBase {
  private static async Task _seedPointerAsync(NpgsqlConnection conn, Guid eventId, Guid streamId, long version, string eventType = "TestNamespace.ProbeEvent") {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, created_at)
      VALUES (@event, @stream, @stream, 'TestAggregate', @type, 'null'::jsonb, @ver,
              nextval('wh_commit_seq'), 0, NOW() - INTERVAL '61 days')
      """;
    cmd.Parameters.AddWithValue("event", eventId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("type", eventType);
    cmd.Parameters.AddWithValue("ver", version);
    await cmd.ExecuteNonQueryAsync();
  }

  // -------------------------------------------------------------------------------------------
  // Issue #696: the typed probe asks "does this stream have an earlier event of a type this
  // perspective folds", not "any earlier event". On a stream shared by several contracts the
  // untyped question is true on first contact, and every first contact re-folded.
  // -------------------------------------------------------------------------------------------

  [Test]
  public async Task TypedProbe_OlderEventsOfOtherTypesOnly_ReturnsFalseAsync() {
    await using var context = CreateDbContext();
    var conn = await _openAsync(context);
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();
    var unhandled = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-61));
    var waking = Guid.CreateVersion7();
    await _seedPointerAsync(conn, unhandled, streamId, 1, TypeNameFormatter.Format(typeof(HistoryProbeContracts.OtherEvent)));

    await Assert.That(await store.HasStreamEventsBeforeAsync(streamId, waking, [typeof(HistoryProbeContracts.HandledEvent)])).IsFalse()
      .Because("history this perspective never folded is not a reaped row; the stream is a first contact for it");
  }

  [Test]
  public async Task TypedProbe_OlderEventOfHandledType_ReturnsTrueAsync() {
    await using var context = CreateDbContext();
    var conn = await _openAsync(context);
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();
    var unhandled = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-62));
    var handled = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-61));
    var waking = Guid.CreateVersion7();
    await _seedPointerAsync(conn, unhandled, streamId, 1, TypeNameFormatter.Format(typeof(HistoryProbeContracts.OtherEvent)));
    await _seedPointerAsync(conn, handled, streamId, 2, TypeNameFormatter.Format(typeof(HistoryProbeContracts.HandledEvent)));

    await Assert.That(await store.HasStreamEventsBeforeAsync(streamId, waking, [typeof(HistoryProbeContracts.HandledEvent)])).IsTrue()
      .Because("a reaped row still has prior events of the handled types, so real resurrection keeps working");
  }

  [Test]
  public async Task TypedProbe_MatchesStoredEventTypeFormat_NestedAndAssemblyQualifiedAsync() {
    // A producer may have written the fully decorated assembly-qualified name (Version, Culture,
    // PublicKeyToken). The match goes through EventTypeMatchingHelper, which normalizes it; a
    // hand-built equality on the bare wire form would miss this row.
    await using var context = CreateDbContext();
    var conn = await _openAsync(context);
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();
    var handled = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-61));
    var waking = Guid.CreateVersion7();
    var decorated = typeof(HistoryProbeContracts.HandledEvent).AssemblyQualifiedName!;
    await Assert.That(decorated).Contains("+").And.Contains("Version=");
    await _seedPointerAsync(conn, handled, streamId, 1, decorated);

    await Assert.That(await store.HasStreamEventsBeforeAsync(streamId, waking, [typeof(HistoryProbeContracts.HandledEvent)])).IsTrue();
  }

  [Test]
  public async Task TypedProbe_NoCandidateTypes_ReturnsFalseAsync() {
    await using var context = CreateDbContext();
    var conn = await _openAsync(context);
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();
    await _seedPointerAsync(conn, Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-61)), streamId, 1);

    await Assert.That(await store.HasStreamEventsBeforeAsync(streamId, Guid.CreateVersion7(), [])).IsFalse()
      .Because("a perspective that folds nothing can have folded nothing");
  }

  [Test]
  public async Task StreamsWithEventsBeforeAsync_AnswersEveryStreamInOneQueryAsync() {
    // The batched probe of a set-based batch (#2246): one statement for every stream whose row it found missing, each
    // answered exactly as the per-stream typed probe answers it, archived history included.
    await using var context = CreateDbContext();
    var conn = await _openAsync(context);
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var handledHistory = Guid.NewGuid();
    var otherHistory = Guid.NewGuid();
    var laterOnly = Guid.NewGuid();
    var noHistory = Guid.NewGuid();
    var archived = Guid.NewGuid();
    var waking = Guid.CreateVersion7();
    await _seedPointerAsync(conn, Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-61)), handledHistory, 1,
      TypeNameFormatter.Format(typeof(HistoryProbeContracts.HandledEvent)));
    await _seedPointerAsync(conn, Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-61)), otherHistory, 1,
      TypeNameFormatter.Format(typeof(HistoryProbeContracts.OtherEvent)));
    await _seedPointerAsync(conn, Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(1)), laterOnly, 1,
      TypeNameFormatter.Format(typeof(HistoryProbeContracts.HandledEvent)));
    await using (var ledger = conn.CreateCommand()) {
      ledger.CommandText = """
        INSERT INTO wh_stream_lifecycle (stream_id, stream_type, first_appended_at, last_appended_bucket, last_version, state, archive_locator)
        VALUES (@s, 'TestAggregate', NOW(), NOW(), 2, 'Archived', 'archive-a')
        """;
      ledger.Parameters.AddWithValue("s", archived);
      await ledger.ExecuteNonQueryAsync();
    }
    (Guid, Guid)[] probes = [(handledHistory, waking), (otherHistory, waking), (laterOnly, waking), (noHistory, waking), (archived, waking)];
    Type[] types = [typeof(HistoryProbeContracts.HandledEvent)];
    var perStream = new HashSet<Guid>();
    foreach (var (streamId, before) in probes) {
      if (await store.HasStreamEventsBeforeAsync(streamId, before, types)) {
        perStream.Add(streamId);
      }
    }

    using var statements = new Perspectives.NpgsqlStatementLog(ConnectionString);
    var batched = await store.StreamsWithEventsBeforeAsync(probes, types);

    await Assert.That(batched).IsEquivalentTo(perStream).Because("the batched probe answers each stream as the per-stream probe does");
    await Assert.That(batched).IsEquivalentTo(new[] { handledHistory, archived });
    await Assert.That(statements.Matching("wh_event_store")).Count().IsEqualTo(1)
      .Because("every stream is answered by one statement");
    await Assert.That(await store.StreamsWithEventsBeforeAsync([], types)).IsEmpty();
    await Assert.That(await store.StreamsWithEventsBeforeAsync(probes, [])).IsEmpty()
      .Because("a perspective that folds nothing can have folded nothing");
    await using (var transaction = await context.Database.BeginTransactionAsync()) {
      await Assert.That(await store.StreamsWithEventsBeforeAsync(probes, types)).IsEquivalentTo(perStream)
        .Because("inside the context's transaction the probe runs on it, and answers the same");
      await transaction.RollbackAsync();
    }
  }

  [Test]
  public async Task GetCommitSequencesAsync_AnswersEveryEventInOneQueryAsync() {
    // The batched read of a set-based batch (#2246): the commit sequences its pairs record, one statement for all of them.
    await using var context = CreateDbContext();
    var conn = await _openAsync(context);
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var stream = Guid.NewGuid();
    var first = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-2));
    var second = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-1));
    await _seedPointerAsync(conn, first, stream, 1);
    await _seedPointerAsync(conn, second, stream, 2);
    var missing = Guid.CreateVersion7();

    using var statements = new Perspectives.NpgsqlStatementLog(ConnectionString);
    var sequences = await store.GetCommitSequencesAsync([first, second, missing]);

    await Assert.That(sequences[first]).IsEqualTo(await store.GetCommitSequenceAsync(first));
    await Assert.That(sequences[second]).IsEqualTo(await store.GetCommitSequenceAsync(second));
    await Assert.That(sequences.ContainsKey(missing)).IsFalse().Because("an event that is not stored is left out");
    await Assert.That(statements.Matching("wh_event_store").Count(s => s.Contains("ANY", StringComparison.Ordinal))).IsEqualTo(1);
    await Assert.That(await store.GetCommitSequencesAsync([])).IsEmpty();
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext context) {
    var conn = (NpgsqlConnection)context.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  [Test]
  public async Task Probe_StreamWithOlderEvents_ReturnsTrueAsync() {
    await using var context = CreateDbContext();
    var conn = (NpgsqlConnection)context.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();
    var oldEvent = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-61));
    var wakingEvent = Guid.CreateVersion7();
    await _seedPointerAsync(conn, oldEvent, streamId, 1);

    await Assert.That(await store.HasStreamEventsBeforeAsync(streamId, wakingEvent)).IsTrue()
      .Because("a reaped stream's 61-day-old history orders before the waking event — resurrection required.");
  }

  [Test]
  public async Task Probe_NewStream_ReturnsFalseAsync() {
    await using var context = CreateDbContext();
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    await Assert.That(await store.HasStreamEventsBeforeAsync(Guid.NewGuid(), Guid.CreateVersion7())).IsFalse()
      .Because("a stream with no stored events is genuinely new — no resurrection.");
  }

  [Test]
  public async Task Probe_OnlyLaterEvents_ReturnsFalseAsync() {
    // The batch's own (or later) events never count as history — only strictly-earlier ids do.
    await using var context = CreateDbContext();
    var conn = (NpgsqlConnection)context.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();
    var probeAnchor = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-1));
    var laterEvent = Guid.CreateVersion7();
    await _seedPointerAsync(conn, laterEvent, streamId, 1);

    await Assert.That(await store.HasStreamEventsBeforeAsync(streamId, probeAnchor)).IsFalse()
      .Because("only events ordered strictly before the anchor are pre-batch history.");
  }
}
