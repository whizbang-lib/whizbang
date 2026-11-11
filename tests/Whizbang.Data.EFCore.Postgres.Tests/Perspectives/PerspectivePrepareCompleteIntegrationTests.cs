// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The set-based write splits a generated runner's apply in two (#2246): <c>PrepareAsync</c> reads the row and folds the
/// batch, a write sink writes the batch's rows together, and <c>CompleteAsync</c> does what follows the write. Run on
/// PostgreSQL with real generated runners, each case here holds the split to the per-pair path it replaces: the same
/// row, document, metadata, columns, snapshot and completion, and every case the batch write does not take handed back
/// to the per-pair path, which then ends exactly as it always did.
/// </summary>
/// <remarks>
/// Every set-based run holds the pair's apply lock, as the worker will, through a coordinator that fails on a second
/// acquisition: prepare and complete take no lock of their own, including on the paths that rerun the pair.
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#prepare-and-commit</docs>
[Category("Integration")]
[Category("Shard4")]
public class PerspectivePrepareCompleteIntegrationTests : EFCoreTestBase {
  private const string ACTION_TABLE = "wh_per_action_test";
  private const string PROGRESS_TABLE = "wh_per_item_progress";
  private const string SPLIT_CLASS_TABLE = "wh_per_split_class_snapshot";
  private const string SPLIT_RECORD_TABLE = "wh_per_init_only_split";
  private const string EXTRACTED_TABLE = "wh_per_init_only_extracted";
  private const string CAPPED_TABLE = "wh_per_prepared_apply_capped_item";

  // ---------------------------------------------------------------------------------------------------------------
  // The batch write stores what the per-pair path stores
  // ---------------------------------------------------------------------------------------------------------------

  [Test]
  public async Task ANewStream_StoresTheRowAndCompletionThePerPairPathDoesAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    var created = await _appendAsync(eventStore, streamId, new ActionTestCreatedEvent { StreamId = streamId, Name = "first", Value = 1 }, 1);

    var perPair = await _perPairAsync(_actionRunner, eventStore, streamId, "action_test", null, [created]);
    var perPairRow = await _readRowAsync(ACTION_TABLE, streamId);
    await _deleteRowAsync(ACTION_TABLE, streamId);

    var setBased = await _setBasedAsync(_actionRunner, eventStore, streamId, "action_test", null, [created]);

    await Assert.That(setBased.Prepared.Write!.Operation).IsEqualTo(PerspectiveWriteOperation.Insert)
      .Because("the apply read no row, so the batch inserts it");
    await Assert.That(setBased.Outcome!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Written);
    await _assertSameRowAsync(perPairRow, await _readRowAsync(ACTION_TABLE, streamId));
    await _assertSameCompletionAsync(perPair, setBased.Completion);
  }

  [Test]
  public async Task AnExistingStream_StoresTheRowAndCompletionThePerPairPathDoesAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    var created = await _appendAsync(eventStore, streamId, new ActionTestCreatedEvent { StreamId = streamId, Name = "first", Value = 1 }, 1);
    var updated = await _appendAsync(eventStore, streamId, new ActionTestUpdatedEvent { StreamId = streamId, NewValue = 2 }, 2);

    await _perPairAsync(_actionRunner, eventStore, streamId, "action_test", null, [created]);
    var perPair = await _perPairAsync(_actionRunner, eventStore, streamId, "action_test", created.MessageId.Value, [updated]);
    var perPairRow = await _readRowAsync(ACTION_TABLE, streamId);
    await _deleteRowAsync(ACTION_TABLE, streamId);
    await _perPairAsync(_actionRunner, eventStore, streamId, "action_test", null, [created]);

    var setBased = await _setBasedAsync(_actionRunner, eventStore, streamId, "action_test", created.MessageId.Value, [updated]);

    await Assert.That(setBased.Prepared.Write!.Operation).IsEqualTo(PerspectiveWriteOperation.Update)
      .Because("the apply read the row, so the batch updates it on the version it read");
    await Assert.That(setBased.Outcome!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Written);
    await _assertSameRowAsync(perPairRow, await _readRowAsync(ACTION_TABLE, streamId));
    await _assertSameCompletionAsync(perPair, setBased.Completion);
  }

  [Test]
  public async Task PhysicalFieldsStoredExtracted_AreWrittenToTheirColumnsAndTheDocumentAsThePerPairPathDoesAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    var set = await _appendAsync(eventStore, streamId, new InitOnlyFieldsSetEvent { StreamId = streamId, Status = "active", Priority = 7 }, 1);

    var perPair = await _perPairAsync(_extractedRunner, eventStore, streamId, "init_only_extracted", null, [set]);
    var perPairRow = await _readRowAsync(EXTRACTED_TABLE, streamId, "status", "priority");
    await _deleteRowAsync(EXTRACTED_TABLE, streamId);

    var setBased = await _setBasedAsync(_extractedRunner, eventStore, streamId, "init_only_extracted", null, [set]);

    var row = await _readRowAsync(EXTRACTED_TABLE, streamId, "status", "priority");
    await _assertSameRowAsync(perPairRow, row);
    await Assert.That(row![6]).IsEqualTo("active").Because("the promoted field is in its column");
    await Assert.That(row[0]).Contains("active").Because("an Extracted field is in the document too");
    await _assertSameCompletionAsync(perPair, setBased.Completion);
  }

  [Test]
  public async Task PhysicalFieldsStoredSplit_AreWrittenToTheirColumnsOnlyAsThePerPairPathDoesAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    var set = await _appendAsync(eventStore, streamId, new InitOnlyFieldsSetEvent { StreamId = streamId, Status = "active", Priority = 7, Note = "doc" }, 1);

    var perPair = await _perPairAsync(_splitRecordRunner, eventStore, streamId, "init_only_split", null, [set]);
    var perPairRow = await _readRowAsync(SPLIT_RECORD_TABLE, streamId, "status", "priority");
    await _deleteRowAsync(SPLIT_RECORD_TABLE, streamId);

    var setBased = await _setBasedAsync(_splitRecordRunner, eventStore, streamId, "init_only_split", null, [set]);

    var row = await _readRowAsync(SPLIT_RECORD_TABLE, streamId, "status", "priority");
    await _assertSameRowAsync(perPairRow, row);
    await Assert.That(row![6]).IsEqualTo("active");
    await Assert.That(row[0]).DoesNotContain("active").Because("a Split field lives only in its column");
    await _assertSameCompletionAsync(perPair, setBased.Completion);
  }

  [Test]
  public async Task ASplitClassModel_TakesTheSnapshotThePerPairPathTakes_WithItsPromotedFieldsAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var snapshots = new EFCorePerspectiveSnapshotStore(dataSource);
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    var status = await _appendAsync(eventStore, streamId, new SplitClassSnapshotStatusSetEvent { StreamId = streamId, Status = "active", Priority = 7 }, 1);
    var note = await _appendAsync(eventStore, streamId, new SplitClassSnapshotNoteChangedEvent { StreamId = streamId, Note = "second" }, 2);
    var runner = _splitClassRunner(snapshots);

    var perPair = await _perPairAsync(runner, eventStore, streamId, SPLIT_CLASS_TABLE, null, [status, note]);
    var perPairRow = await _readRowAsync(SPLIT_CLASS_TABLE, streamId, "status", "priority");
    var perPairSnapshot = await _snapshotTextAsync(snapshots, streamId, SPLIT_CLASS_TABLE);
    await _deleteRowAsync(SPLIT_CLASS_TABLE, streamId);
    await _executeAsync("DELETE FROM wh_perspective_snapshots WHERE stream_id = @id", streamId);

    var setBased = await _setBasedAsync(runner, eventStore, streamId, SPLIT_CLASS_TABLE, null, [status, note]);

    await _assertSameRowAsync(perPairRow, await _readRowAsync(SPLIT_CLASS_TABLE, streamId, "status", "priority"));
    var snapshot = await _snapshotTextAsync(snapshots, streamId, SPLIT_CLASS_TABLE);
    await Assert.That(snapshot).IsEqualTo(perPairSnapshot);
    await Assert.That(snapshot).Contains("active")
      .Because("the snapshot is the model as it was applied, taken before the write stripped the promoted fields in place");
    await _assertSameCompletionAsync(perPair, setBased.Completion);
  }

  [Test]
  public async Task APurge_RemovesTheRowAndMarksTheStreamAsThePerPairPathDoesAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var markers = new PostgresPerspectivePurgeMarkerStore(dataSource.OpenConnectionAsync);
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    var created = await _appendAsync(eventStore, streamId, new ActionTestCreatedEvent { StreamId = streamId, Name = "first", Value = 1 }, 1);
    var purged = await _appendAsync(eventStore, streamId, new ActionTestPurgedEvent { StreamId = streamId }, 2);
    RunnerFactory runner = _actionRunner;

    await _perPairAsync(runner, eventStore, streamId, "action_test", null, [created], markers);
    var perPair = await _perPairAsync(runner, eventStore, streamId, "action_test", created.MessageId.Value, [purged], markers);
    await Assert.That(await markers.IsPurgedAsync(streamId, "action_test")).IsTrue();
    await markers.ClearAsync(streamId, "action_test");
    await _perPairAsync(runner, eventStore, streamId, "action_test", null, [created], markers);

    var setBased = await _setBasedAsync(runner, eventStore, streamId, "action_test", created.MessageId.Value, [purged], markers);

    await Assert.That(setBased.Prepared.Write!.Operation).IsEqualTo(PerspectiveWriteOperation.Purge);
    await Assert.That(await _readRowAsync(ACTION_TABLE, streamId)).IsNull();
    await Assert.That(await markers.IsPurgedAsync(streamId, "action_test")).IsTrue()
      .Because("purge stays purged: the marker is what keeps a later event from recreating the row");
    await _assertSameCompletionAsync(perPair, setBased.Completion);
    await Assert.That(setBased.Completion.PurgedBy).IsNotNull();
  }

  [Test]
  public async Task ABatchTheRowAlreadyHolds_HasNothingToWrite_AndCompletesAsThePerPairPathDoesAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    var created = await _appendAsync(eventStore, streamId, new ActionTestCreatedEvent { StreamId = streamId, Name = "first", Value = 1 }, 1);
    await _perPairAsync(_actionRunner, eventStore, streamId, "action_test", null, [created]);
    var perPair = await _perPairAsync(_actionRunner, eventStore, streamId, "action_test", null, [created]);
    var before = await _readRowAsync(ACTION_TABLE, streamId);

    var setBased = await _setBasedAsync(_actionRunner, eventStore, streamId, "action_test", null, [created]);

    await Assert.That(setBased.Prepared.Write).IsNull();
    await Assert.That(setBased.Prepared.RunsPerPair).IsFalse()
      .Because("a batch the row already holds is complete; nothing about it needs the per-pair path");
    await Assert.That(setBased.Completion.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await _assertSameCompletionAsync(perPair, setBased.Completion);
    await _assertSameRowAsync(before, await _readRowAsync(ACTION_TABLE, streamId));
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Cases the batch write does not take are handed to the per-pair path, which ends as it always did
  // ---------------------------------------------------------------------------------------------------------------

  [Test]
  public async Task AnEventBelowTheRowsPosition_IsHandedToThePerPairPath_WhichRewindsAsync() {
    var streamId = Guid.CreateVersion7();
    var inner = new InMemoryEventStore();
    var started = await _appendAsync(inner, streamId, new ItemProgressStartedEvent { StreamId = streamId }, 1, Guid.Parse("019f0000-0000-7aaa-8000-000000000101"));
    var completed = await _appendAsync(inner, streamId, new ItemProgressCompletedEvent { StreamId = streamId }, 2, Guid.Parse("019f0001-0000-7aaa-8000-000000000101"));
    var eventStore = _stamped(inner, (started, 1000), (completed, 2000));
    // The row as a lost update left it: Completed applied, Started never folded.
    await _perPairAsync(_progressRunner, eventStore, streamId, "item_progress", null, [_copy(completed, eventStore)]);

    var setBased = await _setBasedAsync(_progressRunner, eventStore, streamId, "item_progress", null, [_copy(started, eventStore)]);

    await Assert.That(setBased.Prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.RewindBelowPosition);
    await Assert.That(setBased.Prepared.Write).IsNull();
    var row = await _progressRowAsync(streamId);
    await Assert.That(row.Started && row.Completed && row.Attempts == 1).IsTrue()
      .Because("the per-pair path re-folds the stream from the event below the row's position");
    await Assert.That(setBased.Completion.RowChanged).IsTrue();
  }

  [Test]
  public async Task AMissingRowWhoseStreamHasHistory_IsHandedToThePerPairPath_WhichRebuildsItAsync() {
    PerspectiveRowCapRegistry.Register(typeof(PreparedApplyCappedItem), 1000, null);
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    await _appendAsync(eventStore, streamId, new PreparedApplyCappedItemAddedEvent { StreamId = streamId }, 1);
    var second = await _appendAsync(eventStore, streamId, new PreparedApplyCappedItemAddedEvent { StreamId = streamId }, 2);

    var setBased = await _setBasedAsync(_cappedRunner, eventStore, streamId, "prepared_apply_capped_item", null, [second]);

    await Assert.That(setBased.Prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.MissingRowWithHistory)
      .Because("a missing row of an evictable perspective whose stream has earlier events is an evicted row, not a new one");
    await using var verify = CreateDbContext();
    var row = await verify.Set<PerspectiveRow<PreparedApplyCappedItem>>().AsNoTracking().SingleAsync(r => r.Id == streamId);
    await Assert.That(row.Data.Added).IsEqualTo(2)
      .Because("the per-pair path rebuilds the row from the whole stream, not from this batch alone");
  }

  [Test]
  public async Task APurgedStream_IsHandedToThePerPairPath_WhichKeepsItPurgedAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var markers = new PostgresPerspectivePurgeMarkerStore(dataSource.OpenConnectionAsync);
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    var created = await _appendAsync(eventStore, streamId, new ActionTestCreatedEvent { StreamId = streamId, Name = "first", Value = 1 }, 1);
    var purged = await _appendAsync(eventStore, streamId, new ActionTestPurgedEvent { StreamId = streamId }, 2);
    var updated = await _appendAsync(eventStore, streamId, new ActionTestUpdatedEvent { StreamId = streamId, NewValue = 5 }, 3);
    await _perPairAsync(_actionRunner, eventStore, streamId, "action_test", null, [created, purged], markers);

    var setBased = await _setBasedAsync(_actionRunner, eventStore, streamId, "action_test", purged.MessageId.Value, [updated], markers);

    await Assert.That(setBased.Prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.PurgedStream);
    await Assert.That(await _readRowAsync(ACTION_TABLE, streamId)).IsNull()
      .Because("an event after a purge is skipped, not applied to an empty model");
    await Assert.That(setBased.Completion.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(setBased.Completion.RowChanged).IsFalse();
  }

  [Test]
  public async Task AWriteTheStoreCannotPrepare_IsHandedToThePerPairPath_WhichWritesItAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    var created = await _appendAsync(eventStore, streamId, new ActionTestCreatedEvent { StreamId = streamId, Name = "first", Value = 1 }, 1);
    var perPair = await _perPairAsync(_actionRunner, eventStore, streamId, "action_test", null, [created]);
    var perPairRow = await _readRowAsync(ACTION_TABLE, streamId);
    await _deleteRowAsync(ACTION_TABLE, streamId);

    var setBased = await _setBasedAsync(_actionRunner, eventStore, streamId, "action_test", null, [created],
      wrapStore: store => new ForwardingStore<ActionTestModel>((IPerspectiveStore<ActionTestModel>)store) { Prepares = false });

    await Assert.That(setBased.Prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.WriteNotPreparable);
    await _assertSameRowAsync(perPairRow, await _readRowAsync(ACTION_TABLE, streamId));
    await _assertSameCompletionAsync(perPair, setBased.Completion);
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Outcomes of the batch write
  // ---------------------------------------------------------------------------------------------------------------

  [Test]
  public async Task AWriteTheOrderingGuardRefuses_RewindsTheStream_AndTheRowEndsWithEveryEventAsync() {
    var streamId = Guid.CreateVersion7();
    var inner = new InMemoryEventStore();
    var started = await _appendAsync(inner, streamId, new ItemProgressStartedEvent { StreamId = streamId }, 1, Guid.Parse("019f0000-0000-7aaa-8000-000000000201"));
    var completed = await _appendAsync(inner, streamId, new ItemProgressCompletedEvent { StreamId = streamId }, 2, Guid.Parse("019f0001-0000-7aaa-8000-000000000201"));
    var eventStore = _stamped(inner, (started, 1000), (completed, 2000));

    // The apply reads through a store that tracks no row version, so its write is an upsert under the ordering guard
    // alone; another worker commits the later event between the read and the batch write.
    var setBased = await _setBasedAsync(_progressRunner, eventStore, streamId, "item_progress", null, [_copy(started, eventStore)],
      wrapStore: store => new ForwardingStore<ItemProgress>((IPerspectiveStore<ItemProgress>)store) { FirstReadUnchecked = true },
      beforeWrite: () => _perPairAsync(_progressRunner, eventStore, streamId, "item_progress", null, [_copy(completed, eventStore)]));

    await Assert.That(setBased.Outcome!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Refused)
      .Because("the row holds a later event than the batch, and the guard turns the write away");
    var row = await _progressRowAsync(streamId);
    await Assert.That(row.Started).IsTrue()
      .Because("a refused batch is re-folded onto the row by a rewind, never reported applied while the row lacks it");
    await Assert.That(row.Completed).IsTrue();
    await Assert.That(row.Attempts).IsEqualTo(1);
    await Assert.That(setBased.Completion.RowChanged).IsTrue();
  }

  [Test]
  public async Task ARowThatMovesBeforeTheBatchWrite_IsAppliedAgainOnTheMovedRowAsync() {
    var streamId = Guid.CreateVersion7();
    var inner = new InMemoryEventStore();
    var firstStart = await _appendAsync(inner, streamId, new ItemProgressStartedEvent { StreamId = streamId }, 1, Guid.Parse("019f0000-0000-7aaa-8000-000000000301"));
    var secondStart = await _appendAsync(inner, streamId, new ItemProgressStartedEvent { StreamId = streamId }, 2, Guid.Parse("019f0001-0000-7aaa-8000-000000000301"));
    var completed = await _appendAsync(inner, streamId, new ItemProgressCompletedEvent { StreamId = streamId }, 3, Guid.Parse("019f0002-0000-7aaa-8000-000000000301"));
    var eventStore = _stamped(inner, (firstStart, 1000), (secondStart, 2000), (completed, 3000));
    await _perPairAsync(_progressRunner, eventStore, streamId, "item_progress", null, [_copy(firstStart, eventStore)]);

    var setBased = await _setBasedAsync(_progressRunner, eventStore, streamId, "item_progress", secondStart.MessageId.Value, [_copy(completed, eventStore)],
      beforeWrite: () => _perPairAsync(_progressRunner, eventStore, streamId, "item_progress", firstStart.MessageId.Value, [_copy(secondStart, eventStore)]));

    await Assert.That(setBased.Outcome!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Conflict)
      .Because("another worker moved the row after the apply read it");
    var row = await _progressRowAsync(streamId);
    await Assert.That(row.Attempts).IsEqualTo(2)
      .Because("the batch is folded again onto the row the other worker committed, so neither write is lost");
    await Assert.That(row.Completed).IsTrue();
    await Assert.That(setBased.Completion.RowChanged).IsTrue();
    await Assert.That(setBased.Completion.EventsProcessed).IsEqualTo(1);
  }

  [Test]
  public async Task ARowTheDatabaseRejects_FailsAsThePerPairPathFailsAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    // jsonb has no representation for the null character, so PostgreSQL refuses this document on every path.
    var created = await _appendAsync(eventStore, streamId, new ActionTestCreatedEvent { StreamId = streamId, Name = "bad\u0000name", Value = 1 }, 1);
    Exception? perPairFailure = null;
    try {
      await _perPairAsync(_actionRunner, eventStore, streamId, "action_test", null, [created]);
    } catch (Exception failure) {
      perPairFailure = failure;
    }
    await Assert.That(perPairFailure).IsNotNull();

    Exception? setBasedFailure = null;
    PerspectiveWriteOutcome? outcome = null;
    try {
      await _setBasedAsync(_actionRunner, eventStore, streamId, "action_test", null, [created],
        onOutcome: written => outcome = written);
    } catch (Exception failure) {
      setBasedFailure = failure;
    }

    await Assert.That(outcome!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Failed)
      .Because("the sink isolates the row the database rejected");
    await Assert.That(setBasedFailure).IsNotNull();
    await Assert.That(setBasedFailure!.GetType()).IsEqualTo(perPairFailure!.GetType());
    await Assert.That(setBasedFailure.Message).IsEqualTo(perPairFailure.Message)
      .Because("the rejected row runs the per-pair path, so the worker sees the failure it always saw");
    await Assert.That(await _readRowAsync(ACTION_TABLE, streamId)).IsNull();
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Harness
  // ---------------------------------------------------------------------------------------------------------------

  /// <summary>Builds a generated runner over a context, with optional purge markers, store wrapper and coordinator.</summary>
  private delegate IPerspectiveRunner RunnerFactory(
      IEventStore eventStore, WorkCoordinationDbContext context, IPerspectivePurgeMarkerStore? markers,
      Func<object, object>? wrapStore, IPerspectiveApplyCoordinator? coordinator);

  private sealed record SetBasedRun(PreparedPerspectiveApply Prepared, PerspectiveWriteOutcome? Outcome, PerspectiveCursorCompletion Completion);

  private static ServiceProvider _services<TPerspective>(IPerspectivePurgeMarkerStore? markers) where TPerspective : class {
    var services = new ServiceCollection();
    services.AddTransient<TPerspective>();
    services.AddLogging();
    if (markers is not null) {
      services.AddSingleton(markers);
    }
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// Constructs a generated runner by name. It is not named in code: <c>dotnet format</c> compiles without the source
  /// generators, and a generated type named here would not resolve there.
  /// </summary>
  private static IPerspectiveRunner _generated(
      string runnerName, ServiceProvider services, IEventStore eventStore, object perspectiveStore,
      IPerspectiveSnapshotStore? snapshots, IOptions<PerspectiveSnapshotOptions>? snapshotOptions, IPerspectiveApplyCoordinator? coordinator) {
    var runnerType = typeof(PerspectivePrepareCompleteIntegrationTests).Assembly.GetTypes().Single(t => t.Name == runnerName);
    var logger = typeof(LoggerFactoryExtensions).GetMethods()
      .Single(m => m.Name == "CreateLogger" && m.IsGenericMethod)
      .MakeGenericMethod(runnerType)
      .Invoke(null, [services.GetRequiredService<ILoggerFactory>()])!;
    return (IPerspectiveRunner)runnerType.GetConstructors().Single().Invoke([
      services, logger, eventStore, perspectiveStore, services.GetRequiredService<IServiceScopeFactory>(),
      null, snapshots, snapshotOptions, coordinator, null]);
  }

  private static object _wrap(Func<object, object>? wrapStore, object store) => wrapStore is null ? store : wrapStore(store);

  private static IPerspectiveRunner _actionRunner(
      IEventStore eventStore, WorkCoordinationDbContext context, IPerspectivePurgeMarkerStore? markers,
      Func<object, object>? wrapStore, IPerspectiveApplyCoordinator? coordinator) =>
    _generated("ActionTestPerspectiveRunner", _services<ActionTestPerspective>(markers), eventStore,
      _wrap(wrapStore, new EFCorePostgresPerspectiveStore<ActionTestModel>(context, ACTION_TABLE)), null, null, coordinator);

  private static IPerspectiveRunner _progressRunner(
      IEventStore eventStore, WorkCoordinationDbContext context, IPerspectivePurgeMarkerStore? markers,
      Func<object, object>? wrapStore, IPerspectiveApplyCoordinator? coordinator) =>
    _generated("ItemProgressPerspectiveRunner", _services<ItemProgressPerspective>(markers), eventStore,
      _wrap(wrapStore, new EFCorePostgresPerspectiveStore<ItemProgress>(context, PROGRESS_TABLE)), null, null, coordinator);

  private static IPerspectiveRunner _extractedRunner(
      IEventStore eventStore, WorkCoordinationDbContext context, IPerspectivePurgeMarkerStore? markers,
      Func<object, object>? wrapStore, IPerspectiveApplyCoordinator? coordinator) =>
    _generated("InitOnlyExtractedPerspectiveRunner", _services<InitOnlyExtractedPerspective>(markers), eventStore,
      _wrap(wrapStore, new EFCorePostgresPerspectiveStore<InitOnlyExtractedModel>(context, EXTRACTED_TABLE)), null, null, coordinator);

  private static IPerspectiveRunner _splitRecordRunner(
      IEventStore eventStore, WorkCoordinationDbContext context, IPerspectivePurgeMarkerStore? markers,
      Func<object, object>? wrapStore, IPerspectiveApplyCoordinator? coordinator) =>
    _generated("InitOnlySplitPerspectiveRunner", _services<InitOnlySplitPerspective>(markers), eventStore,
      _wrap(wrapStore, new EFCorePostgresPerspectiveStore<InitOnlySplitModel>(context, SPLIT_RECORD_TABLE)), null, null, coordinator);

  private static IPerspectiveRunner _cappedRunner(
      IEventStore eventStore, WorkCoordinationDbContext context, IPerspectivePurgeMarkerStore? markers,
      Func<object, object>? wrapStore, IPerspectiveApplyCoordinator? coordinator) =>
    _generated("PreparedApplyCappedPerspectiveRunner", _services<PreparedApplyCappedPerspective>(markers), eventStore,
      _wrap(wrapStore, new EFCorePostgresPerspectiveStore<PreparedApplyCappedItem>(context, CAPPED_TABLE)), null, null, coordinator);

  private static RunnerFactory _splitClassRunner(IPerspectiveSnapshotStore snapshots) =>
    (eventStore, context, markers, wrapStore, coordinator) => _generated(
      "SplitClassSnapshotPerspectiveRunner", _services<SplitClassSnapshotPerspective>(markers), eventStore,
      _wrap(wrapStore, new EFCorePostgresPerspectiveStore<SplitClassSnapshotModel>(context, SPLIT_CLASS_TABLE)),
      snapshots,
      Options.Create(new PerspectiveSnapshotOptions { Enabled = true, SnapshotEveryNEvents = 1, MaxSnapshotsPerStream = 10 }),
      coordinator);

  /// <summary>Runs a batch through the per-pair path, on a context of its own, as one worker does.</summary>
  private async Task<PerspectiveCursorCompletion> _perPairAsync(
      RunnerFactory runner, IEventStore eventStore, Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
      IReadOnlyList<MessageEnvelope<IEvent>> events, IPerspectivePurgeMarkerStore? markers = null) {
    await using var context = CreateDbContext();
    return await runner(eventStore, context, markers, null, new PerspectiveApplyCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<PerspectiveApplyCoordinator>.Instance))
      .RunWithEventsAsync(streamId, perspectiveName, lastProcessedEventId, events, CancellationToken.None);
  }

  /// <summary>
  /// Runs a batch through the set-based path as the worker will: the pair's lock held from before the prepare until
  /// after the completion, the prepared write written by the sink on the store's own context.
  /// </summary>
  private async Task<SetBasedRun> _setBasedAsync(
      RunnerFactory runner, IEventStore eventStore, Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
      IReadOnlyList<MessageEnvelope<IEvent>> events, IPerspectivePurgeMarkerStore? markers = null,
      Func<object, object>? wrapStore = null,
      Func<Task>? beforeWrite = null,
      Action<PerspectiveWriteOutcome>? onOutcome = null) {
    await using var context = CreateDbContext();
    var coordinator = new SingleHolderCoordinator();
    var setBasedRunner = runner(eventStore, context, markers, wrapStore, coordinator);
    await using var locks = await PerspectiveApplyLockSet.AcquireAsync(coordinator, [(streamId, perspectiveName)]);

    var prepared = await setBasedRunner.PrepareAsync(streamId, perspectiveName, lastProcessedEventId, events, CancellationToken.None);
    if (beforeWrite is not null) {
      await beforeWrite();
    }
    PerspectiveWriteOutcome? outcome = null;
    if (prepared.Write is { } write) {
      outcome = (await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([write]))[0];
      onOutcome?.Invoke(outcome.Value);
    }
    var completion = await setBasedRunner.CompleteAsync(prepared, outcome, CancellationToken.None);
    return new SetBasedRun(prepared, outcome, completion);
  }

  private static async Task<MessageEnvelope<IEvent>> _appendAsync<TEvent>(
      InMemoryEventStore eventStore, Guid streamId, TEvent payload, int second, Guid? messageId = null) where TEvent : IEvent {
    var envelope = new MessageEnvelope<IEvent> {
      MessageId = MessageId.From(messageId ?? Guid.CreateVersion7()),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [new MessageHop {
        Type = HopType.Current,
        // A fixed business time, so both paths stamp the same metadata, created_at and updated_at.
        Timestamp = new DateTimeOffset(2026, 10, 1, 12, 0, second, TimeSpan.Zero),
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"), ServiceName = "test-service", HostName = "test-host", ProcessId = 1,
        },
        Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-a", UserId = "user-a" }),
      }],
    };
    await eventStore.AppendAsync(streamId, envelope);
    return envelope;
  }

  private static StampingEventStore _stamped(InMemoryEventStore inner, params (MessageEnvelope<IEvent> Envelope, long Stamp)[] stamps) =>
    new(inner, stamps.ToDictionary(s => s.Envelope.MessageId.Value, s => (long?)s.Stamp));

  /// <summary>The envelope a worker's batch carries: the stored event with the commit sequence its claim read.</summary>
  private static MessageEnvelope<IEvent> _copy(MessageEnvelope<IEvent> stored, StampingEventStore eventStore) =>
    new() {
      MessageId = stored.MessageId,
      Payload = stored.Payload,
      DispatchContext = stored.DispatchContext,
      Hops = stored.Hops,
      LocalCommitSequence = eventStore.StampOf(stored.MessageId.Value),
    };

  /// <summary>
  /// The stored row as text, column by column: document, metadata, scope, created_at, updated_at, version, then the
  /// named physical columns. The system clock columns are left out: they are the write's own wall time.
  /// </summary>
  private async Task<string?[]?> _readRowAsync(string table, Guid streamId, params string[] physicalColumns) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var physical = string.Concat(physicalColumns.Select(c => $", \"{c}\"::text"));
    await using var command = new NpgsqlCommand(
      $"SELECT data::text, metadata::text, scope::text, created_at::text, updated_at::text, version::text{physical} FROM {table} WHERE id = @id",
      connection);
    command.Parameters.AddWithValue("id", streamId);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return null;
    }
    var values = new string?[reader.FieldCount];
    for (var i = 0; i < values.Length; i++) {
      values[i] = await reader.IsDBNullAsync(i) ? null : reader.GetString(i);
    }
    return values;
  }

  private async Task<ItemProgress> _progressRowAsync(Guid streamId) {
    await using var verify = CreateDbContext();
    return (await verify.Set<PerspectiveRow<ItemProgress>>().AsNoTracking().SingleAsync(r => r.Id == streamId)).Data;
  }

  private Task _deleteRowAsync(string table, Guid streamId) => _executeAsync($"DELETE FROM {table} WHERE id = @id", streamId);

  private async Task _executeAsync(string sql, Guid streamId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.AddWithValue("id", streamId);
    await command.ExecuteNonQueryAsync();
  }

  private static async Task<string?> _snapshotTextAsync(IPerspectiveSnapshotStore snapshots, Guid streamId, string perspectiveName) {
    var snapshot = await snapshots.GetLatestSnapshotAsync(streamId, perspectiveName);
    return snapshot?.SnapshotData.RootElement.GetRawText();
  }

  private static async Task _assertSameRowAsync(string?[]? expected, string?[]? actual) {
    await Assert.That(expected).IsNotNull();
    await Assert.That(actual).IsNotNull();
    await Assert.That(actual).IsEquivalentTo(expected!, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the document, metadata, scope, business times, version and physical columns must be what the per-pair path stores");
  }

  private static async Task _assertSameCompletionAsync(PerspectiveCursorCompletion expected, PerspectiveCursorCompletion actual) {
    await Assert.That(actual with { ProcessedEventIds = [], DroppedStragglers = [] })
      .IsEqualTo(expected with { ProcessedEventIds = [], DroppedStragglers = [] });
    await Assert.That(actual.ProcessedEventIds).IsEquivalentTo(expected.ProcessedEventIds, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(actual.DroppedStragglers).IsEquivalentTo(expected.DroppedStragglers, TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  /// <summary>
  /// An apply coordinator that refuses a second acquisition of a pair it holds, instead of waiting on it: a runner that
  /// took the lock the caller already holds would deadlock, and here it fails at once.
  /// </summary>
  private sealed class SingleHolderCoordinator : IPerspectiveApplyCoordinator {
    private readonly HashSet<(Guid, string)> _held = [];

    public Task<IAsyncDisposable> AcquireAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      if (!_held.Add((streamId, perspectiveName))) {
        throw new InvalidOperationException(
          $"The apply lock of {perspectiveName} stream {streamId} was taken again while it was held; a runner took a lock its caller holds.");
      }
      return Task.FromResult<IAsyncDisposable>(new Release(_held, (streamId, perspectiveName)));
    }

    private sealed class Release(HashSet<(Guid, string)> held, (Guid, string) pair) : IAsyncDisposable {
      public ValueTask DisposeAsync() {
        held.Remove(pair);
        return ValueTask.CompletedTask;
      }
    }
  }

  /// <summary>
  /// Forwards to the real store, optionally declining to prepare writes (a store written before the set-based write)
  /// or answering the first apply read as unchecked (a store that tracks no row versions).
  /// </summary>
  private sealed class ForwardingStore<TModel>(IPerspectiveStore<TModel> inner) : IPerspectiveStore<TModel> where TModel : class {
    private bool _firstReadDone;
    public bool Prepares { get; init; } = true;
    public bool FirstReadUnchecked { get; init; }

    public Task<PerspectiveApplyRead> ReadForApplyAsync(Guid streamId, CancellationToken cancellationToken = default) {
      if (FirstReadUnchecked && !_firstReadDone) {
        _firstReadDone = true;
        return Task.FromResult(PerspectiveApplyRead.Unchecked);
      }
      return inner.ReadForApplyAsync(streamId, cancellationToken);
    }

    public PendingPerspectiveWrite? PrepareWrite(Guid streamId, TModel model, PerspectiveScope scope, bool forceUpdateScope,
        PerspectiveMetadata metadata, PerspectiveRowVersion expectedVersion, IDictionary<string, object?>? physicalFieldValues) =>
      Prepares ? inner.PrepareWrite(streamId, model, scope, forceUpdateScope, metadata, expectedVersion, physicalFieldValues) : null;

    public PendingPerspectiveWrite? PreparePurge(Guid streamId) => Prepares ? inner.PreparePurge(streamId) : null;

    public Task<TModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      inner.GetByStreamIdAsync(streamId, cancellationToken);
    public Task<PerspectiveMetadata?> GetMetadataByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      inner.GetMetadataByStreamIdAsync(streamId, cancellationToken);
    public Task<bool> TryUpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, bool forceUpdateScope,
        PerspectiveMetadata metadata, PerspectiveRowVersion expectedVersion, CancellationToken cancellationToken = default) =>
      inner.TryUpsertAsync(streamId, model, scope, forceUpdateScope, metadata, expectedVersion, cancellationToken);
    public Task UpsertAsync(Guid streamId, TModel model, CancellationToken cancellationToken = default) =>
      inner.UpsertAsync(streamId, model, cancellationToken);
    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues,
        PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
      inner.UpsertWithPhysicalFieldsAsync(streamId, model, physicalFieldValues, scope, cancellationToken);
    public Task<TModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => inner.GetByPartitionKeyAsync(partitionKey, cancellationToken);
    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => inner.UpsertByPartitionKeyAsync(partitionKey, model, cancellationToken);
    public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => inner.PurgeAsync(streamId, cancellationToken);
    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => inner.PurgeByPartitionKeyAsync(partitionKey, cancellationToken);
  }
}
