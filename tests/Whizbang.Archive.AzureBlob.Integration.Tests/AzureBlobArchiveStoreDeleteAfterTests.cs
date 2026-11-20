// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Archive.AzureBlob.Integration.Tests;

/// <summary>
/// #2367: the optional deletion delay (<see cref="AzureBlobArchiveOptions.DeleteAfter"/>) for storage that keeps
/// nothing after a delete, so a database restored to any point within the delay still finds every object it names, and
/// the orphan sweep that runs only with the delay on. Every age is measured on a clock the test sets from the database's
/// and the storage's own timestamps, never on the wall clock.
/// </summary>
/// <docs>operations/deployment/restoring-an-environment-with-an-archive#a-deletion-delay</docs>
public partial class AzureBlobArchiveStoreTests {
  private static readonly TimeSpan _delay = TimeSpan.FromDays(1);
  private static readonly TimeSpan _microsecond = TimeSpan.FromMicroseconds(1);

  // Long after every object and queued row of a test: storage and the database stamp both with their own clocks.
  private static readonly DateTimeOffset _farFuture = new(2999, 1, 1, 0, 0, 0, TimeSpan.Zero);

  // ── The deletion delay ─────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task DeleteAfter_ARestoreAndADelete_LeaveTheirObjects_UntilTheDelayHasPassedAsync() {
    var (restored, _) = await _seedAsync(3);
    var (deleted, _) = await _seedAsync(3);
    var clock = new ManualClock(_farFuture);
    var store = _delayedStore(clock);
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(restored));
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(deleted));
    var objects = await _objectsAsync();
    clock.Now = await _databaseNowAsync();

    _ = await store.RestoreAsync(restored);
    _ = await store.DeleteAsync(deleted);
    var queuedAt = await _queuedAtAsync();
    clock.Now = queuedAt.Min() + _delay - _microsecond;
    await new ArchiveObjectDeletionStep(store).RunAsync(_noServices, CancellationToken.None);
    var beforeTheDelay = await _objectsAsync();
    clock.Now = queuedAt.Max() + _delay;
    await new ArchiveObjectDeletionStep(store).RunAsync(_noServices, CancellationToken.None);

    await Assert.That(objects).Count().IsEqualTo(4);
    await Assert.That(queuedAt).Count().IsEqualTo(4).Because("the restore and the delete queued every object they stopped naming");
    await Assert.That(beforeTheDelay).IsEquivalentTo(objects)
      .Because("neither the restore's nor the delete's own drain, nor the step, deletes an object queued less than the delay ago");
    await Assert.That(await _objectsAsync()).IsEmpty().Because("once the delay has passed, the step deletes them");
    await Assert.That(await _queuedAtAsync()).IsEmpty();
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task DeleteAfter_ADatabaseRestoredToAnEarlierCopy_ReadsEveryStream_OnlyWithTheDelayOnAsync(bool delayOn) {
    var (stream, _) = await _seedAsync(5);
    var hot = await _hotAsReadAsync(stream);
    var clock = new ManualClock(_farFuture);
    var store = delayOn ? _delayedStore(clock) : _store(maxEventsPerChunk: 2);
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));
    // The point in time the database is restored to: the archive is written, nothing is deleted yet.
    var earlier = await _copyDatabaseAsync();
    try {
      clock.Now = await _databaseNowAsync();
      _ = await store.DeleteAsync(stream);
      clock.Now = (await _queuedAtAsync()).DefaultIfEmpty(_farFuture).Min() + _delay - _microsecond;
      await new ArchiveObjectDeletionStep(store).RunAsync(_noServices, CancellationToken.None);
      var restoredDatabase = _store(maxEventsPerChunk: 2, database: _databaseAt(earlier.ConnectionString));

      if (delayOn) {
        await Assert.That(await _readAllAsync(restoredDatabase, stream)).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching)
          .Because("within the delay, every object the restored database names is still in storage");
      } else {
        await Assert.That(() => _completedAsync(_readAllAsync(restoredDatabase, stream))).Throws<RequestFailedException>()
          .Because("the control: deleted at once, as before, the restored database names objects storage no longer has");
      }
    } finally {
      await PerTestDatabaseFactory.DropAsync(earlier.Name);
    }
  }

  // ── The orphan sweep ───────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Sweep_WithoutADelay_DoesNothing_AndNeverListsTheContainerAsync() {
    var recorder = new RecordRequests();
    var (stream, _) = await _seedAsync(3);
    var crash = new FaultObjectWrites(afterSending: true);
    var store = _recordedStore(recorder, crash);
    await Assert.That(() => _completedAsync(store.WriteAsync(ArchiveBatch.WholeStream(stream)))).Throws<RequestFailedException>();
    var orphaned = await _objectsAsync();

    var swept = await store.SweepOrphanedObjectsAsync();
    await new ArchiveObjectDeletionStep(store).RunAsync(_noServices, CancellationToken.None);

    await Assert.That(swept).IsEqualTo(0);
    await Assert.That(recorder.Kinds).DoesNotContain(RecordRequests.LIST_OBJECTS)
      .Because("without a delay nothing lists the container; the queue alone drives deletions, as before");
    await Assert.That(orphaned).Count().IsEqualTo(1);
    await Assert.That(await _objectsAsync()).IsEquivalentTo(orphaned);
  }

  [Test]
  public async Task Sweep_DeletesOnlyObjectsNothingNames_OnceTheyAreOlderThanTheDelayAsync() {
    var (named, _) = await _seedAsync(3);
    var (queued, _) = await _seedAsync(3);
    var (orphan, _) = await _seedAsync(3);
    var clock = new ManualClock(_farFuture);
    var crash = new FaultObjectWrites(afterSending: true);
    var store = _delayedStore(clock);
    var crashing = _recordedStore(new RecordRequests(), crash, new AzureBlobArchiveOptions { MaxEventsPerChunk = 10, DeleteAfter = _delay }, clock);
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(named));
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(queued));
    clock.Now = await _databaseNowAsync();
    _ = await store.RestoreAsync(queued);
    await Assert.That(() => _completedAsync(crashing.WriteAsync(ArchiveBatch.WholeStream(orphan)))).Throws<RequestFailedException>();
    // Not an object this store writes: never touched, however old.
    _ = await _container().GetBlobClient("notes/readme.txt").UploadAsync(BinaryData.FromString("operator notes"));
    var created = await _createdAsync();
    var everything = created.Keys.ToList();
    var orphanName = everything.Single(n => n.StartsWith(orphan.ToString("D"), StringComparison.Ordinal));

    clock.Now = created[orphanName] + _delay - _microsecond;
    var young = await store.SweepOrphanedObjectsAsync();
    var afterYoung = await _objectsAsync();
    clock.Now = created.Values.Max() + _delay;
    var old = await store.SweepOrphanedObjectsAsync();

    await Assert.That(young).IsEqualTo(0).Because("an object younger than the delay may be an upload whose commit is still coming");
    await Assert.That(afterYoung).IsEquivalentTo(everything);
    await Assert.That(old).IsEqualTo(1);
    await Assert.That(await _objectsAsync()).IsEquivalentTo(everything.Where(n => n != orphanName).ToList())
      .Because("only the object no manifest row and no queued deletion names goes; the named, the queued and the foreign stay");
    await Assert.That(await _readAllAsync(store, named)).Count().IsEqualTo(3);
    await Assert.That(everything.Count(n => n.StartsWith(queued.ToString("D"), StringComparison.Ordinal))).IsEqualTo(2);
    await Assert.That(await _queuedAsync(queued)).Count().IsEqualTo(2).Because("a queued object is the queue's to delete, after its delay");
  }

  [Test]
  public async Task Sweep_APageWithNoObjectOldEnough_DeletesNothingAsync() {
    var (stream, _) = await _seedAsync(3);
    var clock = new ManualClock(_farFuture);
    var store = _delayedStore(clock);
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));
    // Not an object this store writes, so it never counts, however old.
    _ = await _container().GetBlobClient("notes/readme.txt").UploadAsync(BinaryData.FromString("operator notes"));
    var created = await _createdAsync();

    clock.Now = created.Values.Min() + _delay - _microsecond;
    var swept = await store.SweepOrphanedObjectsAsync();

    await Assert.That(swept).IsEqualTo(0)
      .Because("a page whose objects are all younger than the delay, or not this store's, has nothing to check or delete");
    await Assert.That(await _objectsAsync()).IsEquivalentTo(created.Keys.ToList());
  }

  [Test]
  public async Task Sweep_InADatabaseThatNeverWroteToTheLocation_DeletesNothingAsync() {
    var (stream, _) = await _seedAsync(3);
    _ = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));
    var elsewhere = $"archive-{Guid.NewGuid():N}";
    await _copyObjectsAsync(_containerName, elsewhere);
    var copied = await _objectsAsync(elsewhere);
    var store = new AzureBlobArchiveStore(_database(), _container(elsewhere),
      new AzureBlobArchiveOptions { ContainerName = elsewhere, DeleteAfter = _delay }, logger: null, new ManualClock(_farFuture));

    var swept = await store.SweepOrphanedObjectsAsync();

    await Assert.That(swept).IsEqualTo(0);
    await Assert.That(copied).IsNotEmpty();
    await Assert.That(await _objectsAsync(elsewhere)).IsEquivalentTo(copied)
      .Because("a database that never knew this location names nothing in it, so a sweep would empty it: it deletes nothing");
  }

  [Test]
  public async Task Sweep_AnObjectWhoseStreamIsBeingWritten_IsLeftForALaterSweepAsync() {
    var (orphan, _) = await _seedAsync(3);
    var clock = new ManualClock(_farFuture);
    var crash = new FaultObjectWrites(afterSending: true);
    var store = _recordedStore(new RecordRequests(), crash, new AzureBlobArchiveOptions { MaxEventsPerChunk = 10, DeleteAfter = _delay }, clock);
    await _archiveAnotherStreamAsync();
    await Assert.That(() => _completedAsync(store.WriteAsync(ArchiveBatch.WholeStream(orphan)))).Throws<RequestFailedException>();

    int whileLocked;
    await using (var connection = await _openAsync()) {
      await using var transaction = await connection.BeginTransactionAsync();
      await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@k))", connection, transaction)) {
        command.Parameters.AddWithValue("k", $"wh_event_store:{orphan:D}");
        _ = await command.ExecuteNonQueryAsync();
      }
      whileLocked = await store.SweepOrphanedObjectsAsync();
    }
    var afterwards = await store.SweepOrphanedObjectsAsync();

    await Assert.That(whileLocked).IsEqualTo(0).Because("a write holds the stream's lock until its manifest row commits");
    await Assert.That(afterwards).IsEqualTo(1);
    await Assert.That(await _objectsAsync()).DoesNotContain(n => n.StartsWith(orphan.ToString("D"), StringComparison.Ordinal));
  }

  [Test]
  public async Task Sweep_AnOrphanThatCannotBeDeleted_IsLogged_AndLeftForTheNextSweepAsync() {
    var (orphan, _) = await _seedAsync(3);
    var clock = new ManualClock(_farFuture);
    var options = new AzureBlobArchiveOptions { MaxEventsPerChunk = 10, DeleteAfter = _delay };
    var crashing = _recordedStore(new RecordRequests(), new FaultObjectWrites(afterSending: true), options, clock);
    await _archiveAnotherStreamAsync();
    await Assert.That(() => _completedAsync(crashing.WriteAsync(ArchiveBatch.WholeStream(orphan)))).Throws<RequestFailedException>();
    var logger = new ListLogger();
    var failing = new AzureBlobArchiveStore(_database(), _container(fault: new FaultObjectDeletes()),
      new AzureBlobArchiveOptions { ContainerName = _containerName, DeleteAfter = _delay }, logger, clock);

    var swept = await failing.SweepOrphanedObjectsAsync();
    var healthy = await _delayedStore(clock).SweepOrphanedObjectsAsync();

    await Assert.That(swept).IsEqualTo(0);
    await Assert.That(logger.Messages).Contains(m => m.Level == Microsoft.Extensions.Logging.LogLevel.Warning
      && m.Text.Contains(orphan.ToString("D"), StringComparison.Ordinal))
      .Because("a delete that failed is logged with the object it could not remove, never swallowed");
    await Assert.That(healthy).IsEqualTo(1).Because("nothing was recorded against the object, so the next sweep deletes it");
    await Assert.That(await _objectsAsync()).DoesNotContain(n => n.StartsWith(orphan.ToString("D"), StringComparison.Ordinal));
  }

  [Test]
  public async Task Step_SweepsAtMostOncePerInterval_AndOnlyWithADelayAsync() {
    var (first, _) = await _seedAsync(3);
    var (second, _) = await _seedAsync(3);
    var recorder = new RecordRequests();
    var clock = new ManualClock(_farFuture);
    var crash = new FaultObjectWrites(afterSending: true);
    var store = _recordedStore(recorder, crash, new AzureBlobArchiveOptions { MaxEventsPerChunk = 10, DeleteAfter = _delay }, clock);
    var step = new ArchiveObjectDeletionStep(store);
    var named = await _archiveAnotherStreamAsync();

    await Assert.That(() => _completedAsync(store.WriteAsync(ArchiveBatch.WholeStream(first)))).Throws<RequestFailedException>();
    await step.RunAsync(_noServices, CancellationToken.None);
    var afterFirst = await _objectsAsync();
    await Assert.That(() => _completedAsync(store.WriteAsync(ArchiveBatch.WholeStream(second)))).Throws<RequestFailedException>();
    clock.Now += AzureBlobArchiveStore.OrphanSweepInterval - _microsecond;
    await step.RunAsync(_noServices, CancellationToken.None);
    var withinTheInterval = await _objectsAsync();
    clock.Now += _microsecond;
    await step.RunAsync(_noServices, CancellationToken.None);

    await Assert.That(afterFirst).IsEquivalentTo(named).Because("the first cycle with a delay sweeps");
    await Assert.That(withinTheInterval).Count().IsEqualTo(named.Count + 1).Because("the next sweep waits for the interval");
    await Assert.That(await _objectsAsync()).IsEquivalentTo(named).Because("and runs once it has passed");
    await Assert.That(recorder.Kinds.Count(k => k == RecordRequests.LIST_OBJECTS)).IsEqualTo(2);
  }

  // ── Arrange ────────────────────────────────────────────────────────────────────────────────────────

  private AzureBlobArchiveStore _delayedStore(TimeProvider clock) =>
    new(_database(), _container(), new AzureBlobArchiveOptions { MaxEventsPerChunk = 2, ContainerName = _containerName, DeleteAfter = _delay },
      logger: null, clock);

  // The database's own clock now: a delay measured from here has not passed for anything queued from here on.
  private async Task<DateTimeOffset> _databaseNowAsync() {
    await using var connection = await _openAsync();
    await using var command = new NpgsqlCommand("SELECT NOW()", connection);
    await using var reader = await command.ExecuteReaderAsync();
    _ = await reader.ReadAsync();
    return reader.GetFieldValue<DateTimeOffset>(0);
  }

  // A committed archive of another stream: the database now knows the configured location, and its objects are named.
  private async Task<List<string>> _archiveAnotherStreamAsync() {
    var (stream, _) = await _seedAsync(3);
    _ = await _store(maxEventsPerChunk: 10).WriteAsync(ArchiveBatch.WholeStream(stream));
    return await _objectsAsync();
  }

  private static PostgresArchiveConnection _databaseAt(string connectionString) => new(async ct => {
    var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);
    return connection;
  }, Schema: null);

  // When the database queued each object deletion, on its own clock.
  private async Task<List<DateTimeOffset>> _queuedAtAsync() {
    await using var connection = await _openAsync();
    await using var command = new NpgsqlCommand("SELECT queued_at FROM wh_archive_object_deletions", connection);
    var times = new List<DateTimeOffset>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      times.Add(reader.GetFieldValue<DateTimeOffset>(0));
    }
    return times;
  }

  // When storage created each object, as the listing the sweep reads reports it.
  private async Task<Dictionary<string, DateTimeOffset>> _createdAsync() {
    var created = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    await foreach (var item in _container().GetBlobsAsync()) {
      if (item.Name != AzureBlobArchiveStore.OWNER_MARKER_NAME) {
        created[item.Name] = item.Properties.CreatedOn!.Value;
      }
    }
    return created;
  }

  // An exact copy of the test's database as it is now: a database restored to this point in time.
  private async Task<PerTestDatabase> _copyDatabaseAsync() {
    var name = $"{_db.Name[..Math.Min(_db.Name.Length, 40)]}_pitr";
    NpgsqlConnection.ClearAllPools();
    await using var admin = new NpgsqlConnection(PerTestDatabaseFactory.ForSetup(SharedPostgresContainer.ConnectionString));
    await admin.OpenAsync();
    await using (var terminate = new NpgsqlCommand(
        "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @d AND pid <> pg_backend_pid()", admin)) {
      terminate.Parameters.AddWithValue("d", _db.Name);
      _ = await terminate.ExecuteNonQueryAsync();
    }
    await using (var create = new NpgsqlCommand($"CREATE DATABASE {name} TEMPLATE {_db.Name}", admin)) {
      _ = await create.ExecuteNonQueryAsync();
    }
    var builder = new NpgsqlConnectionStringBuilder(_db.ConnectionString) { Database = name };
    return new PerTestDatabase(name, builder.ConnectionString);
  }

  /// <summary>A clock the test sets: every age the store measures is measured on it.</summary>
  private sealed class ManualClock(DateTimeOffset now) : TimeProvider {
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
  }
}
