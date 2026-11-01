// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Archive read-through end to end on the EF Core driver (#1738, WS-3 S9): a stream archived whole, a stream archived
/// through a prefix and the same stream never archived load, rebuild and rewind to the same model through the event
/// store the driver registers, with the archive seam innermost in its decorator chain; and a read that resumes after an
/// event issues no statement beyond the hot read, while a read from the start of an archived stream issues one more.
/// </summary>
/// <docs>fundamentals/events/archive-store#read-through</docs>
/// <code-under-test>src/Whizbang.Core/Archival/ArchiveAwareEventStoreDecorator.cs</code-under-test>
[Category("Integration")]
[Category("Shard1")]
[NotInParallel("NpgsqlActivityListener")]
public class ArchiveReadThroughIntegrationTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.RebuildBalancePerspective";
  private static readonly IReadOnlyList<Type> _types = [
    typeof(RebuildCreditedEvent), typeof(RebuildDebitedEvent), typeof(RebuildBalanceSetEvent), typeof(RebuildLabelSetEvent),
  ];

  // ===== Load =====

  [Test]
  public async Task Load_AHotStream_AStreamArchivedWhole_AndOneArchivedThroughAPrefix_ReadTheSameEventsAsync() {
    await using var sp = _services(readThrough: true);
    var (hot, whole, prefix) = await _threeCopiesAsync(sp);

    await using (var control = sp.CreateAsyncScope()) {
      await Assert.That(await _describeAsync(control.ServiceProvider.GetRequiredService<IEventStore>().ReadPolymorphicAsync(hot, null, _types)))
        .IsEquivalentTo(["credit 10", "debit 3", "set 50", "label settled", "credit 5"])
        .Because("the control: the hot copy reads every event, so an equal archived read is not two empty reads");
    }
    foreach (var archived in new[] { whole, prefix }) {
      await using var scope = sp.CreateAsyncScope();
      var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
      await Assert.That(await _describeAsync(store.ReadPolymorphicAsync(archived, null, _types)))
        .IsEquivalentTo(await _describeAsync(store.ReadPolymorphicAsync(hot, null, _types)));
      await Assert.That((await store.GetEventsBetweenPolymorphicAsync(archived, null, Guid.Empty, _types)).Select(_describe))
        .IsEquivalentTo((await store.GetEventsBetweenPolymorphicAsync(hot, null, Guid.Empty, _types)).Select(_describe));
    }
  }

  [Test]
  public async Task Load_TypedReads_OfAnArchivedStream_ReturnItsEventsAsync() {
    await using var sp = _services(readThrough: true);
    var stream = Guid.CreateVersion7();
    await _appendAsync(sp, stream, [
      new RebuildCreditedEvent { StreamId = stream, Amount = 10m },
      new RebuildCreditedEvent { StreamId = stream, Amount = 5m },
      new RebuildCreditedEvent { StreamId = stream, Amount = 1m },
    ]);
    _ = await _archive().WriteAsync(ArchiveBatch.Prefix(stream, 1));

    await using var scope = sp.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
    var bySequence = await _listAsync(store.ReadAsync<RebuildCreditedEvent>(stream, fromSequence: 0));
    var byId = await _listAsync(store.ReadAsync<RebuildCreditedEvent>(stream, (Guid?)null));
    var between = await store.GetEventsBetweenAsync<RebuildCreditedEvent>(stream, null, Guid.Empty);

    foreach (var read in new[] { bySequence, byId, between }) {
      await Assert.That(read.Select(e => e.Payload.Amount)).IsEquivalentTo([10m, 5m, 1m])
        .Because("versions 0 and 1 are archived and read back ahead of the hot version 2");
    }
  }

  [Test]
  public async Task Load_WithReadThroughOff_ReturnsOnlyTheHotEventsAsync() {
    await using var sp = _services(readThrough: false);
    var (hot, whole, prefix) = await _threeCopiesAsync(sp);

    await using var scope = sp.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
    await Assert.That(await _describeAsync(store.ReadPolymorphicAsync(whole, null, _types))).IsEmpty();
    await Assert.That((await _describeAsync(store.ReadPolymorphicAsync(prefix, null, _types))).Count)
      .IsLessThan((await _describeAsync(store.ReadPolymorphicAsync(hot, null, _types))).Count)
      .Because("the control: with read-through off, the archived prefix is not read");
  }

  // ===== Rebuild and rewind =====

  // #1931 flipped #1738's rebuild: reading an archived prefix through the archive is now an opt-in (IncludeWhileLive).
  [Test]
  public async Task Rebuild_IncludeWhileLive_AHotStream_AndOneArchivedThroughAPrefix_FoldToTheSameRowAsync() {
    await using var sp = _services(readThrough: true);
    var (hot, _, prefix) = await _threeCopiesAsync(sp);

    var result = await _rebuilder(sp).RebuildStreamsAsync(
      PERSPECTIVE, [hot, prefix], new RebuildOrigin { ArchivedData = RebuildArchivedData.IncludeWhileLive }, CancellationToken.None);

    await Assert.That(result.Error).IsNull();
    await Assert.That(result.Success).IsTrue();
    var expected = await _rowAsync(hot);
    await Assert.That(expected).IsEqualTo((55m, (string?)null)).Because("the control: the hot stream folds to the value the events set (a credit after the label drops it, as this perspective folds)");
    await Assert.That(await _rowAsync(prefix)).IsEqualTo(expected)
      .Because("the prefix's archived credit and debit are read back ahead of its hot events");
  }

  [Test]
  public async Task Rebuild_ByDefault_SkipsAStreamArchivedThroughAPrefix_WithNoLiveSnapshot_AndReportsItSkippedAsync() {
    var archive = new CountingArchive(_archive());
    await using var sp = _services(readThrough: true, archive: archive);
    var (hot, _, prefix) = await _threeCopiesAsync(sp);

    var result = await _rebuilder(sp).RebuildStreamsAsync(PERSPECTIVE, [hot, prefix], CancellationToken.None);

    await Assert.That(result.Success).IsTrue().Because("a skipped stream is reported, not failed");
    await Assert.That(result.StreamsSkipped).IsEqualTo(1);
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);
    await Assert.That(await _rowExistsAsync(prefix)).IsFalse()
      .Because("without a live snapshot the stream cannot be rebuilt from hot events alone, so it is left as it was");
    await Assert.That(archive.Reads).IsEqualTo(0).Because("the default never reads the archive");
  }

  [Test]
  public async Task Rebuild_ByDefault_StartsAStreamArchivedThroughAPrefix_FromItsNewestLiveSnapshot_AndReadsNoArchivedEventAsync() {
    var archive = new CountingArchive(_archive());
    await using var sp = _services(readThrough: true, archive: archive, withSnapshots: true);
    var (_, _, prefix) = await _threeCopiesAsync(sp);
    // A snapshot at the stream's first hot event (version 3, the balance set). Its model says 1000 where the events say
    // 50, so the row shows whether the rebuild started from it or replayed the stream from its start.
    await _snapshotAsync(prefix, await _firstHotEventAsync(prefix), new RebuildBalanceModel { Id = prefix, Balance = 1000m });

    var result = await _rebuilder(sp).RebuildStreamsAsync(PERSPECTIVE, [prefix], CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsSkipped).IsEqualTo(0);
    await Assert.That(await _rowAsync(prefix)).IsEqualTo((1005m, (string?)null))
      .Because("the rebuild starts from the snapshot and folds the hot events after it: the label, then the credit of 5");
    await Assert.That(archive.Reads).IsEqualTo(0).Because("the default never reads the archive");
  }

  [Test]
  [Arguments(RebuildArchivedData.Skip)]
  [Arguments(RebuildArchivedData.IncludeWhileLive)]
  public async Task Rebuild_WithoutIncludeAll_SkipsAStreamArchivedWhole_AndReportsItSkippedAsync(RebuildArchivedData choice) {
    await using var sp = _services(readThrough: true);
    var (_, whole, _) = await _threeCopiesAsync(sp);

    var result = await _rebuilder(sp).RebuildStreamsAsync(
      PERSPECTIVE, [whole], new RebuildOrigin { ArchivedData = choice }, CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsSkipped).IsEqualTo(1);
    await Assert.That(await _rowExistsAsync(whole)).IsFalse();
  }

  // #1958: a stream whose prefix archive was deleted read as never archived, so an opt-in rebuild folded its hot events
  // alone and reported the partial history as rebuilt.
  [Test]
  [Arguments(RebuildArchivedData.IncludeWhileLive)]
  [Arguments(RebuildArchivedData.IncludeAll)]
  public async Task Rebuild_ThatReadsArchivedData_SkipsAStreamWhosePrefixArchiveWasDeleted_AndReportsItSkippedAsync(RebuildArchivedData choice) {
    await using var sp = _services(readThrough: true);
    var (_, _, prefix) = await _threeCopiesAsync(sp);
    await Assert.That(await _archive().DeleteAsync(prefix)).IsGreaterThan(0L).Because("the control: the prefix archive held events");

    var result = await _rebuilder(sp).RebuildStreamsAsync(PERSPECTIVE, [prefix], new RebuildOrigin { ArchivedData = choice }, CancellationToken.None);

    await Assert.That(result.Success).IsTrue().Because("a skipped stream is reported, not failed");
    await Assert.That(result.StreamsSkipped).IsEqualTo(1)
      .Because("the history the rebuild was asked to read is deleted, and a replay of the hot events alone would fold part of it");
    await Assert.That(result.StreamsProcessed).IsEqualTo(0);
    await Assert.That(await _rowExistsAsync(prefix)).IsFalse().Because("the row is left as it was");
  }

  [Test]
  public async Task Rebuild_ByDefault_StartsAStreamWhosePrefixArchiveWasDeleted_FromItsNewestLiveSnapshotAsync() {
    await using var sp = _services(readThrough: true, withSnapshots: true);
    var (_, _, prefix) = await _threeCopiesAsync(sp);
    await Assert.That(await _archive().DeleteAsync(prefix)).IsGreaterThan(0L).Because("the control: the prefix archive held events");
    await _snapshotAsync(prefix, await _firstHotEventAsync(prefix), new RebuildBalanceModel { Id = prefix, Balance = 1000m });

    var result = await _rebuilder(sp).RebuildStreamsAsync(PERSPECTIVE, [prefix], CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsSkipped).IsEqualTo(0);
    await Assert.That(await _rowAsync(prefix)).IsEqualTo((1005m, (string?)null))
      .Because("the default never reads the archive, so a deleted one changes nothing: the stream starts from its newest live snapshot");
  }

  [Test]
  public async Task Rebuild_IncludeAll_SkipsAStreamWhoseWholeArchiveWasDeleted_AndReportsItSkippedAsync() {
    await using var sp = _services(readThrough: true);
    var (_, whole, _) = await _threeCopiesAsync(sp);
    await Assert.That(await _archive().DeleteAsync(whole)).IsEqualTo(5L);

    var result = await _rebuilder(sp).RebuildStreamsAsync(
      PERSPECTIVE, [whole], new RebuildOrigin { ArchivedData = RebuildArchivedData.IncludeAll }, CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsSkipped).IsEqualTo(1).Because("nothing of the stream remains to replay");
    await Assert.That(await _rowExistsAsync(whole)).IsFalse();
  }

  [Test]
  public async Task Rebuild_IncludeAll_RebuildsAStreamArchivedWhole_ParksItsCursor_AndTheRestoreBringsTheCursorBackAsync() {
    await using var sp = _services(readThrough: true);
    var (hot, whole, _) = await _threeCopiesAsync(sp);
    await _rebuilder(sp).RebuildStreamsAsync(PERSPECTIVE, [hot], CancellationToken.None);

    var result = await _rebuilder(sp).RebuildStreamsAsync(
      PERSPECTIVE, [whole], new RebuildOrigin { ArchivedData = RebuildArchivedData.IncludeAll }, CancellationToken.None);

    await Assert.That(result.Error).IsNull().Because("the cursor names an archived event, so it is parked rather than written");
    await Assert.That(result.Success).IsTrue();
    await Assert.That(await _rowAsync(whole)).IsEqualTo(await _rowAsync(hot));
    var lastArchived = await _scalarAsync<Guid>(
      "SELECT event_id FROM wh_event_archive WHERE stream_id = @s ORDER BY version DESC LIMIT 1", whole);
    await Assert.That(await _scalarAsync<long>(
        "SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = @s", whole)).IsEqualTo(0L)
      .Because("no live cursor may name an event the hot store does not hold");
    await Assert.That(await _scalarAsync<string>(
        "SELECT cursor_row ->> 'last_event_id' FROM wh_event_archive_cursors WHERE stream_id = @s AND perspective_name = @p",
        whole, PERSPECTIVE)).IsEqualTo(lastArchived.ToString())
      .Because("the cursor is parked with the stream while it is archived whole");

    await Assert.That((await _archive().RestoreAsync(whole)).Status).IsEqualTo(ArchiveStatus.RESTORED);

    await Assert.That(await _scalarAsync<Guid>(
        "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p", whole, PERSPECTIVE))
      .IsEqualTo(lastArchived).Because("the restore brings the parked cursor back with the events it names");
  }

  [Test]
  public async Task Rewind_WithoutASnapshot_OfAStreamArchivedWhole_FoldsToTheSameRowAsTheHotCopyAsync() {
    await using var sp = _services(readThrough: true);
    var (hot, whole, _) = await _threeCopiesAsync(sp);

    foreach (var stream in new[] { hot, whole }) {
      await using var scope = sp.CreateAsyncScope();
      var runner = scope.ServiceProvider.GetRequiredService<IPerspectiveRunnerRegistry>().GetRunner(PERSPECTIVE, scope.ServiceProvider)!;
      _ = await runner.RewindAndRunAsync(stream, PERSPECTIVE, Guid.CreateVersion7(), CancellationToken.None);
    }

    await Assert.That(await _rowAsync(whole)).IsEqualTo(await _rowAsync(hot))
      .Because("a rewind with no snapshot replays the stream from its start, which reads through the archive");
  }

  // ===== Cost: a resumed read costs nothing more =====

  [Test]
  public async Task AResumedRead_IssuesNoStatementBeyondTheHotRead_AndAReadFromTheStartOfAnArchivedStreamIssuesOneMoreAsync() {
    await using var decorated = _services(readThrough: true);
    await using var plain = _services(readThrough: true, withArchive: false);
    var (hot, whole, prefix) = await _threeCopiesAsync(decorated);
    var resumeAt = await _firstHotEventAsync(prefix);

    var resumedDecorated = await _countStatementsAsync(decorated, s => _drainAsync(s.ReadPolymorphicAsync(prefix, resumeAt, _types)));
    var resumedPlain = await _countStatementsAsync(plain, s => _drainAsync(s.ReadPolymorphicAsync(prefix, resumeAt, _types)));
    var betweenDecorated = await _countStatementsAsync(decorated, s => s.GetEventsBetweenPolymorphicAsync(prefix, resumeAt, Guid.Empty, _types));
    var betweenPlain = await _countStatementsAsync(plain, s => s.GetEventsBetweenPolymorphicAsync(prefix, resumeAt, Guid.Empty, _types));
    var hotFromStartDecorated = await _countStatementsAsync(decorated, s => _drainAsync(s.ReadPolymorphicAsync(hot, null, _types)));
    var hotFromStartPlain = await _countStatementsAsync(plain, s => _drainAsync(s.ReadPolymorphicAsync(hot, null, _types)));
    var archivedFromStartDecorated = await _countStatementsAsync(decorated, s => _drainAsync(s.ReadPolymorphicAsync(whole, null, _types)));
    var archivedFromStartPlain = await _countStatementsAsync(plain, s => _drainAsync(s.ReadPolymorphicAsync(whole, null, _types)));

    await Assert.That(resumedPlain).IsGreaterThan(0).Because("the control: the listener sees the hot read's statement");
    await Assert.That(resumedDecorated).IsEqualTo(resumedPlain)
      .Because("a read that resumes after an event never looks at the archive: zero added statements");
    await Assert.That(betweenDecorated).IsEqualTo(betweenPlain);
    await Assert.That(hotFromStartDecorated).IsEqualTo(hotFromStartPlain)
      .Because("a stream that was never archived shows no gap, so a read from its start costs what it always did");
    await Assert.That(archivedFromStartDecorated).IsEqualTo(archivedFromStartPlain + 1)
      .Because("the gap costs one archive read, and only on the read that shows it");
  }

  // =================================================================================================

  private PostgresTableArchiveStore _archive() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  private ServiceProvider _services(bool readThrough, bool withArchive = true, IArchiveStore? archive = null, bool withSnapshots = false) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddScoped<WorkCoordinationDbContext>(_ => new WorkCoordinationDbContext(DbContextOptions));
    services.AddScoped<DbContext>(sp => sp.GetRequiredService<WorkCoordinationDbContext>());
    archive ??= _archive();
    // What a rebuild knows of each stream's archive (#1931), as both drivers register it.
    services.AddSingleton<IArchivedStreamExtents>(new PostgresArchivedStreamExtents(
      new PostgresArchiveConnection(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, Schema: null)));
    if (withSnapshots) {
      services.AddSingleton<IPerspectiveSnapshotStore>(new EFCorePerspectiveSnapshotStore(NpgsqlDataSource.Create(ConnectionString)));
    }
    services.AddScoped<IEventStore>(sp => {
      var driver = new EFCoreEventStore<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>());
      return withArchive ? new ArchiveAwareEventStoreDecorator(driver, archive, readThrough) : driver;
    });
    services.AddScoped<IEventStoreQuery>(sp => new EFCoreFilterableEventStoreQuery(sp.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddScoped<IPerspectiveStore<RebuildBalanceModel>>(sp =>
      new EFCorePostgresPerspectiveStore<RebuildBalanceModel>(sp.GetRequiredService<WorkCoordinationDbContext>(), "wh_per_rebuild_balance"));
    services.AddScoped<IPerspectiveStore<RebuildInventoryModel>>(sp =>
      new EFCorePostgresPerspectiveStore<RebuildInventoryModel>(sp.GetRequiredService<WorkCoordinationDbContext>(), "wh_per_rebuild_inventory"));
    services.AddScoped<RebuildBalancePerspective>();
    services.AddScoped<RebuildInventoryPerspective>();
    var asm = typeof(ArchiveReadThroughIntegrationTests).Assembly;
    services.AddScoped(asm.GetTypes().Single(t => t.Name == "RebuildBalancePerspectiveRunner"));
    services.AddScoped(asm.GetTypes().Single(t => t.Name == "RebuildInventoryPerspectiveRunner"));
    services.AddSingleton(typeof(IPerspectiveRunnerRegistry), asm.GetTypes().Single(t =>
      t.Name == "PerspectiveRunnerRegistry" && t.Namespace == "Whizbang.Data.EFCore.Postgres.Tests.Generated"));
    services.AddScoped<IReceptorInvoker, NoReceptorInvoker>();
    services.AddScoped<IPerspectiveCheckpointCompleter>(sp =>
      new EFCorePostgresPerspectiveCheckpointCompleter(sp.GetRequiredService<WorkCoordinationDbContext>()));
    return services.BuildServiceProvider();
  }

  private static PerspectiveRebuilder _rebuilder(ServiceProvider sp) =>
    new(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILogger<PerspectiveRebuilder>>());

  private async Task _snapshotAsync(Guid stream, Guid atEvent, RebuildBalanceModel model) {
    var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<RebuildBalanceModel>)
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(RebuildBalanceModel));
    // Stamped with the serialization version as the runner writes a snapshot, so the runner reads it back.
    using var modelData = System.Text.Json.JsonSerializer.SerializeToDocument(model, typeInfo);
    using var data = SnapshotEnvelope.Wrap(modelData.RootElement, Whizbang.Core.Serialization.SerializationVersion.CURRENT);
    await new EFCorePerspectiveSnapshotStore(NpgsqlDataSource.Create(ConnectionString))
      .CreateSnapshotAsync(stream, PERSPECTIVE, atEvent, data);
  }

  private async Task<bool> _rowExistsAsync(Guid stream) =>
    await _scalarAsync<long>("SELECT count(*) FROM wh_per_rebuild_balance WHERE id = @s", stream) > 0;

  private async Task<T> _scalarAsync<T>(string sql, Guid stream, string? perspective = null) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("s", stream);
    if (perspective is not null) {
      cmd.Parameters.AddWithValue("p", perspective);
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>The table store, counting the reads that reach it.</summary>
  private sealed class CountingArchive(IArchiveStore inner) : IArchiveStore {
    private int _reads;
    public int Reads => _reads;
    public Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default) => inner.WriteAsync(batch, cancellationToken);
    public IAsyncEnumerable<ArchivedEvent> ReadAsync(Guid streamId, long fromVersion = 0, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _reads);
      return inner.ReadAsync(streamId, fromVersion, cancellationToken);
    }
    public Task<ArchiveRestoreResult> RestoreAsync(Guid streamId, CancellationToken cancellationToken = default) => inner.RestoreAsync(streamId, cancellationToken);
    public Task<long> DeleteAsync(Guid streamId, CancellationToken cancellationToken = default) => inner.DeleteAsync(streamId, cancellationToken);
  }

  /// <summary>Lifecycle receptors are not under test here.</summary>
  private sealed class NoReceptorInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  // The same five events on three streams: one left hot, one archived whole, one archived through version 2.
  private async Task<(Guid Hot, Guid Whole, Guid Prefix)> _threeCopiesAsync(ServiceProvider sp) {
    var streams = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
    foreach (var stream in streams) {
      await _appendAsync(sp, stream, [
        new RebuildCreditedEvent { StreamId = stream, Amount = 10m },
        new RebuildDebitedEvent { StreamId = stream, Amount = 3m },
        new RebuildBalanceSetEvent { StreamId = stream, Balance = 50m },
        new RebuildLabelSetEvent { StreamId = stream, Label = "settled" },
        new RebuildCreditedEvent { StreamId = stream, Amount = 5m },
      ]);
    }
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.WholeStream(streams[1]))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(streams[2], 2))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    return (streams[0], streams[1], streams[2]);
  }

  // Each event appended as its own type, as a producer appends it, so the store records the type a read resolves by.
  private static async Task _appendAsync(ServiceProvider sp, Guid stream, IEvent[] events) {
    await using var scope = sp.CreateAsyncScope();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>());
    foreach (var e in events) {
      await (e switch {
        RebuildCreditedEvent c => store.AppendAsync(stream, _envelope(c)),
        RebuildDebitedEvent d => store.AppendAsync(stream, _envelope(d)),
        RebuildBalanceSetEvent b => store.AppendAsync(stream, _envelope(b)),
        RebuildLabelSetEvent l => store.AppendAsync(stream, _envelope(l)),
        _ => throw new ArgumentException($"no append for {e.GetType().Name}", nameof(events)),
      });
    }
  }

  private static MessageEnvelope<T> _envelope<T>(T payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private async Task<Guid> _firstHotEventAsync(Guid stream) {
    await using var ctx = CreateDbContext();
    return (await ctx.Database.SqlQueryRaw<Guid>(
      "SELECT event_id AS \"Value\" FROM wh_event_store WHERE stream_id = {0} ORDER BY version LIMIT 1", stream).ToListAsync()).Single();
  }

  private async Task<(decimal Balance, string? Label)> _rowAsync(Guid stream) {
    await using var ctx = CreateDbContext();
    var row = await ctx.Set<PerspectiveRow<RebuildBalanceModel>>().AsNoTracking().FirstAsync(r => r.Id == stream);
    return (row.Data.Balance, row.Data.Label);
  }

  private static string _describe(MessageEnvelope<IEvent> e) => e.Payload switch {
    RebuildCreditedEvent c => $"credit {c.Amount}",
    RebuildDebitedEvent d => $"debit {d.Amount}",
    RebuildBalanceSetEvent b => $"set {b.Balance}",
    RebuildLabelSetEvent l => $"label {l.Label}",
    _ => e.Payload.GetType().Name,
  };

  private static async Task<List<string>> _describeAsync(IAsyncEnumerable<MessageEnvelope<IEvent>> read) =>
    (await _listAsync(read)).ConvertAll(_describe);

  private static async Task<List<T>> _listAsync<T>(IAsyncEnumerable<T> source) {
    var list = new List<T>();
    await foreach (var item in source) {
      list.Add(item);
    }
    return list;
  }

  private static async Task _drainAsync<T>(IAsyncEnumerable<T> source) {
    await foreach (var _ in source) {
      // reading is the point
    }
  }

  // The statements Npgsql runs against this test's database while the read runs, from its tracing source.
  private async Task<int> _countStatementsAsync(ServiceProvider sp, Func<IEventStore, Task> read) {
    var database = new NpgsqlConnectionStringBuilder(ConnectionString).Database;
    var count = 0;
    using var listener = new ActivityListener {
      ShouldListenTo = source => source.Name == "Npgsql",
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
      ActivityStopped = activity => {
        if (activity.GetTagItem("db.namespace") as string == database && activity.GetTagItem("db.query.text") is string) {
          Interlocked.Increment(ref count);
        }
      },
    };
    ActivitySource.AddActivityListener(listener);
    await using var scope = sp.CreateAsyncScope();
    await read(scope.ServiceProvider.GetRequiredService<IEventStore>());
    return Volatile.Read(ref count);
  }
}
