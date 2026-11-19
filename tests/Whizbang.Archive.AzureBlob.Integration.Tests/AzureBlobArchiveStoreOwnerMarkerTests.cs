// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Observability;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Archive.AzureBlob.Integration.Tests;

/// <summary>
/// #2367: one database per container. The first write claims the container with a create-only owner marker naming
/// the database (<c>archive_database_id</c>, migration 465), and the orphan sweep runs only for the database the marker
/// names. Writes and reads never depend on the marker. Two environments here are two real databases on one Azurite.
/// </summary>
/// <docs>operations/deployment/restoring-an-environment-with-an-archive#one-database-per-container</docs>
public partial class AzureBlobArchiveStoreTests {
  // ── The marker ─────────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Write_ClaimsTheContainer_WithAMarkerNamingTheDatabase_AndTheOwnerSweepsAsync() {
    var clock = new ManualClock(_farFuture);
    var (orphan, _) = await _seedAsync(3);
    var store = _delayedStore(clock);
    var named = await _archiveAnotherStreamAsync();
    await _crashAfterUploadAsync(orphan, clock);

    var swept = await store.SweepOrphanedObjectsAsync();

    await Assert.That(await _markerAsync()).IsNotNull().Because("the first write claimed the container");
    await Assert.That(await _markerAsync()).IsEqualTo(await _databaseIdAsync(_db.ConnectionString))
      .Because("the first write claimed the container for this database");
    await Assert.That(swept).IsEqualTo(1).Because("the database the marker names sweeps");
    await Assert.That(await _objectsAsync()).IsEquivalentTo(named);
  }

  [Test]
  public async Task Sweep_ASecondDatabaseOnTheSameContainer_Refuses_AndBothEnvironmentsObjectsSurviveAsync() {
    var clock = new ManualClock(_farFuture);
    var second = await PerTestDatabaseFactory.CreateAsync("blob_archive_b");
    try {
      await new PostgresSchemaInitializer(second.ConnectionString).InitializeSchemaAsync();
      var first = _delayedStore(clock);
      var (firstStream, _) = await _seedAsync(3);
      var (secondStream, _) = await _seedAsync(3, second.ConnectionString);
      var logger = new ListLogger();
      using var metrics = new SweepMetricsRecorder();
      var other = new AzureBlobArchiveStore(_databaseAt(second.ConnectionString), _container(),
        new AzureBlobArchiveOptions { MaxEventsPerChunk = 2, ContainerName = _containerName, DeleteAfter = _delay }, logger, clock,
        metrics.Metrics);
      _ = await first.WriteAsync(ArchiveBatch.WholeStream(firstStream));
      var written = await other.WriteAsync(ArchiveBatch.WholeStream(secondStream));
      var everything = await _objectsAsync();

      var swept = await other.SweepOrphanedObjectsAsync();

      await Assert.That(written.Archived).IsTrue().Because("writes never depend on the marker");
      await Assert.That(await _readAllAsync(other, secondStream)).Count().IsEqualTo(3).Because("nor do reads");
      await Assert.That(logger.Messages).Contains(m => m.Level == LogLevel.Warning && m.Text.Contains("another database", StringComparison.Ordinal))
        .Because("a write into a container another database owns warns that the owner's sweep will not know its objects");
      await Assert.That(swept).IsEqualTo(0);
      await Assert.That(await _objectsAsync()).IsEquivalentTo(everything)
        .Because("the second database names none of the first's objects, so a sweep would have deleted them all: it refuses");
      await Assert.That(metrics.Refused(AzureBlobArchiveStore.SweepRefusal.FOREIGN_MARKER)).IsEqualTo(1L);
      await Assert.That(logger.Messages).Contains(m => m.Level == LogLevel.Warning && m.Text.Contains(AzureBlobArchiveStore.SweepRefusal.FOREIGN_MARKER, StringComparison.Ordinal));
      await Assert.That(await _markerAsync()).IsEqualTo(await _databaseIdAsync(_db.ConnectionString))
        .Because("the marker is never updated");
      await Assert.That(await _databaseIdAsync(second.ConnectionString)).IsNotEqualTo(await _databaseIdAsync(_db.ConnectionString))
        .Because("each database migrated on its own has its own id");
    } finally {
      await PerTestDatabaseFactory.DropAsync(second.Name);
    }
  }

  [Test]
  public async Task Sweep_ADatabaseRestoredToItsOwnContainer_StillMatchesItsMarkerAsync() {
    var clock = new ManualClock(_farFuture);
    var named = await _archiveAnotherStreamAsync();
    var (orphan, _) = await _seedAsync(3);
    var restored = await _copyDatabaseAsync();
    try {
      // The restored database writes (and crashes) into its own container, then sweeps it.
      await _crashAfterUploadAsync(orphan, clock, restored.ConnectionString);
      var store = new AzureBlobArchiveStore(_databaseAt(restored.ConnectionString), _container(),
        new AzureBlobArchiveOptions { ContainerName = _containerName, DeleteAfter = _delay }, logger: null, clock);

      var swept = await store.SweepOrphanedObjectsAsync();

      await Assert.That(swept).IsEqualTo(1).Because("a restored database holds the same id, so the marker still names it");
      await Assert.That(await _objectsAsync()).IsEquivalentTo(named);
    } finally {
      await PerTestDatabaseFactory.DropAsync(restored.Name);
    }
  }

  [Test]
  public async Task Sweep_ARepointedCopy_ClaimsItsNewContainer_AndSweepsItAsync() {
    var clock = new ManualClock(_farFuture);
    var (stream, _) = await _seedAsync(3);
    var (orphan, _) = await _seedAsync(3);
    _ = await _store(maxEventsPerChunk: 10).WriteAsync(ArchiveBatch.WholeStream(stream));
    var sourceObjects = await _objectsAsync();
    var copy = await _copyDatabaseAsync();
    try {
      var target = $"archive-{Guid.NewGuid():N}";
      await _copyObjectsAsync(_containerName, target);
      var options = new AzureBlobArchiveOptions { MaxEventsPerChunk = 10, ContainerName = target, DeleteAfter = _delay };
      var moved = new AzureBlobArchiveStore(_databaseAt(copy.ConnectionString), _container(target), options, logger: null, clock);
      var refused = await Assert.That(() => _completedAsync(_readAllAsync(moved, stream))).Throws<ArchiveLocationMismatchException>();
      _ = await moved.RepointLocationAsync(refused!.RecordedLocationId!.Value);
      var crashing = new AzureBlobArchiveStore(_databaseAt(copy.ConnectionString), _container(target, new FaultObjectWrites(afterSending: true)),
        options, logger: null, clock);
      await Assert.That(() => _completedAsync(crashing.WriteAsync(ArchiveBatch.WholeStream(orphan)))).Throws<RequestFailedException>();
      var withTheOrphan = await _objectsAsync(target);

      var swept = await moved.SweepOrphanedObjectsAsync();

      await Assert.That(withTheOrphan).Count().IsEqualTo(sourceObjects.Count + 1);
      await Assert.That(swept).IsEqualTo(1).Because("the repointed copy claimed its new container and sweeps it");
      await Assert.That(await _objectsAsync(target)).IsEquivalentTo(sourceObjects);
      await Assert.That(await _markerAsync(target)).IsNotNull();
      await Assert.That(await _markerAsync(target)).IsEqualTo(await _databaseIdAsync(copy.ConnectionString));
      await Assert.That(await _objectsAsync()).IsEquivalentTo(sourceObjects).Because("the source container is untouched");
    } finally {
      await PerTestDatabaseFactory.DropAsync(copy.Name);
    }
  }

  [Test]
  public async Task Sweep_AForeignMarkerPlacedFirst_Refuses_AndTheWriteStillSucceedsAsync() {
    var clock = new ManualClock(_farFuture);
    _ = await _container().CreateIfNotExistsAsync();
    _ = await _container().GetBlobClient(AzureBlobArchiveStore.OWNER_MARKER_NAME).UploadAsync(BinaryData.FromString("some-other-database"));
    var (orphan, _) = await _seedAsync(3);
    var named = await _archiveAnotherStreamAsync();
    await _crashAfterUploadAsync(orphan, clock);
    using var metrics = new SweepMetricsRecorder();
    var store = new AzureBlobArchiveStore(_database(), _container(),
      new AzureBlobArchiveOptions { ContainerName = _containerName, DeleteAfter = _delay }, logger: null, clock, metrics.Metrics);

    var swept = await store.SweepOrphanedObjectsAsync();

    await Assert.That(named).IsNotEmpty().Because("the write into a container another database claimed still succeeded");
    await Assert.That(swept).IsEqualTo(0);
    await Assert.That(await _objectsAsync()).Count().IsEqualTo(named.Count + 1).Because("the orphan stays: nothing is swept");
    await Assert.That(metrics.Refused(AzureBlobArchiveStore.SweepRefusal.FOREIGN_MARKER)).IsEqualTo(1L);
    await Assert.That(await _markerAsync()).IsEqualTo("some-other-database").Because("a marker is never updated");
  }

  [Test]
  [Arguments("PUT")]
  [Arguments("GET")]
  public async Task Sweep_AMarkerItCannotClaimOrRead_Refuses_AndTheWriteStillSucceedsAsync(string failing) {
    var clock = new ManualClock(_farFuture);
    var (orphan, _) = await _seedAsync(3);
    var (stream, _) = await _seedAsync(3);
    var fault = new FaultMarker(new RequestMethod(failing));
    var logger = new ListLogger();
    using var metrics = new SweepMetricsRecorder();
    var options = new AzureBlobArchiveOptions { MaxEventsPerChunk = 10, ContainerName = _containerName, DeleteAfter = _delay };
    if (failing == "GET") {
      // A marker is there, written by this database, but cannot be read back.
      _ = await _store(maxEventsPerChunk: 10).WriteAsync(ArchiveBatch.WholeStream(stream));
    }
    var store = new AzureBlobArchiveStore(_database(), _container(fault: fault), options, logger, clock, metrics.Metrics);
    if (failing == "PUT") {
      _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));
    }
    await _crashAfterUploadAsync(orphan, clock);
    var before = await _objectsAsync();

    var swept = await store.SweepOrphanedObjectsAsync();

    await Assert.That(fault.Faulted).IsGreaterThan(0).Because("the marker request failed, as the test intends");
    await Assert.That(await _readAllAsync(_store(), stream)).Count().IsEqualTo(3).Because("the write succeeded without its marker");
    await Assert.That(swept).IsEqualTo(0);
    await Assert.That(await _objectsAsync()).IsEquivalentTo(before);
    await Assert.That(metrics.Refused(AzureBlobArchiveStore.SweepRefusal.MARKER_UNCLAIMABLE)).IsEqualTo(1L);
    await Assert.That(logger.Messages).Contains(m => m.Level == LogLevel.Warning
      && m.Text.Contains(AzureBlobArchiveStore.SweepRefusal.MARKER_UNCLAIMABLE, StringComparison.Ordinal));
  }

  [Test]
  public async Task Sweep_AContainerWithoutAMarker_IsClaimedByTheSweep_AndSweptAsync() {
    var clock = new ManualClock(_farFuture);
    var (orphan, _) = await _seedAsync(3);
    var named = await _archiveAnotherStreamAsync();
    // A container written before markers existed: no marker.
    _ = await _container().GetBlobClient(AzureBlobArchiveStore.OWNER_MARKER_NAME).DeleteAsync();
    await _crashAfterUploadAsync(orphan, clock);
    var store = new AzureBlobArchiveStore(_database(), _container(),
      new AzureBlobArchiveOptions { ContainerName = _containerName, DeleteAfter = _delay }, logger: null, clock);

    var swept = await store.SweepOrphanedObjectsAsync();

    await Assert.That(swept).IsEqualTo(1);
    await Assert.That(await _markerAsync()).IsEqualTo(await _databaseIdAsync(_db.ConnectionString));
    await Assert.That(await _objectsAsync()).IsEquivalentTo(named);
  }

  [Test]
  public async Task Sweep_InADatabaseThatNeverWroteToTheLocation_CountsTheRefusalAsync() {
    using var metrics = new SweepMetricsRecorder();
    var store = new AzureBlobArchiveStore(_database(), _container(),
      new AzureBlobArchiveOptions { ContainerName = _containerName, DeleteAfter = _delay }, logger: null, new ManualClock(_farFuture), metrics.Metrics);

    await Assert.That(await store.SweepOrphanedObjectsAsync()).IsEqualTo(0);
    await Assert.That(metrics.Refused(AzureBlobArchiveStore.SweepRefusal.UNKNOWN_LOCATION)).IsEqualTo(1L);
  }

  // ── Arrange ────────────────────────────────────────────────────────────────────────────────────────

  // An upload that reaches storage while its write never commits: an orphan, in this container.
  private async Task _crashAfterUploadAsync(Guid stream, TimeProvider clock, string? connectionString = null) {
    var crashing = new AzureBlobArchiveStore(connectionString is null ? _database() : _databaseAt(connectionString),
      _container(fault: new FaultObjectWrites(afterSending: true)),
      new AzureBlobArchiveOptions { MaxEventsPerChunk = 10, ContainerName = _containerName, DeleteAfter = _delay }, logger: null, clock);
    await Assert.That(() => _completedAsync(crashing.WriteAsync(ArchiveBatch.WholeStream(stream)))).Throws<RequestFailedException>();
  }

  private async Task<string?> _markerAsync(string? container = null) {
    var blob = _container(container).GetBlobClient(AzureBlobArchiveStore.OWNER_MARKER_NAME);
    return (await blob.ExistsAsync()).Value ? (await blob.DownloadContentAsync()).Value.Content.ToString() : null;
  }

  private static async Task<string> _databaseIdAsync(string connectionString) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
      "SELECT setting_value FROM wh_settings WHERE setting_key = 'archive_database_id'", connection);
    return (string)(await command.ExecuteScalarAsync())!;
  }

  /// <summary>Fails every request of one method addressed to the owner marker.</summary>
  private sealed class FaultMarker(RequestMethod method) : HttpPipelinePolicy {
    public int Faulted { get; private set; }

    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) =>
      ProcessAsync(message, pipeline).AsTask().GetAwaiter().GetResult();

    public override ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) {
      if (message.Request.Method == method
          && message.Request.Uri.Path.EndsWith("/" + AzureBlobArchiveStore.OWNER_MARKER_NAME, StringComparison.Ordinal)) {
        Faulted++;
        throw new RequestFailedException(503, "injected: the marker request failed");
      }
      return ProcessNextAsync(message, pipeline);
    }
  }

  /// <summary>The sweep metrics of one store, read through a listener on exactly its instrument.</summary>
  private sealed class SweepMetricsRecorder : IDisposable {
    private readonly MeterListener _listener;
    private readonly ConcurrentDictionary<string, long> _refused = new(StringComparer.Ordinal);

    public SweepMetricsRecorder() {
      Metrics = new ArchiveSweepMetrics(new WhizbangMetrics(
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
      _listener = new MeterListener {
        InstrumentPublished = (instrument, listener) => {
          if (instrument == Metrics.SweepsRefused.Instrument) {
            listener.EnableMeasurementEvents(instrument);
          }
        },
      };
      _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
        foreach (var tag in tags) {
          if (tag.Key == "reason" && tag.Value is string reason) {
            _refused[reason] = value;
          }
        }
      });
      _listener.Start();
    }

    public ArchiveSweepMetrics Metrics { get; }

    // The cumulative count of refusals for one reason (passive counters report on collection).
    public long Refused(string reason) {
      _refused.Clear();
      _listener.RecordObservableInstruments();
      return _refused.GetValueOrDefault(reason);
    }

    public void Dispose() => _listener.Dispose();
  }
}
