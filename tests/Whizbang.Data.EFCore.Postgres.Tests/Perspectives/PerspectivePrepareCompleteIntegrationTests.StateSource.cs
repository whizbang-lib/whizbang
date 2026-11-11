// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The state source of a set-based batch (#2246), with real generated runners on PostgreSQL: a batch reads no row of a
/// stream it knows is new, reads every other row of a perspective table with one read, asks the purge markers once for
/// the rows it found missing, and folds exactly what the per-pair read folds. Statements are counted from Npgsql's own
/// tracing, never timed.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes#state-source</docs>
public partial class PerspectivePrepareCompleteIntegrationTests {
  private const int BATCH = 5;

  [Test]
  public async Task NewStreams_ReadNoRowAndNoMarker_AndAreWrittenWithOneInsertAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var markers = new PostgresPerspectivePurgeMarkerStore(dataSource.OpenConnectionAsync);
    var eventStore = new InMemoryEventStore();
    var pairs = new List<(Guid Stream, MessageEnvelope<IEvent> Created)>();
    for (var i = 0; i < BATCH; i++) {
      var stream = Guid.CreateVersion7();
      var created = await _appendAsync(eventStore, stream, new ActionTestCreatedEvent { StreamId = stream, Name = $"new-{i}", Value = i }, 1);
      created.IsFirstInLocalStream = true;
      pairs.Add((stream, created));
    }
    var batch = new BatchedPerspectiveStateSource().BeginBatch(
      [.. pairs.Select(p => new PerspectiveStateRequest(p.Stream, "action_test", p.Created.MessageId.Value, KnownNew: true))]);

    using var statements = new NpgsqlStatementLog(ConnectionString);
    var run = await _prepareBatchAsync(_actionRunner, eventStore, markers, batch,
      [.. pairs.Select(p => (p.Stream, "action_test", (Guid?)null, (IReadOnlyList<MessageEnvelope<IEvent>>)[p.Created]))]);

    await Assert.That(statements.Matching(ACTION_TABLE)).IsEmpty()
      .Because("a stream known to be new has no row to read");
    await Assert.That(statements.Matching("wh_stream_purge_markers")).IsEmpty()
      .Because("a stream known to be new cannot have been purged");
    await Assert.That(run.Prepared.Select(p => p.Write!.Operation)).All().Satisfy(s => s.IsEqualTo(PerspectiveWriteOperation.Insert));

    statements.Clear();
    var completions = await run.WriteAndCompleteAsync();

    await Assert.That(statements.Matching("INSERT INTO", ACTION_TABLE)).Count().IsEqualTo(1)
      .Because("the batch's new rows are written with one statement");
    foreach (var (stream, _) in pairs) {
      await Assert.That(await _readRowAsync(ACTION_TABLE, stream)).IsNotNull();
    }
    await Assert.That(completions.Select(c => c.Status)).All().Satisfy(s => s.IsEqualTo(PerspectiveProcessingStatus.Completed));
  }

  [Test]
  public async Task ExistingStreams_AreReadWithOneVersionReadAndOneModelRead_AndFoldAsThePerPairPathDoesAsync() {
    var eventStore = new InMemoryEventStore();
    var pairs = new List<(Guid Stream, MessageEnvelope<IEvent> Created, MessageEnvelope<IEvent> Updated)>();
    for (var i = 0; i < BATCH; i++) {
      var stream = Guid.CreateVersion7();
      var created = await _appendAsync(eventStore, stream, new ActionTestCreatedEvent { StreamId = stream, Name = $"old-{i}", Value = i }, 1);
      var updated = await _appendAsync(eventStore, stream, new ActionTestUpdatedEvent { StreamId = stream, NewValue = 100 + i }, 2);
      await _perPairAsync(_actionRunner, eventStore, stream, "action_test", null, [created]);
      pairs.Add((stream, created, updated));
    }
    // The per-pair rows to compare with, from the same starting rows.
    var expected = new Dictionary<Guid, string?[]?>();
    foreach (var (stream, created, updated) in pairs) {
      await _perPairAsync(_actionRunner, eventStore, stream, "action_test", created.MessageId.Value, [updated]);
      expected[stream] = await _readRowAsync(ACTION_TABLE, stream);
      await _deleteRowAsync(ACTION_TABLE, stream);
      await _perPairAsync(_actionRunner, eventStore, stream, "action_test", null, [created]);
    }
    var batch = new BatchedPerspectiveStateSource().BeginBatch(
      [.. pairs.Select(p => new PerspectiveStateRequest(p.Stream, "action_test", p.Updated.MessageId.Value, KnownNew: false))]);

    using var statements = new NpgsqlStatementLog(ConnectionString);
    var run = await _prepareBatchAsync(_actionRunner, eventStore, null, batch,
      [.. pairs.Select(p => (p.Stream, "action_test", (Guid?)p.Created.MessageId.Value, (IReadOnlyList<MessageEnvelope<IEvent>>)[p.Updated]))]);

    var reads = statements.Matching(ACTION_TABLE);
    await Assert.That(reads).Count().IsEqualTo(2)
      .Because("the batch reads the table's versions with one statement and its models with one more, whatever its size");
    await Assert.That(reads.Count(r => r.Contains("xmin", StringComparison.Ordinal))).IsEqualTo(1);
    await Assert.That(run.Prepared.Select(p => p.Write!.Operation)).All().Satisfy(s => s.IsEqualTo(PerspectiveWriteOperation.Update));

    await run.WriteAndCompleteAsync();
    foreach (var (stream, _, _) in pairs) {
      await _assertSameRowAsync(expected[stream], await _readRowAsync(ACTION_TABLE, stream));
    }
  }

  [Test]
  public async Task MissingRowsOfStreamsNotKnownNew_AskThePurgeMarkersOnce_AndAPurgedStreamStaysPurgedAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var markers = new PostgresPerspectivePurgeMarkerStore(dataSource.OpenConnectionAsync);
    var eventStore = new InMemoryEventStore();
    var purgedStream = Guid.CreateVersion7();
    await markers.MarkPurgedAsync(purgedStream, "action_test", null);
    var streams = new List<(Guid Stream, MessageEnvelope<IEvent> Event)>();
    foreach (var stream in new[] { purgedStream, Guid.CreateVersion7(), Guid.CreateVersion7() }) {
      // A cursor and no row, so the batch cannot know the stream is new: it reads, and asks the markers.
      var updated = await _appendAsync(eventStore, stream, new ActionTestCreatedEvent { StreamId = stream, Name = "late", Value = 1 }, 2);
      streams.Add((stream, updated));
    }
    var batch = new BatchedPerspectiveStateSource().BeginBatch(
      [.. streams.Select(s => new PerspectiveStateRequest(s.Stream, "action_test", s.Event.MessageId.Value, KnownNew: false))]);

    using var statements = new NpgsqlStatementLog(ConnectionString);
    var run = await _prepareBatchAsync(_actionRunner, eventStore, markers, batch,
      [.. streams.Select(s => (s.Stream, "action_test", (Guid?)Guid.CreateVersion7(), (IReadOnlyList<MessageEnvelope<IEvent>>)[s.Event]))]);

    await Assert.That(statements.Matching("wh_stream_purge_markers")).Count().IsEqualTo(1)
      .Because("every missing row of the batch is asked about with one query");
    await Assert.That(statements.Matching(ACTION_TABLE)).Count().IsEqualTo(1)
      .Because("the version read finds no row, so no model is read");
    await Assert.That(run.Prepared[0].PerPairReason).IsEqualTo(PerspectivePerPairReason.PurgedStream);
    await Assert.That(run.Prepared[1].Write!.Operation).IsEqualTo(PerspectiveWriteOperation.Insert);

    await run.WriteAndCompleteAsync();
    await Assert.That(await _readRowAsync(ACTION_TABLE, purgedStream)).IsNull().Because("purge stays purged");
    await Assert.That(await _readRowAsync(ACTION_TABLE, streams[1].Stream)).IsNotNull();
  }

  [Test]
  public async Task ASplitModel_IsReadByTheBatchAsTheStreamsOwnReadReadsItAsync() {
    var eventStore = new InMemoryEventStore();
    var streams = new List<Guid>();
    for (var i = 0; i < 3; i++) {
      var stream = Guid.CreateVersion7();
      var set = await _appendAsync(eventStore, stream, new InitOnlyFieldsSetEvent { StreamId = stream, Status = $"s{i}", Priority = i, Note = $"n{i}" }, 1);
      await _perPairAsync(_splitRecordRunner, eventStore, stream, "init_only_split", null, [set]);
      streams.Add(stream);
    }
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<InitOnlySplitModel>(context, SPLIT_RECORD_TABLE);

    var batched = await ((IPerspectiveStore<InitOnlySplitModel>)store).ReadForApplyManyAsync([.. streams, Guid.CreateVersion7()]);

    await Assert.That(batched).IsNotNull();
    await Assert.That(batched!.Count).IsEqualTo(3).Because("an absent row is left out");
    foreach (var stream in streams) {
      await using var own = CreateDbContext();
      var ownStore = new EFCorePostgresPerspectiveStore<InitOnlySplitModel>(own, SPLIT_RECORD_TABLE);
      await Assert.That(batched[stream].Model).IsEqualTo(await ownStore.GetByStreamIdAsync(stream))
        .Because("the promoted fields live only in their columns, and the batch hydrates them as the stream's own read does");
      var ownRead = await ownStore.ReadForApplyAsync(stream);
      await Assert.That(batched[stream].ApplyRead.Version).IsEqualTo(ownRead.Version);
      await Assert.That(batched[stream].ApplyRead.Metadata!.EventId).IsEqualTo(ownRead.Metadata!.EventId);
      await Assert.That(batched[stream].ApplyRead.Metadata!.CommitSequence).IsEqualTo(ownRead.Metadata!.CommitSequence);
      await Assert.That(batched[stream].ApplyRead.Scope?.TenantId).IsEqualTo(ownRead.Scope?.TenantId);
    }
    await Assert.That(await ((IPerspectiveStore<InitOnlySplitModel>)store).ReadForApplyManyAsync([])).IsEmpty();
  }

  [Test]
  public async Task ABatchedRead_DeclinesWhileABlueGreenRebuildRedirectsTheTable_AndOnAnotherProviderAsync() {
    await using var context = CreateDbContext();
    IPerspectiveStore<ActionTestModel> store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, ACTION_TABLE);
    using (PerspectiveTableRedirect.Begin(ACTION_TABLE, ACTION_TABLE + "_shadow")) {
      await Assert.That(await store.ReadForApplyManyAsync([Guid.CreateVersion7()])).IsNull()
        .Because("a redirected flow reads its shadow table row by row, as the per-stream read does");
    }

    var options = new DbContextOptionsBuilder<InMemoryRowContext>()
      .UseInMemoryDatabase($"state-source-{Guid.NewGuid():N}").Options;
    await using var inMemory = new InMemoryRowContext(options);
    IPerspectiveStore<ActionTestModel> other = new EFCorePostgresPerspectiveStore<ActionTestModel>(inMemory, ACTION_TABLE);
    await Assert.That(await other.ReadForApplyManyAsync([Guid.CreateVersion7()])).IsNull()
      .Because("a provider without row versions has no batched read");
  }

  [Test]
  public async Task ABatchedRead_OnAnOpenConnection_LeavesItOpen_AndKeepsARowHeldWithAPendingChangeAsync() {
    var eventStore = new InMemoryEventStore();
    var unchanged = Guid.CreateVersion7();
    var pending = Guid.CreateVersion7();
    foreach (var stream in new[] { unchanged, pending }) {
      var set = await _appendAsync(eventStore, stream, new InitOnlyFieldsSetEvent { StreamId = stream, Status = "stored", Priority = 1 }, 1);
      await _perPairAsync(_splitRecordRunner, eventStore, stream, "init_only_split", null, [set]);
    }
    await using var context = CreateDbContext();
    var rows = context.Set<PerspectiveRow<InitOnlySplitModel>>();
    // Held by the context: one unchanged, one with a change it has not saved.
    _ = await rows.SingleAsync(r => r.Id == unchanged);
    var held = await rows.SingleAsync(r => r.Id == pending);
    held.Data = held.Data with { Note = "pending" };
    await context.Database.OpenConnectionAsync();
    IPerspectiveStore<InitOnlySplitModel> store = new EFCorePostgresPerspectiveStore<InitOnlySplitModel>(context, SPLIT_RECORD_TABLE);

    var batched = await store.ReadForApplyManyAsync([unchanged, pending]);

    await Assert.That(context.Database.GetDbConnection().State).IsEqualTo(System.Data.ConnectionState.Open)
      .Because("a connection the read found open is left as it was");
    await Assert.That(batched![unchanged].Model!.Status).IsEqualTo("stored")
      .Because("a row held unchanged is let go and read afresh, with its promoted columns");
    await Assert.That(batched[pending].Model!.Note).IsEqualTo("pending")
      .Because("a row held with a pending change is returned as it is, as the per-stream read returns it");
    await context.Database.CloseConnectionAsync();
  }

  // ---------------------------------------------------------------------------------------------------------------

  /// <summary>A context on a provider other than Npgsql, mapping the one row type the test reads.</summary>
  private sealed class InMemoryRowContext(DbContextOptions<InMemoryRowContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<ActionTestModel>>(entity => {
        entity.ToTable(ACTION_TABLE);
        entity.HasKey(e => e.Id);
        entity.OwnsOne(e => e.Data, d => d.WithOwner());
        entity.OwnsOne(e => e.Metadata, m => m.WithOwner());
        entity.Property(e => e.Scope)
          .HasConversion(
            v => System.Text.Json.JsonSerializer.Serialize(v, System.Text.Json.JsonSerializerOptions.Default),
            v => System.Text.Json.JsonSerializer.Deserialize<PerspectiveScope>(v, System.Text.Json.JsonSerializerOptions.Default)!);
      });
    }
  }

  /// <summary>A batch prepared pair by pair, each on a runner and context of its own as the worker gives them.</summary>
  private sealed class PreparedBatch(
      List<(IPerspectiveRunner Runner, WorkCoordinationDbContext Context)> lanes,
      List<PreparedPerspectiveApply> prepared,
      IAsyncDisposable locks,
      WorkCoordinationDbContext sinkContext) {
    public List<PreparedPerspectiveApply> Prepared { get; } = prepared;

    /// <summary>Writes the batch's prepared rows with one sink call, then completes every pair.</summary>
    public async Task<List<PerspectiveCursorCompletion>> WriteAndCompleteAsync() {
      var writes = Prepared.Where(p => p.Write is not null).ToList();
      var outcomes = await new EFCorePostgresPerspectiveWriteSink(sinkContext).WriteAsync([.. writes.Select(p => p.Write!)]);
      var completions = new List<PerspectiveCursorCompletion>();
      for (var i = 0; i < Prepared.Count; i++) {
        var index = writes.IndexOf(Prepared[i]);
        completions.Add(await lanes[i].Runner.CompleteAsync(Prepared[i], index < 0 ? null : outcomes[index]));
      }
      await locks.DisposeAsync();
      foreach (var (_, context) in lanes) {
        await context.DisposeAsync();
      }
      await sinkContext.DisposeAsync();
      return completions;
    }
  }

  private async Task<PreparedBatch> _prepareBatchAsync(
      RunnerFactory runner, IEventStore eventStore, IPerspectivePurgeMarkerStore? markers, IPerspectiveBatchState batch,
      List<(Guid Stream, string Perspective, Guid? Cursor, IReadOnlyList<MessageEnvelope<IEvent>> Events)> pairs) {
    var coordinator = new SingleHolderCoordinator();
    var locks = await PerspectiveApplyLockSet.AcquireAsync(coordinator, pairs.Select(p => (p.Stream, p.Perspective)));
    var lanes = new List<(IPerspectiveRunner, WorkCoordinationDbContext)>();
    var prepared = new List<PreparedPerspectiveApply>();
    foreach (var (stream, perspective, cursor, events) in pairs) {
      var context = CreateDbContext();
      var laneRunner = runner(eventStore, context, markers, null, coordinator);
      lanes.Add((laneRunner, context));
      prepared.Add(await laneRunner.PrepareAsync(stream, perspective, cursor, events, batch, CancellationToken.None));
    }
    return new PreparedBatch(lanes, prepared, locks, CreateDbContext());
  }
}
