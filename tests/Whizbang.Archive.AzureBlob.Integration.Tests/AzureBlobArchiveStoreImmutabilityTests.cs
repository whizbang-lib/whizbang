// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Storage.AzureBlob.Integration.Tests;

/// <summary>
/// #2367: an archived object is written once and never updated, and the store never purges what the storage account
/// keeps after a delete. Both are what let a database and its archive storage be restored to the same point in time.
/// </summary>
/// <docs>operations/deployment/restoring-an-environment-with-an-archive</docs>
public partial class AzureBlobArchiveStoreTests {
  // ── Never updated ──────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Write_NeverReplacesAnObject_WithDifferentBytesUnderItsName_TheStreamStaysHotAsync() {
    var (stream, _) = await _seedAsync(3);
    var store = _store(maxEventsPerChunk: 10);
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));
    var name = (await _objectsAsync()).Single();
    _ = await store.RestoreAsync(stream);
    // Something else now holds the name the same events archive to: bytes that are not theirs.
    var foreign = BinaryData.FromString("not the archived events");
    _ = await _container().GetBlobClient(name).UploadAsync(foreign);
    var hot = await _hotAsync(stream);

    var refused = await Assert.That(() => _completedAsync(store.WriteAsync(ArchiveBatch.WholeStream(stream))))
      .Throws<ArchiveObjectCorruptedException>();

    await Assert.That(refused!.Locator).IsEqualTo(name);
    await Assert.That(refused.Message).Contains("this write encoded").Because("the expected bytes came from the write, not a manifest row");
    await Assert.That(refused.ActualSha256).IsEqualTo(ArchiveChunk.Sha256(foreign.ToArray()))
      .Because("the write compared the object already under the name with the bytes it would have written");
    await Assert.That((await _container().GetBlobClient(name).DownloadContentAsync()).Value.Content.ToString())
      .IsEqualTo("not the archived events").Because("a write never replaces an existing object; it only creates");
    await Assert.That(await _hotAsync(stream)).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the refused write rolled back, so the stream stays fully hot");
    await Assert.That(await _manifestAsync(stream)).IsEmpty();
  }

  [Test]
  public async Task EveryStorageRequest_CreatesReadsListsOrPlainDeletes_NeverUpdatesOrPurgesAsync() {
    var recorder = new RecordRequests();
    var store = _recordedStore(recorder);
    var crash = new FaultObjectWrites(afterSending: true);
    var crashing = _recordedStore(recorder, crash);
    var (restored, _) = await _seedAsync(3);
    var (deleted, _) = await _seedAsync(3);
    var (purged, _) = await _seedAsync(3);
    var (retried, _) = await _seedAsync(3);

    // Every operation the store has: write (and the same events again), read, restore, delete, a purge drained by the
    // maintenance step, and a write retried after a crash between its upload and its commit.
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(restored));
    _ = await _readAllAsync(store, restored);
    _ = await store.RestoreAsync(restored);
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(restored));
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(deleted));
    _ = await store.DeleteAsync(deleted);
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(purged));
    _ = await new PostgresStreamPurger(_database().Open).PurgeAsync(
      new StreamPurgeRequest { StreamIds = [purged], RequestedBy = "operator-1", Reason = "test" });
    await new ArchiveObjectDeletionStep(store).RunAsync(_noServices, CancellationToken.None);
    await Assert.That(() => _completedAsync(crashing.WriteAsync(ArchiveBatch.WholeStream(retried)))).Throws<ArchiveObjectStoreException>();
    crash.Armed = false;
    _ = await crashing.WriteAsync(ArchiveBatch.WholeStream(retried));
    // With a deletion delay: the queue drained once the delay has passed, and the orphan sweep (the one listing).
    var (orphan, _) = await _seedAsync(3);
    crash.Armed = true;
    await Assert.That(() => _completedAsync(crashing.WriteAsync(ArchiveBatch.WholeStream(orphan)))).Throws<ArchiveObjectStoreException>();
    var delayed = _recordedStore(recorder, options: new AzureBlobArchiveOptions { MaxEventsPerChunk = 2, DeleteAfter = TimeSpan.FromDays(1) },
      clock: new ManualClock(new DateTimeOffset(2999, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    _ = await delayed.RestoreAsync(restored);
    await new ArchiveObjectDeletionStep(delayed).RunAsync(_noServices, CancellationToken.None);
    _ = await delayed.SweepOrphanedObjectsAsync();

    await Assert.That(recorder.Kinds).Contains(RecordRequests.CREATE_OBJECT).And.Contains(RecordRequests.READ_OBJECT)
      .And.Contains(RecordRequests.DELETE_OBJECT).And.Contains(RecordRequests.LIST_OBJECTS).And.Contains(RecordRequests.CREATE_CONTAINER).Because("a positive control: the recording saw every kind of request it judges");
    await Assert.That(recorder.Violations).IsEmpty()
      .Because("an object is only created (If-None-Match: *), read, listed or plainly deleted: never overwritten, never "
        + "given another tier, metadata, properties or tags, never snapshotted or copied over, and a delete never purges "
        + "snapshots, versions or a soft-deleted object, never undeletes and never deletes the container");
  }

  // A store whose every request passes the recorder, and optionally a fault after it.
  private PostgresObjectArchiveStore _recordedStore(
      RecordRequests recorder, HttpPipelinePolicy? fault = null, AzureBlobArchiveOptions? options = null, TimeProvider? clock = null) {
    var clientOptions = new BlobClientOptions();
    clientOptions.AddPolicy(recorder, HttpPipelinePosition.PerCall);
    if (fault is not null) {
      clientOptions.AddPolicy(fault, HttpPipelinePosition.PerCall);
    }
    var client = new BlobContainerClient(_azurite, _containerName, clientOptions);
    options ??= new AzureBlobArchiveOptions { MaxEventsPerChunk = 2 };
    options.ContainerName = client.Name;
    return AzureBlobArchive.Create(_database(), client, options, logger: null, timeProvider: clock);
  }

  /// <summary>
  /// Records every request the store sends and judges it against the guarantees of #2367: an object is created and never
  /// overwritten, read, listed, or deleted with the plain delete only.
  /// </summary>
  private sealed class RecordRequests : HttpPipelinePolicy {
    public const string CREATE_CONTAINER = "create container";
    public const string CREATE_OBJECT = "create object";
    public const string READ_OBJECT = "read object";
    public const string LIST_OBJECTS = "list objects";
    public const string DELETE_OBJECT = "delete object";

    public List<string> Kinds { get; } = [];
    public List<string> Violations { get; } = [];

    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) =>
      ProcessAsync(message, pipeline).AsTask().GetAwaiter().GetResult();

    public override ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) {
      _judge(message.Request);
      return ProcessNextAsync(message, pipeline);
    }

    private void _judge(Request request) {
      var query = _query(request.Uri.Query);
      query.TryGetValue("comp", out var comp);
      var container = query.TryGetValue("restype", out var restype) && restype == "container";
      var method = request.Method;
      var described = $"{method} {request.Uri.Path}?{request.Uri.Query}";
      string? kind = null;
      if (container && method == RequestMethod.Put && comp is null) {
        kind = CREATE_CONTAINER;
      } else if (container && method == RequestMethod.Get && comp == "list") {
        kind = LIST_OBJECTS;
      } else if (!container && method == RequestMethod.Put && comp is null or "block" or "blocklist") {
        kind = CREATE_OBJECT;
        if (!request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) || ifNoneMatch != "*") {
          Violations.Add($"{described}: an object write without If-None-Match: * can overwrite an existing object");
        }
        if (request.Headers.TryGetValue("x-ms-copy-source", out _)) {
          Violations.Add($"{described}: an object written from a copy");
        }
      } else if (!container && (method == RequestMethod.Get || method == RequestMethod.Head) && comp is null) {
        kind = READ_OBJECT;
      } else if (!container && method == RequestMethod.Delete && comp is null) {
        kind = DELETE_OBJECT;
        if (request.Headers.TryGetValue("x-ms-delete-snapshots", out var snapshots)) {
          Violations.Add($"{described}: a delete that also deletes snapshots ({snapshots})");
        }
        foreach (var purge in (string[])["deletetype", "snapshot", "versionid"]) {
          if (query.ContainsKey(purge)) {
            Violations.Add($"{described}: a delete addressed with {purge}, which purges what soft delete or versioning keeps");
          }
        }
      }
      if (kind is null) {
        Violations.Add($"{described}: not a create, a read, a list or a plain delete");
        return;
      }
      Kinds.Add(kind);
    }

    private static Dictionary<string, string> _query(string query) {
      var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)) {
        var equals = part.IndexOf('=', StringComparison.Ordinal);
        pairs[Uri.UnescapeDataString(equals < 0 ? part : part[..equals])] = equals < 0 ? string.Empty : Uri.UnescapeDataString(part[(equals + 1)..]);
      }
      return pairs;
    }
  }
}
