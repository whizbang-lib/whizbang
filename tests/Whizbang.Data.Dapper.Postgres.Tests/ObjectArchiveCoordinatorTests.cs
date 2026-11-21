// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Fault = Whizbang.Data.Dapper.Postgres.Tests.InMemoryArchiveObjectStore.Fault;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Postgres archive coordinator (#2503) against an in-memory object store: the crash-safety guarantees hold whatever
/// provider moves the objects. A put that fails leaves the stream fully hot; a crash after the put and before the commit
/// is retried without a duplicate; an object is never replaced; a stream never spans two locations; a corrupted object is
/// never decoded; restores and deletes queue their objects and the queue survives a failed delete; the orphan sweep
/// deletes only what nothing names and refuses a location it cannot prove this database's. The Azure Blob twins of these
/// scenarios run against Azurite in <c>Whizbang.Storage.AzureBlob.Integration.Tests</c>.
/// </summary>
/// <docs>fundamentals/events/archive-store#object-stores-and-the-coordinator</docs>
public class ObjectArchiveCoordinatorTests : PostgresTestBase {
  private static readonly KeyValuePair<string, string>[] _noEntries = [];

  // ── Arrange ────────────────────────────────────────────────────────────────────────────────────────

  private PostgresArchiveConnection _database() => new(async ct => {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(ct);
    return connection;
  }, Schema: null);

  private PostgresObjectArchiveStore _coordinator(InMemoryArchiveObjectStore objects, TimeProvider? clock = null, ArchiveSweepMetrics? metrics = null) =>
    new(_database(), objects, logger: null, clock, metrics);

  private static InMemoryArchiveObjectStore _objects(int maxEventsPerChunk = 2, TimeSpan? deleteAfter = null, string container = "archive",
      bool verifyLocation = true, TimeProvider? clock = null) =>
    new(new ArchiveObjectStoreSettings { MaxEventsPerChunk = maxEventsPerChunk, DeleteAfter = deleteAfter, VerifyLocation = verifyLocation },
      container, clock: clock);

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  // Events through the emit chain (versions 1, 2, 3...), with commit positions stamped.
  private async Task<Guid> _seedAsync(int count) {
    await using var connection = await _openAsync();
    var stream = Guid.CreateVersion7();
    var ids = Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).Order().ToArray();
    foreach (var id in ids) {
      var request = $$"""
        {
          "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
          "new_outbox_messages": [{
            "MessageId": "{{id}}", "Destination": "out", "MessageType": "Whizbang.Tests.LedgerEntry", "EnvelopeType": null,
            "Envelope": {"Payload": {"OrderId": 42, "Note": "zßé \"q\" a\nb"}, "MessageId": "{{id}}", "Hops": []},
            "Metadata": {"z": 1, "a": "two"}, "Scope": {"t": "tenant-1"}, "StreamId": "{{stream}}", "IsEvent": true, "Flags": 0
          }]
        }
        """;
      await connection.ExecuteAsync("SELECT commit_handler_result(@Req::jsonb)", new { Req = request });
    }
    await connection.ExecuteAsync("SELECT stamp_pending_commit_sequences(1000)");
    return stream;
  }

  private async Task<List<string>> _hotAsync(Guid stream) {
    await using var connection = await _openAsync();
    return [.. await connection.QueryAsync<string>(
      "SELECT concat_ws('|', es.event_id, es.version, es.event_type, eb.event_data::text, eb.metadata::text, es.scope::text) " +
      "FROM wh_event_store es JOIN wh_event_body eb ON eb.event_id = es.event_id WHERE es.stream_id = @stream ORDER BY es.version",
      new { stream })];
  }

  private static async Task<List<string>> _readAsync(PostgresObjectArchiveStore store, Guid stream, long fromVersion = 0) {
    var read = new List<string>();
    await foreach (var e in store.ReadAsync(stream, fromVersion)) {
      read.Add(string.Join('|', e.EventId, e.Version, e.EventType, e.EventDataJson, e.MetadataJson, e.ScopeJson));
    }
    return read;
  }

  private async Task<long> _countAsync(string sql, Guid stream) {
    await using var connection = await _openAsync();
    return await connection.ExecuteScalarAsync<long>(sql, new { stream });
  }

  private async Task<List<string>> _manifestAsync(Guid stream) {
    await using var connection = await _openAsync();
    return [.. await connection.QueryAsync<string>(
      "SELECT concat_ws('|', first_version, last_version, event_count, relative_path) FROM wh_archive_manifest " +
      "WHERE stream_id = @stream ORDER BY first_version", new { stream })];
  }

  private async Task<string?> _generationAsync() {
    await using var connection = await _openAsync();
    return await connection.ExecuteScalarAsync<string?>(
      "SELECT setting_value FROM wh_settings WHERE setting_key = 'integrity_origin_generation'");
  }

  private async Task<string?> _tombstoneAsync(Guid stream) {
    await using var connection = await _openAsync();
    return await connection.ExecuteScalarAsync<string?>(
      "SELECT concat_ws('|', state, COALESCE(archive_locator, '<none>'), last_version) FROM wh_stream_lifecycle WHERE stream_id = @stream",
      new { stream });
  }

  private async Task<List<string>> _queuedAsync(Guid stream) {
    await using var connection = await _openAsync();
    return [.. await connection.QueryAsync<string>(
      "SELECT concat_ws('|', relative_path, attempts) FROM wh_archive_object_deletions WHERE stream_id = @stream ORDER BY relative_path",
      new { stream })];
  }

  private async Task _dueNowAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync("UPDATE wh_archive_object_deletions SET next_attempt_at = NOW() - interval '1 second'");
  }

  private static IReadOnlyList<string> _archiveObjects(InMemoryArchiveObjectStore objects) =>
    [.. objects.Paths.Where(p => p != PostgresObjectArchiveStore.OWNER_MARKER_NAME)];

  // Awaits a call for its exception only, so an assertion can take it as a plain task.
  private static async Task _completedAsync<T>(Task<T> call) => _ = await call;

  // ── Write and read ─────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Write_WholeStream_ThenRead_IsByteIdentical_InChunks_AndTheTombstoneNamesTheProviderAsync() {
    var stream = await _seedAsync(5);
    var hot = await _hotAsync(stream);
    var objects = _objects(maxEventsPerChunk: 2);
    var coordinator = _coordinator(objects);

    var written = await coordinator.WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(written.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(written.EventsArchived).IsEqualTo(5L);
    await Assert.That(written.Locator!.ToString()).IsEqualTo($"memory://{stream:D}?codec=brotli&format=1")
      .Because("the locator names the object store's provider, so Azure Blob archives keep their azure-blob locator");
    await Assert.That(await _readAsync(coordinator, stream)).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await _readAsync(coordinator, stream, fromVersion: 4)).IsEquivalentTo(hot[3..], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That((await _manifestAsync(stream)).Select(m => string.Join('|', m.Split('|')[..3])))
      .IsEquivalentTo(["1|2|2", "3|4|2", "5|5|1"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(_archiveObjects(objects)).Count().IsEqualTo(3);
    await Assert.That(objects.Paths).Contains(PostgresObjectArchiveStore.OWNER_MARKER_NAME)
      .Because("the first write claims the location for this database");
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_event_archive WHERE stream_id = @stream", stream)).IsEqualTo(0L);
    await Assert.That(await _tombstoneAsync(stream)).IsEqualTo($"Archived|memory://{stream:D}?codec=brotli&format=1|5");
    await Assert.That(objects.Requests).Contains("prepare").Because("the location is prepared before the first object is put");
  }

  [Test]
  public async Task Write_ThePutFails_TheStreamStaysFullyHot_AndNothingIsListedAsync() {
    var stream = await _seedAsync(3);
    var hot = await _hotAsync(stream);
    var tombstone = await _tombstoneAsync(stream);
    var generation = await _generationAsync();
    var objects = _objects();
    objects.Faults = static (operation, path) => operation == "put" && path!.Contains('/', StringComparison.Ordinal) ? Fault.Before : Fault.None;

    await Assert.That(() => _completedAsync(_coordinator(objects).WriteAsync(ArchiveBatch.WholeStream(stream)))).Throws<ArchiveObjectStoreException>();

    await Assert.That(objects.Faulted).IsEqualTo(1).Because("the failure comes from the put, after the database moved the events");
    await Assert.That(await _hotAsync(stream)).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the transaction never committed, so every event is hot as it was");
    await Assert.That(await _tombstoneAsync(stream)).IsEqualTo(tombstone);
    await Assert.That(await _generationAsync()).IsEqualTo(generation);
    await Assert.That(await _manifestAsync(stream)).IsEmpty();
    await Assert.That(_archiveObjects(objects)).IsEmpty();
  }

  [Test]
  public async Task Write_ACrashAfterThePutBeforeTheCommit_IsRetriedWithoutADuplicateAsync() {
    var stream = await _seedAsync(3);
    var hot = await _hotAsync(stream);
    var objects = _objects(maxEventsPerChunk: 10);
    var armed = true;
    objects.Faults = (operation, path) => armed && operation == "put" && path!.Contains('/', StringComparison.Ordinal) ? Fault.After : Fault.None;
    var coordinator = _coordinator(objects);

    await Assert.That(() => _completedAsync(coordinator.WriteAsync(ArchiveBatch.WholeStream(stream)))).Throws<ArchiveObjectStoreException>();
    var orphaned = _archiveObjects(objects);
    await Assert.That(orphaned).Count().IsEqualTo(1).Because("the object reached the store; only the coordinator never heard");
    await Assert.That(await _manifestAsync(stream)).IsEmpty();
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_event_store WHERE stream_id = @stream", stream)).IsEqualTo(3L);

    armed = false;
    var retried = await coordinator.WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(retried.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(_archiveObjects(objects)).IsEquivalentTo(orphaned).Because("the retry finds the same bytes under the same name");
    await Assert.That(await _manifestAsync(stream)).Count().IsEqualTo(1);
    await Assert.That(await _readAsync(coordinator, stream)).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Write_NeverReplacesAnObject_WithDifferentBytesUnderItsName_TheStreamStaysHotAsync() {
    var stream = await _seedAsync(2);
    var objects = _objects(maxEventsPerChunk: 10);
    var coordinator = _coordinator(objects);
    _ = await coordinator.WriteAsync(ArchiveBatch.WholeStream(stream));
    var name = _archiveObjects(objects).Single();
    _ = await coordinator.RestoreAsync(stream);
    objects.Seed(name, [1, 2, 3]);

    await Assert.That(() => _completedAsync(coordinator.WriteAsync(ArchiveBatch.WholeStream(stream)))).Throws<ArchiveObjectCorruptedException>();

    await Assert.That(await _countAsync("SELECT count(*) FROM wh_event_store WHERE stream_id = @stream", stream)).IsEqualTo(2L)
      .Because("an object is written once and never replaced, so the write is refused and the stream stays hot");
    await Assert.That(await _manifestAsync(stream)).IsEmpty();
  }

  [Test]
  public async Task Read_ACorruptedObject_IsDetected_AndNothingIsDecodedAsync() {
    var stream = await _seedAsync(2);
    var objects = _objects(maxEventsPerChunk: 10);
    var coordinator = _coordinator(objects);
    _ = await coordinator.WriteAsync(ArchiveBatch.WholeStream(stream));
    objects.Corrupt(_archiveObjects(objects).Single(), [0x1b, 0x00]);

    await Assert.That(() => _completedAsync(_readAsync(coordinator, stream))).Throws<ArchiveObjectCorruptedException>();
    await Assert.That(() => _completedAsync(coordinator.RestoreAsync(stream))).Throws<ArchiveObjectCorruptedException>();
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_archive_manifest WHERE stream_id = @stream", stream)).IsEqualTo(1L)
      .Because("a restore of a corrupted archive changes nothing");
  }

  [Test]
  public async Task Write_OfAStreamWhoseObjectsAreInAnotherLocation_IsRefused_UntilRepointedAsync() {
    var stream = await _seedAsync(4);
    var source = _objects(container: "archive-source");
    _ = await _coordinator(source).WriteAsync(ArchiveBatch.Prefix(stream, 2));
    var copy = _objects(container: "archive-copy");
    var moved = _coordinator(copy);

    var refused = await Assert.That(() => _completedAsync(moved.WriteAsync(ArchiveBatch.WholeStream(stream))))
      .Throws<ArchiveLocationMismatchException>();
    await Assert.That(() => _completedAsync(_readAsync(moved, stream))).Throws<ArchiveLocationMismatchException>();
    await Assert.That(copy.Requests).IsEmpty().Because("the refusal comes before any request reaches storage");

    foreach (var path in source.Paths) {
      copy.Seed(path, await source.GetAsync(path));
    }
    var location = await moved.RepointLocationAsync(refused!.RecordedLocationId!.Value);
    var written = await moved.WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(location).IsGreaterThan(0L);
    await Assert.That(written.Status).IsEqualTo(ArchiveStatus.ARCHIVED).Because("once repointed, the stream's records name this location");
    await Assert.That(await _readAsync(moved, stream)).Count().IsEqualTo(4);
  }

  [Test]
  public async Task VerifyLocationOff_AMismatchedRecord_IsRead_InTheConfiguredLocationOnlyAsync() {
    var stream = await _seedAsync(2);
    var source = _objects(maxEventsPerChunk: 10, container: "archive-source");
    _ = await _coordinator(source).WriteAsync(ArchiveBatch.WholeStream(stream));
    var copy = _objects(maxEventsPerChunk: 10, container: "archive-copy", verifyLocation: false);
    foreach (var path in source.Paths) {
      copy.Seed(path, await source.GetAsync(path));
    }

    await Assert.That(await _readAsync(_coordinator(copy), stream)).Count().IsEqualTo(2)
      .Because("with the check off the configured location is used without comparison");
  }

  // ── Restore, delete and the deletion queue ─────────────────────────────────────────────────────────

  [Test]
  public async Task Restore_PutsTheStreamBack_AndDeletesItsObjects_ADeleteThatFails_StaysQueuedAsync() {
    var stream = await _seedAsync(3);
    var hot = await _hotAsync(stream);
    var objects = _objects();
    var coordinator = _coordinator(objects);
    _ = await coordinator.WriteAsync(ArchiveBatch.WholeStream(stream));
    var failing = true;
    objects.Faults = (operation, _) => failing && operation == "delete" ? Fault.Before : Fault.None;

    var restored = await coordinator.RestoreAsync(stream);

    await Assert.That(restored.Status).IsEqualTo(ArchiveStatus.RESTORED);
    await Assert.That(await _hotAsync(stream)).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That((await _queuedAsync(stream)).Select(q => q.Split('|')[1])).IsEquivalentTo(["1", "1"])
      .Because("a delete storage refused stays queued, its attempt counted, for the maintenance step");
    await Assert.That(_archiveObjects(objects)).Count().IsEqualTo(2);

    failing = false;
    await _dueNowAsync();
    var deleted = await coordinator.DeleteQueuedObjectsAsync(ArchiveObjectDeletionStep.BATCH_SIZE);

    await Assert.That(deleted).IsEqualTo(2);
    await Assert.That(await _queuedAsync(stream)).IsEmpty();
    await Assert.That(_archiveObjects(objects)).IsEmpty();
  }

  [Test]
  public async Task Delete_RemovesTheManifestAndTheObjects_TheTombstoneStaysAsync() {
    var stream = await _seedAsync(3);
    var objects = _objects();
    var coordinator = _coordinator(objects);
    _ = await coordinator.WriteAsync(ArchiveBatch.WholeStream(stream));

    var deleted = await coordinator.DeleteAsync(stream);

    await Assert.That(deleted).IsEqualTo(3L);
    await Assert.That(await _manifestAsync(stream)).IsEmpty();
    await Assert.That(_archiveObjects(objects)).IsEmpty();
    await Assert.That(await _tombstoneAsync(stream)).StartsWith("Archived|");
  }

  // ── The orphan sweep ───────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Sweep_WithADelay_DeletesOnlyOldObjectsNothingNames_InBatchesAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var stream = await _seedAsync(2);
    var objects = _objects(deleteAfter: TimeSpan.FromDays(7), clock: clock);
    var coordinator = new PostgresObjectArchiveStore(_database(), objects, timeProvider: clock) { SweepBatchSize = 2 };
    _ = await coordinator.WriteAsync(ArchiveBatch.WholeStream(stream));
    var orphan = Guid.CreateVersion7();
    var old = clock.GetUtcNow() - TimeSpan.FromDays(8);
    objects.Seed($"{orphan:D}/0000000001-0000000001-aa.jsonl.br", [1], old);
    objects.Seed($"{orphan:D}/0000000002-0000000002-bb.jsonl.br", [2], old);
    objects.Seed("not-an-archive-object.txt", [4], old);
    clock.Advance(TimeSpan.FromDays(8));
    objects.Seed($"{orphan:D}/0000000003-0000000003-cc.jsonl.br", [3], clock.GetUtcNow());

    var swept = await coordinator.SweepOrphanedObjectsAsync();

    await Assert.That(swept).IsEqualTo(2).Because("only objects older than the delay that no record names are deleted");
    await Assert.That(objects.Paths).Contains($"{orphan:D}/0000000003-0000000003-cc.jsonl.br")
      .And.Contains("not-an-archive-object.txt");
    await Assert.That(await _readAsync(coordinator, stream)).Count().IsEqualTo(2).Because("named objects survive every sweep");
  }

  [Test]
  public async Task Sweep_WithoutADelay_NeverLists_AndTheStepSweepsAtMostOncePerIntervalAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var stream = await _seedAsync(1);
    var undelayed = _objects();
    var delayed = _objects(deleteAfter: TimeSpan.FromDays(1), container: "delayed", clock: clock);
    var withDelay = _coordinator(delayed, clock);
    _ = await withDelay.WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(await _coordinator(undelayed).SweepOrphanedObjectsAsync()).IsEqualTo(0);
    await Assert.That(await _coordinator(undelayed).SweepOrphanedObjectsIfDueAsync()).IsEqualTo(0);
    _ = await withDelay.SweepOrphanedObjectsIfDueAsync();
    _ = await withDelay.SweepOrphanedObjectsIfDueAsync();
    clock.Advance(PostgresObjectArchiveStore.OrphanSweepInterval);
    _ = await withDelay.SweepOrphanedObjectsIfDueAsync();

    await Assert.That(undelayed.Requests).DoesNotContain("list").Because("without a delay nothing lists the location");
    await Assert.That(delayed.Requests.Count(r => r == "list")).IsEqualTo(2)
      .Because("the step sweeps at most once per interval in a process");
  }

  [Test]
  public async Task Sweep_Refuses_AnUnknownLocation_AForeignMarker_AndAMarkerItCannotClaimOrRead_CountingEachAsync() {
    var metrics = new ArchiveSweepMetrics(new WhizbangMetrics(
      new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
    var refused = new Dictionary<string, long>(StringComparer.Ordinal);
    using var listener = new System.Diagnostics.Metrics.MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument == metrics.SweepsRefused.Instrument) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      foreach (var tag in tags) {
        if (tag.Key == "reason" && tag.Value is string reason) {
          refused[reason] = value;
        }
      }
    });
    listener.Start();
    var delay = TimeSpan.FromDays(1);

    var unknown = await _coordinator(_objects(deleteAfter: delay, container: "never-written"), metrics: metrics).SweepOrphanedObjectsAsync();
    var stream = await _seedAsync(1);
    var foreignObjects = _objects(deleteAfter: delay, container: "foreign");
    foreignObjects.Seed(PostgresObjectArchiveStore.OWNER_MARKER_NAME, "another-database"u8.ToArray());
    var foreign = _coordinator(foreignObjects, metrics: metrics);
    var written = await foreign.WriteAsync(ArchiveBatch.WholeStream(stream));
    var foreignSwept = await foreign.SweepOrphanedObjectsAsync();
    var unclaimable = new List<int>();
    foreach (var failing in new[] { "put", "get" }) {
      var objects = _objects(deleteAfter: delay, container: $"unclaimable-{failing}");
      objects.Faults = (operation, path) => operation == failing && path == PostgresObjectArchiveStore.OWNER_MARKER_NAME ? Fault.Before : Fault.None;
      if (failing == "get") {
        objects.Seed(PostgresObjectArchiveStore.OWNER_MARKER_NAME, "x"u8.ToArray());
      }
      var coordinator = _coordinator(objects, metrics: metrics);
      _ = await coordinator.WriteAsync(ArchiveBatch.WholeStream(await _seedAsync(1)));
      unclaimable.Add(await coordinator.SweepOrphanedObjectsAsync());
    }

    await Assert.That(written.Status).IsEqualTo(ArchiveStatus.ARCHIVED).Because("a write never depends on the marker");
    listener.RecordObservableInstruments();

    await Assert.That(unknown + foreignSwept + unclaimable.Sum()).IsEqualTo(0).Because("a refused sweep deletes nothing");
    await Assert.That(refused[PostgresObjectArchiveStore.SweepRefusal.UNKNOWN_LOCATION]).IsEqualTo(1L);
    await Assert.That(refused[PostgresObjectArchiveStore.SweepRefusal.FOREIGN_MARKER]).IsEqualTo(1L);
    await Assert.That(refused[PostgresObjectArchiveStore.SweepRefusal.MARKER_UNCLAIMABLE]).IsEqualTo(2L)
      .Because("a marker that cannot be written and one that cannot be read both leave the location unproven");
  }

  [Test]
  public async Task Sweep_AnOrphanThatCannotBeDeleted_IsLeftForTheNextSweepAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var stream = await _seedAsync(1);
    var objects = _objects(deleteAfter: TimeSpan.FromDays(1), clock: clock);
    var coordinator = _coordinator(objects, clock);
    _ = await coordinator.WriteAsync(ArchiveBatch.WholeStream(stream));
    var orphan = $"{Guid.CreateVersion7():D}/0000000001-0000000001-aa.jsonl.br";
    objects.Seed(orphan, [1], clock.GetUtcNow());
    clock.Advance(TimeSpan.FromDays(2));
    objects.Faults = (operation, path) => operation == "delete" && path == orphan ? Fault.Before : Fault.None;

    var first = await coordinator.SweepOrphanedObjectsAsync();
    objects.Faults = static (_, _) => Fault.None;
    var second = await coordinator.SweepOrphanedObjectsAsync();

    await Assert.That(first).IsEqualTo(0);
    await Assert.That(second).IsEqualTo(1);
    await Assert.That(objects.Paths).DoesNotContain(orphan);
  }

  // ── Construction, registration, metrics ────────────────────────────────────────────────────────────

  [Test]
  public async Task Coordinator_RefusesSettingsAndLocationsItCannotRunWithAsync() {
    await Assert.That(() => _coordinator(_objects(maxEventsPerChunk: 0))).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new PostgresObjectArchiveStore(_database(), new Located(new("memory", " ", "default", "c")))).Throws<ArgumentException>();
    await Assert.That(() => new PostgresObjectArchiveStore(_database(), new Located(new("memory", null, "default", "c")))).Throws<ArgumentException>();
    var looksLikeASecret = await Assert.That(() => new PostgresObjectArchiveStore(_database(), new Located(new("memory", "e", "AccountKey=k", "c"))))
      .Throws<ArgumentException>();
    await Assert.That(() => new PostgresObjectArchiveStore(_database(), new Located(new("memory", "e", null, "c")))).Throws<ArgumentException>();
    await Assert.That(looksLikeASecret!.Message).DoesNotContain("AccountKey=k").Because("a value that looks like a connection string may be one");
    await Assert.That(_coordinator(_objects()).Clock).IsSameReferenceAs(TimeProvider.System); // review-checks:allow compares the default reference; reads no time
    await Assert.That(_coordinator(_objects()).Objects).IsNotNull();
  }

  [Test]
  public async Task RepointLocation_RefusesAnIdThatCannotNameARow_AndOneNoRowHasAsync() {
    var coordinator = _coordinator(_objects());

    await Assert.That(() => coordinator.RepointLocationAsync(0)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => coordinator.RepointLocationAsync(987_654)).Throws<ArgumentException>();
  }

  [Test]
  public async Task Registration_TheDriverRegistersTheCoordinator_WhenAnObjectStoreIsSelected_ElseTheTableStoreAsync() {
    var stream = await _seedAsync(3);
    var objects = _objects();
    var selected = new ServiceCollection().AddLogging();
    selected.AddWhizbangArchiveObjectStore(_ => new ArchiveObjectStoreSelection(objects, "code"));
    selected.AddWhizbangPostgres(ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false, _noEntries, configureOptions: null);
    selected.AddWhizbangWorkers();
    var none = new ServiceCollection().AddLogging();
    none.AddWhizbangArchiveObjectStore(_ => new ArchiveObjectStoreSelection(null, "nothing configured"));
    none.AddWhizbangPostgres(ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false, _noEntries, configureOptions: null);
    await using var withObjects = selected.BuildServiceProvider();
    await using var withoutObjects = none.BuildServiceProvider();

    var result = await withObjects.GetRequiredService<IStreamPrefixTruncator>().TruncateAsync(stream, 2, Disposition.Archive);

    await Assert.That(withObjects.GetRequiredService<IArchiveStore>()).IsTypeOf<PostgresObjectArchiveStore>();
    await Assert.That(withoutObjects.GetRequiredService<IArchiveStore>()).IsTypeOf<PostgresTableArchiveStore>()
      .Because("a selection with no store keeps the table store");
    await Assert.That(result.Status).IsEqualTo("truncated");
    await Assert.That(_archiveObjects(objects)).Count().IsEqualTo(1)
      .Because("the driver's own archive path writes through the selected object store");
  }

  [Test]
  public async Task ArchiveSweepMetrics_KeepsItsMeterName_AndIsListedInWhizbangMetersAsync() {
    await Assert.That(ArchiveSweepMetrics.METER_NAME).IsEqualTo("Whizbang.Archive.AzureBlob")
      .Because("dashboards and meter subscriptions name the meter; moving the coordinator must not rename it");
    await Assert.That(WhizbangMeters.All).Contains(ArchiveSweepMetrics.METER_NAME);
  }

  // A store that only names a location, for the coordinator's construction checks.
  private sealed class Located(ArchiveLocation location) : IArchiveObjectStore {
    public ArchiveLocation Location { get; } = location;
    public ArchiveObjectStoreSettings Settings { get; } = new();
    public Task PrepareAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> PutAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<byte[]> GetAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<ArchiveObjectInfo> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }
}
