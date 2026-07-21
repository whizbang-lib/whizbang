using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Unit tests for the E3-3b <see cref="StreamCompactor"/> fold orchestration: read the authoritative snapshot,
/// append the permanent <see cref="Compacted"/> origin (summary durable BEFORE the truncate), then gated-
/// truncate the folded detail via the A1 <see cref="IStreamCloser"/>. Branches: no snapshot, no anchor version,
/// and the happy path (append-then-close ordering + the Compacted payload). The DB-touching pieces (append
/// flagging Compacted, the version query, the truncate) are integration-tested individually elsewhere.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class StreamCompactorTests {
  private sealed class FakeSnapshotStore : IPerspectiveSnapshotStore {
    public (Guid SnapshotEventId, JsonDocument SnapshotData)? Snapshot { get; init; }
    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(Snapshot);
    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) => Task.CompletedTask;
    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();
    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();
  }

  private sealed class FakeEventStore(List<string> log) : IEventStore {
    public List<object> Appended { get; } = [];
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull {
      log.Add("append");
      Appended.Add(message);
      return Task.CompletedTask;
    }
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SyncResult> AppendAndWaitAsync<TMessage, TPerspective>(Guid streamId, TMessage message, TimeSpan? timeout = null, Action<SyncWaitingContext>? onWaiting = null, Action<SyncDecisionContext>? onDecisionMade = null, CancellationToken cancellationToken = default) where TMessage : notnull where TPerspective : class => throw new NotSupportedException();
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => throw new NotSupportedException();
  }

  private sealed class FakeCloser(List<string> log, StreamCloseResult result) : IStreamCloser {
    public (Guid StreamId, long Through, bool Archive)? LastCall { get; private set; }
    public int Calls { get; private set; }
    public Task<StreamCloseResult> CloseAsync(Guid streamId, long throughVersion, bool archive = false, CancellationToken cancellationToken = default) {
      log.Add("close");
      Calls++;
      LastCall = (streamId, throughVersion, archive);
      return Task.FromResult(result);
    }
  }

  private sealed class FakeCoordinator(long? version) : IWorkCoordinator {
    public Task<long?> GetEventVersionAsync(Guid eventId, CancellationToken cancellationToken = default) => Task.FromResult(version);
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }

  private static JsonDocument _wrappedSnapshot(string modelJson, int version) {
    using var model = JsonDocument.Parse(modelJson);
    return Whizbang.Core.Serialization.VersionedJsonEnvelope.Wrap(model.RootElement, version);
  }

  private static StreamCompactor _compactor(FakeSnapshotStore snaps, FakeCoordinator coord, FakeEventStore events, FakeCloser closer) =>
    new(snaps, coord, events, closer, NullLogger<StreamCompactor>.Instance);

  [Test]
  public async Task Compact_NoSnapshot_ReturnsNoSnapshot_AppendsNothingAsync() {
    var log = new List<string>();
    var events = new FakeEventStore(log);
    var closer = new FakeCloser(log, new StreamCloseResult("closed", 3));
    var compactor = _compactor(new FakeSnapshotStore { Snapshot = null }, new FakeCoordinator(5), events, closer);

    var result = await compactor.CompactAsync(Guid.NewGuid(), "P");

    await Assert.That(result.Status).IsEqualTo("no_snapshot")
      .Because("There is no authoritative model to fold, so the compaction is a no-op.");
    await Assert.That(events.Appended.Count).IsEqualTo(0);
    await Assert.That(closer.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task Compact_NoAnchorVersion_ReturnsNoAnchor_AppendsNothingAsync() {
    var log = new List<string>();
    var events = new FakeEventStore(log);
    var closer = new FakeCloser(log, new StreamCloseResult("closed", 3));
    var snaps = new FakeSnapshotStore { Snapshot = (Guid.NewGuid(), _wrappedSnapshot("{}", 1)) };
    var compactor = _compactor(snaps, new FakeCoordinator(version: null), events, closer);

    var result = await compactor.CompactAsync(Guid.NewGuid(), "P");

    await Assert.That(result.Status).IsEqualTo("no_anchor")
      .Because("The snapshot's anchor event has no resolvable per-stream version — nothing to fold through.");
    await Assert.That(events.Appended.Count).IsEqualTo(0);
    await Assert.That(closer.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task Compact_HappyPath_AppendsCompactedOriginThenTruncatesThroughAnchorAsync() {
    var log = new List<string>();
    var streamId = Guid.NewGuid();
    var events = new FakeEventStore(log);
    var closer = new FakeCloser(log, new StreamCloseResult("closed", 7));
    var snaps = new FakeSnapshotStore { Snapshot = (Guid.NewGuid(), _wrappedSnapshot("""{"balance":140}""", 3)) };
    var compactor = _compactor(snaps, new FakeCoordinator(version: 42), events, closer);

    var result = await compactor.CompactAsync(streamId, "LedgerBalance");

    await Assert.That(result.Status).IsEqualTo("compacted");
    await Assert.That(result.ThroughVersion).IsEqualTo(42L);
    await Assert.That(result.EventsFolded).IsEqualTo(7L);

    // Summary durable BEFORE the truncate — no state loss on a mid-fold failure.
    await Assert.That(string.Join(",", log)).IsEqualTo("append,close")
      .Because("The Compacted origin must be appended before the folded detail is truncated.");

    // The appended origin carries the model + schema version + the fold point.
    await Assert.That(events.Appended.Count).IsEqualTo(1);
    var compacted = events.Appended[0] as Compacted;
    await Assert.That(compacted).IsNotNull().Because("A Compacted carry-forward is appended at the head.");
    await Assert.That(compacted!.StreamId).IsEqualTo(streamId);
    await Assert.That(compacted.PerspectiveName).IsEqualTo("LedgerBalance");
    await Assert.That(compacted.SchemaVersion).IsEqualTo(3);
    await Assert.That(compacted.ThroughVersion).IsEqualTo(42L);
    await Assert.That(compacted.Model.GetProperty("balance").GetInt32()).IsEqualTo(140)
      .Because("The folded authoritative model rides on the Compacted origin.");

    // Truncated the folded detail through the anchor version, discard (ephemeral — no archive).
    await Assert.That(closer.LastCall!.Value.StreamId).IsEqualTo(streamId);
    await Assert.That(closer.LastCall!.Value.Through).IsEqualTo(42L);
    await Assert.That(closer.LastCall!.Value.Archive).IsFalse();
  }

  [Test]
  public async Task Compact_CloseBlocked_SurfacesCloseStatusAsync() {
    // If the A1 closer refuses (e.g. detail not yet consumed), the compaction surfaces that status rather than
    // claiming success.
    var log = new List<string>();
    var events = new FakeEventStore(log);
    var closer = new FakeCloser(log, new StreamCloseResult("blocked", 0));
    var snaps = new FakeSnapshotStore { Snapshot = (Guid.NewGuid(), _wrappedSnapshot("{}", 1)) };
    var compactor = _compactor(snaps, new FakeCoordinator(version: 10), events, closer);

    var result = await compactor.CompactAsync(Guid.NewGuid(), "P");

    await Assert.That(result.Status).IsEqualTo("blocked")
      .Because("A gate-blocked truncate is surfaced, not masked as compacted.");
  }
}
