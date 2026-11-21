// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Archive.AzureBlob.Integration.Tests;

/// <summary>
/// The archive location pin against the three moves that motivated it (#2141), under each handling of
/// <c>Whizbang:Archive:AzureBlob:LocationMismatch</c>. The database stores the normalized location every record
/// references; infrastructure decides where the objects actually are, and configuration points the store there. In
/// each move below the infrastructure put the objects in the configured place, and the records still name the old one:
/// <list type="bullet">
/// <item><b>restore</b>: storage restored into another container;</item>
/// <item><b>failover</b>: storage failed over to another account endpoint;</item>
/// <item><b>copy</b>: the database copied to another environment, with its own connection name and container.</item>
/// </list>
/// Error refuses before any request reaches storage; Warning and Info go ahead in the configured location and log the
/// mismatch at that level; Ignore goes ahead without comparing or logging. A location written another way (case,
/// whitespace, a trailing slash, a default port) is the same location and is never a mismatch.
/// </summary>
/// <docs>fundamentals/events/archive-store#archive-locations</docs>
public partial class AzureBlobArchiveStoreTests {
  private const string RESTORE = "restore";
  private const string FAILOVER = "failover";
  private const string COPY = "copy";

  // The store a moved environment runs, configured for where infrastructure now keeps the objects, with every request
  // it sends counted.
  private async Task<(AzureBlobArchiveStore Store, RedirectHost Requests)> _movedAsync(
      string move, ArchiveLocationMismatchHandling handling, ListLogger logger) {
    if (move == FAILOVER) {
      // The same container at another account endpoint: the emulator answers for both, as a replicated account would.
      var (connectionString, redirect) = _anotherEndpoint();
      return (_store(maxEventsPerChunk: 10, fault: redirect, logger: logger, connectionString: connectionString, locationMismatch: handling),
        redirect);
    }
    var counter = new RedirectHost(from: null, to: null);
    var container = $"archive-{Guid.NewGuid():N}";
    await _copyObjectsAsync(_containerName, container);
    return (_store(maxEventsPerChunk: 10, fault: counter, container: container, logger: logger, locationMismatch: handling,
      connectionName: move == COPY ? "staging" : null), counter);
  }

  private static List<(LogLevel Level, string Text)> _mismatchLogs(ListLogger logger) =>
    logger.Messages.Where(m => m.Text.Contains("LocationMismatch", StringComparison.Ordinal)).ToList();

  private static LogLevel _levelOf(ArchiveLocationMismatchHandling handling) =>
    handling == ArchiveLocationMismatchHandling.Warning ? LogLevel.Warning : LogLevel.Information;

  [Test]
  [Arguments(RESTORE, ArchiveLocationMismatchHandling.Error)]
  [Arguments(RESTORE, ArchiveLocationMismatchHandling.Warning)]
  [Arguments(RESTORE, ArchiveLocationMismatchHandling.Info)]
  [Arguments(RESTORE, ArchiveLocationMismatchHandling.Ignore)]
  [Arguments(FAILOVER, ArchiveLocationMismatchHandling.Error)]
  [Arguments(FAILOVER, ArchiveLocationMismatchHandling.Warning)]
  [Arguments(FAILOVER, ArchiveLocationMismatchHandling.Info)]
  [Arguments(FAILOVER, ArchiveLocationMismatchHandling.Ignore)]
  [Arguments(COPY, ArchiveLocationMismatchHandling.Error)]
  [Arguments(COPY, ArchiveLocationMismatchHandling.Warning)]
  [Arguments(COPY, ArchiveLocationMismatchHandling.Info)]
  [Arguments(COPY, ArchiveLocationMismatchHandling.Ignore)]
  public async Task Move_ARecordPinnedToTheOldLocation_IsHandledAsConfiguredAsync(string move, ArchiveLocationMismatchHandling handling) {
    var (archived, _) = await _seedAsync(3);
    var (growing, _) = await _seedAsync(4);
    var hotAsRead = await _hotAsReadAsync(archived);
    var source = _store(maxEventsPerChunk: 10);
    _ = await source.WriteAsync(ArchiveBatch.WholeStream(archived));
    _ = await source.WriteAsync(ArchiveBatch.Prefix(growing, 2));
    var sourceObjects = await _objectsAsync();
    var logger = new ListLogger();
    var (moved, requests) = await _movedAsync(move, handling, logger);

    await Assert.That(moved.ConfiguredLocation).IsNotEqualTo(source.ConfiguredLocation).Because($"the {move} is another location");
    if (handling == ArchiveLocationMismatchHandling.Error) {
      var read = await Assert.That(() => _completedAsync(_readAllAsync(moved, archived))).Throws<ArchiveLocationMismatchException>();
      var write = await Assert.That(() => _completedAsync(moved.WriteAsync(ArchiveBatch.Prefix(growing, 3))))
        .Throws<ArchiveLocationMismatchException>();

      await Assert.That(read!.Operation).IsEqualTo("read");
      await Assert.That(write!.Operation).IsEqualTo("write");
      await Assert.That(read.Recorded).IsEqualTo(source.ConfiguredLocation);
      await Assert.That(requests.Requests).IsEqualTo(0).Because("Error stops the archive before any request reaches storage");
      await Assert.That(await _countAsync("SELECT count(*) FROM wh_event_store WHERE stream_id = @s", growing)).IsEqualTo(2L)
        .Because("the refused write moved nothing");
      await Assert.That(_mismatchLogs(logger)).IsEmpty().Because("the exception is the report");
    } else {
      var read = await _readAllAsync(moved, archived);
      var write = await moved.WriteAsync(ArchiveBatch.Prefix(growing, 3));

      await Assert.That(read).IsEquivalentTo(hotAsRead, TUnit.Assertions.Enums.CollectionOrdering.Matching)
        .Because("infrastructure controls the location: the objects are read from the configured one");
      await Assert.That(write.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
      var logs = _mismatchLogs(logger);
      if (handling == ArchiveLocationMismatchHandling.Ignore) {
        await Assert.That(logs).IsEmpty().Because("Ignore neither compares nor logs");
      } else {
        await Assert.That(logs.Count).IsEqualTo(2).Because("one entry for the read, one for the write");
        await Assert.That(logs.All(l => l.Level == _levelOf(handling))).IsTrue().Because($"{handling} logs at its own level");
        await Assert.That(logs[0].Text).Contains(archived.ToString()).And.Contains("read").And.Contains(moved.ConfiguredLocation.ToString());
        await Assert.That(logs[1].Text).Contains(growing.ToString()).And.Contains("write");
      }
    }
    var objectsAfter = await _objectsAsync();
    if (move == FAILOVER && handling != ArchiveLocationMismatchHandling.Error) {
      // The failover account is the same emulator container, so the write's new objects land beside the old ones.
      await Assert.That(sourceObjects.All(objectsAfter.Contains)).IsTrue().Because("no object was deleted");
    } else {
      await Assert.That(objectsAfter).IsEquivalentTo(sourceObjects).Because("the old location is never touched");
    }
  }

  [Test]
  [Arguments(ArchiveLocationMismatchHandling.Error)]
  [Arguments(ArchiveLocationMismatchHandling.Warning)]
  [Arguments(ArchiveLocationMismatchHandling.Info)]
  [Arguments(ArchiveLocationMismatchHandling.Ignore)]
  public async Task Failover_RecordedByHandInAnotherForm_IsTheSameLocation_UnderEveryHandlingAsync(ArchiveLocationMismatchHandling handling) {
    var (stream, _) = await _seedAsync(3);
    var hotAsRead = await _hotAsReadAsync(stream);
    _ = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));
    var logger = new ListLogger();
    var (connectionString, redirect) = _anotherEndpoint();
    var moved = _store(fault: redirect, logger: logger, connectionString: connectionString, locationMismatch: handling,
      connectionName: " DEFAULT ");
    // The operator points the location row at the failover account, written the way a person types it.
    var typed = " " + moved.ConfiguredLocation.Endpoint!.ToUpperInvariant() + "/ ";
    await using (var connection = await _openAsync()) {
      await using var update = new NpgsqlCommand(
        "UPDATE wh_archive_locations SET account_endpoint = @e, connection_name = ' Default ', container = upper(container)", connection);
      update.Parameters.AddWithValue("e", typed);
      _ = await update.ExecuteNonQueryAsync();
    }

    var read = await _readAllAsync(moved, stream);

    await Assert.That(read).IsEquivalentTo(hotAsRead, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("case, whitespace and a trailing slash do not make another location, so even Error goes ahead");
    await Assert.That(_mismatchLogs(logger)).IsEmpty();
    await Assert.That(moved.ConfiguredLocation.ConnectionName).IsEqualTo("default");
    await Assert.That(await _rowsAsync(
        "SELECT concat_ws('|', account_endpoint, connection_name, container) FROM wh_archive_locations WHERE @s IS NOT NULL", stream))
      .IsEquivalentTo([$"{moved.ConfiguredLocation.Endpoint}|default|{_containerName}"])
      .Because("the database stores the pin normalized, however it was written");
  }

  [Test]
  [Arguments(ArchiveLocationMismatchHandling.Error)]
  [Arguments(ArchiveLocationMismatchHandling.Warning)]
  [Arguments(ArchiveLocationMismatchHandling.Info)]
  [Arguments(ArchiveLocationMismatchHandling.Ignore)]
  public async Task Copy_QueuedDeletionsPinnedToTheSource_AreHandledAsConfiguredAsync(ArchiveLocationMismatchHandling handling) {
    var (purged, _) = await _seedAsync(3);
    var source = _store(maxEventsPerChunk: 2);
    _ = await source.WriteAsync(ArchiveBatch.WholeStream(purged));
    var sourceObjects = await _objectsAsync();
    var logger = new ListLogger();
    var (copied, _) = await _movedAsync(COPY, handling, logger);
    var target = copied.ConfiguredLocation.Container;
    _ = await new PostgresStreamPurger(_database().Open).PurgeAsync(
      new StreamPurgeRequest { StreamIds = [purged], RequestedBy = "operator-1", Reason = "test" });

    var drained = await copied.DeleteQueuedObjectsAsync(100);

    await Assert.That(await _objectsAsync()).IsEquivalentTo(sourceObjects).Because("the source's objects are never deleted");
    var logs = _mismatchLogs(logger);
    if (handling == ArchiveLocationMismatchHandling.Error) {
      await Assert.That(drained).IsEqualTo(0).Because("Error deletes only what is queued in the configured location");
      await Assert.That(await _objectsAsync(target)).IsEquivalentTo(sourceObjects);
      await Assert.That(logs).IsEmpty();
    } else {
      await Assert.That(drained).IsEqualTo(sourceObjects.Count)
        .Because("infrastructure controls the location: the queued objects are deleted from the configured container");
      await Assert.That(await _objectsAsync(target)).IsEmpty();
      if (handling == ArchiveLocationMismatchHandling.Ignore) {
        await Assert.That(logs).IsEmpty();
      } else {
        await Assert.That(logs.Count).IsEqualTo(1).Because("one entry per drain, with the count");
        await Assert.That(logs[0].Level).IsEqualTo(_levelOf(handling));
        await Assert.That(logs[0].Text).Contains($"{sourceObjects.Count} queued").And.Contains(target);
      }
    }
  }

  [Test]
  [Arguments(ArchiveLocationMismatchHandling.Warning)]
  [Arguments(ArchiveLocationMismatchHandling.Info)]
  public async Task NoMove_TheConfiguredLocation_IsNeverReportedAsAMismatchAsync(ArchiveLocationMismatchHandling handling) {
    var (stream, _) = await _seedAsync(3);
    var logger = new ListLogger();
    var store = _store(maxEventsPerChunk: 2, logger: logger, locationMismatch: handling);
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));

    var read = await _readAllAsync(store, stream);
    var restored = await store.RestoreAsync(stream);

    await Assert.That(read).Count().IsEqualTo(3);
    await Assert.That(restored.Status).IsEqualTo(ArchiveStatus.RESTORED);
    await Assert.That(await _objectsAsync()).IsEmpty().Because("the restore's own queued objects were deleted in the configured location");
    await Assert.That(_mismatchLogs(logger)).IsEmpty().Because("a record in the configured location is never a mismatch, so nothing is logged");
  }

  [Test]
  public async Task Info_ARecordWithNoLocation_IsReadInTheConfiguredLocation_AndLoggedAsNamingNoneAsync() {
    var (stream, _) = await _seedAsync(3);
    var hotAsRead = await _hotAsReadAsync(stream);
    var logger = new ListLogger();
    var store = _store(logger: logger, locationMismatch: ArchiveLocationMismatchHandling.Info);
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));
    await using (var connection = await _openAsync()) {
      await _executeAsync(connection, $"UPDATE wh_archive_manifest SET location_id = NULL WHERE stream_id = '{stream}'");
    }

    var read = await _readAllAsync(store, stream);

    await Assert.That(read).IsEquivalentTo(hotAsRead, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    var logs = _mismatchLogs(logger);
    await Assert.That(logs.Count).IsEqualTo(1);
    await Assert.That(logs[0].Level).IsEqualTo(LogLevel.Information);
    await Assert.That(logs[0].Text).Contains("(no location)").Because("a record that names no location is reported as such");
  }
}
