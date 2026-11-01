// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>Folded by <see cref="DeletedArchiveRewindPerspective"/> only.</summary>
public sealed record DeletedArchiveRewindAdded : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public int Delta { get; init; }
}

public sealed class DeletedArchiveRewindModel {
  [StreamId]
  public Guid Id { get; set; }
  public int Total { get; set; }
}

/// <summary>Sums its events, so a replay that folds part of a stream shows in the total.</summary>
public sealed class DeletedArchiveRewindPerspective : IPerspectiveFor<DeletedArchiveRewindModel, DeletedArchiveRewindAdded> {
  public DeletedArchiveRewindModel Apply(DeletedArchiveRewindModel currentData, DeletedArchiveRewindAdded @event) {
    currentData.Total += @event.Delta;
    return currentData;
  }
}

/// <summary>
/// A rewind of a stream whose archive was deleted (#1964), over the real generated runner. A rewind reads through the
/// archive whenever its replay needs archived history: a replay from the stream's start (no snapshot before the late
/// event, or one this build cannot read) reads the archived prefix through the gap, and a replay from a snapshot an
/// archive covers reads the archived events after it. Once the archive was deleted that history is gone, and the replay
/// would fold the hot events alone and write a partial row as the whole. The rewind refuses instead, as a rebuild that
/// reads archived data skips such a stream (#1958): the row and the cursor are left as they are, the late event is named
/// as dropped so the worker retires its work, and one warning names the deleted archive. A rewind that never reads the
/// archive (from a snapshot no archive covers) is unaffected, and does not even ask the ledger.
/// </summary>
/// <docs>fundamentals/perspectives/rewind#deleted-archive</docs>
public class RewindDeletedArchiveTests {
  private const string NO_SNAPSHOT = "no snapshot";
  private const string STALE_SNAPSHOT = "stale snapshot";
  private const string NO_SNAPSHOT_STORE = "no snapshot store";

  [Test]
  [Arguments(ArchivedStreamExtent.PrefixDeleted, NO_SNAPSHOT)]
  [Arguments(ArchivedStreamExtent.PrefixDeleted, STALE_SNAPSHOT)]
  [Arguments(ArchivedStreamExtent.PrefixDeleted, NO_SNAPSHOT_STORE)]
  [Arguments(ArchivedStreamExtent.WholeDeleted, NO_SNAPSHOT)]
  public async Task ARewindFromTheStreamsStart_OfAStreamWhoseArchiveWasDeleted_IsRefused_AndNamesTheLateEventDroppedAsync(
      ArchivedStreamExtent extent, string floor) {
    var streamId = Guid.NewGuid();
    var store = new CountingEventStore();
    var hot = _envelope(new DeletedArchiveRewindAdded { StreamId = streamId, Delta = 1 });
    var late = _envelope(new DeletedArchiveRewindAdded { StreamId = streamId, Delta = 5 });
    store.Stream.AddRange([hot, late]);
    var snapshots = floor switch {
      NO_SNAPSHOT => new FloorSnapshotStore(floor: null),
      STALE_SNAPSHOT => new FloorSnapshotStore(new PerspectiveSnapshotFloor(
        hot.MessageId.Value, 3, _staleSnapshotOf(new DeletedArchiveRewindModel { Id = streamId, Total = 40 }), ArchivedThroughVersion: null)),
      _ => null,
    };
    var rows = new InMemoryRows();
    await rows.UpsertAsync(streamId, new DeletedArchiveRewindModel { Id = streamId, Total = 10 });
    var ledger = new LedgerExtents(new() { [streamId] = extent });
    var logs = new List<(LogLevel Level, string Message)>();
    var runner = _runner(store, rows, snapshots, ledger, logs);

    var completion = await runner.RewindAndRunAsync(streamId, _name, late.MessageId.Value, triggeringCommitSequence: 9, CancellationToken.None);

    await Assert.That(store.Reads).IsEqualTo(0)
      .Because("the replay would read the hot events alone, the archived history it needs being deleted");
    await Assert.That(completion.DroppedStragglers).IsEquivalentTo([late.MessageId.Value])
      .Because("the worker retires the late event's work only when the runner names it; left pending, every claim refuses it again");
    await Assert.That(completion.LastEventId).IsEqualTo(Guid.Empty).Because("nothing was replayed, so the cursor is not moved");
    await Assert.That(completion.Status).IsEqualTo(PerspectiveProcessingStatus.None);
    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Total).IsEqualTo(10).Because("the row is left as it was");
    var warnings = logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message).ToList();
    await Assert.That(warnings).Count().IsEqualTo(1)
      .Because("one refusal is one warning; a second announcing a full replay that never runs would mislead the reader");
    await Assert.That(warnings[0]).Contains("archive was deleted");
    await Assert.That(warnings[0]).Contains(extent.ToString());
    await Assert.That(warnings[0]).Contains(late.MessageId.Value.ToString()).Because("the warning names the event that was dropped");
  }

  [Test]
  public async Task ARewindFromASnapshotAnArchiveCovers_OfAStreamWhoseArchiveWasDeleted_IsRefusedAsync() {
    var streamId = Guid.NewGuid();
    var store = new CountingEventStore();
    var snapshotEvent = _envelope(new DeletedArchiveRewindAdded { StreamId = streamId, Delta = 1 });
    var late = _envelope(new DeletedArchiveRewindAdded { StreamId = streamId, Delta = 5 });
    store.Stream.AddRange([snapshotEvent, late]);
    // The snapshot sits at version 1 and the archive reached version 3: the replay from it needs versions 2 and 3.
    var snapshots = new FloorSnapshotStore(new PerspectiveSnapshotFloor(
      snapshotEvent.MessageId.Value, 1, _snapshotOf(new DeletedArchiveRewindModel { Id = streamId, Total = 1 }), ArchivedThroughVersion: 3));
    var rows = new InMemoryRows();
    await rows.UpsertAsync(streamId, new DeletedArchiveRewindModel { Id = streamId, Total = 10 });
    var logs = new List<(LogLevel Level, string Message)>();
    var runner = _runner(store, rows, snapshots, new LedgerExtents(new() { [streamId] = ArchivedStreamExtent.PrefixDeleted }), logs);

    var completion = await runner.RewindAndRunAsync(streamId, _name, late.MessageId.Value, triggeringCommitSequence: null, CancellationToken.None);

    await Assert.That(store.Reads).IsEqualTo(0).Because("the archived events after the snapshot are deleted");
    await Assert.That(completion.DroppedStragglers).IsEquivalentTo([late.MessageId.Value]);
    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Total).IsEqualTo(10);
    var warnings = logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message).ToList();
    await Assert.That(warnings).Count().IsEqualTo(1);
    await Assert.That(warnings[0]).Contains("archive was deleted");
  }

  [Test]
  public async Task ARewindFromASnapshotNoArchiveCovers_ReplaysTheHotEvents_EvenWhenThePrefixArchiveWasDeleted_AndAsksTheLedgerNothingAsync() {
    var streamId = Guid.NewGuid();
    var store = new CountingEventStore();
    var snapshotEvent = _envelope(new DeletedArchiveRewindAdded { StreamId = streamId, Delta = 1 });
    var late = _envelope(new DeletedArchiveRewindAdded { StreamId = streamId, Delta = 5 });
    store.Stream.AddRange([snapshotEvent, late]);
    var snapshots = new FloorSnapshotStore(new PerspectiveSnapshotFloor(
      snapshotEvent.MessageId.Value, 4, _snapshotOf(new DeletedArchiveRewindModel { Id = streamId, Total = 100 }), ArchivedThroughVersion: null));
    var rows = new InMemoryRows();
    var ledger = new LedgerExtents(new() { [streamId] = ArchivedStreamExtent.PrefixDeleted });
    var runner = _runner(store, rows, snapshots, ledger, logs: null);

    var completion = await runner.RewindAndRunAsync(streamId, _name, late.MessageId.Value, triggeringCommitSequence: null, CancellationToken.None);

    await Assert.That(completion.DroppedStragglers).IsEmpty();
    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Total).IsEqualTo(105)
      .Because("every event after a snapshot no archive covers is hot, so the deleted archive held nothing this replay needs");
    await Assert.That(ledger.Lookups).IsEqualTo(0).Because("a rewind that never reads the archive pays no ledger lookup");
  }

  [Test]
  [Arguments(ArchivedStreamExtent.Prefix)]
  [Arguments(ArchivedStreamExtent.Whole)]
  [Arguments(ArchivedStreamExtent.None)]
  public async Task ARewindFromTheStreamsStart_OfAStreamWhoseArchiveWasNotDeleted_ReplaysFromItsStartAsync(ArchivedStreamExtent extent) {
    var streamId = Guid.NewGuid();
    var store = new CountingEventStore();
    var first = _envelope(new DeletedArchiveRewindAdded { StreamId = streamId, Delta = 2 });
    var late = _envelope(new DeletedArchiveRewindAdded { StreamId = streamId, Delta = 5 });
    store.Stream.AddRange([first, late]);
    var rows = new InMemoryRows();
    // A stream never archived has no ledger entry, as the lookup reports it.
    var ledger = new LedgerExtents(extent == ArchivedStreamExtent.None ? [] : new() { [streamId] = extent });
    var runner = _runner(store, rows, new FloorSnapshotStore(floor: null), ledger, logs: null);

    var completion = await runner.RewindAndRunAsync(streamId, _name, late.MessageId.Value, triggeringCommitSequence: null, CancellationToken.None);

    await Assert.That(ledger.Lookups).IsEqualTo(1).Because("the control: the replay from the start asked the ledger");
    await Assert.That(completion.DroppedStragglers).IsEmpty();
    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Total).IsEqualTo(7)
      .Because("an archive that still holds its history is read through, and a stream never archived is all hot");
  }

  [Test]
  public async Task ARewindFromTheStreamsStart_InAHostWithNoArchiveLedger_ReplaysFromItsStartAsync() {
    var streamId = Guid.NewGuid();
    var store = new CountingEventStore();
    var late = _envelope(new DeletedArchiveRewindAdded { StreamId = streamId, Delta = 5 });
    store.Stream.Add(late);
    var rows = new InMemoryRows();
    var runner = _runner(store, rows, new FloorSnapshotStore(floor: null), ledger: null, logs: null);

    var completion = await runner.RewindAndRunAsync(streamId, _name, late.MessageId.Value, triggeringCommitSequence: null, CancellationToken.None);

    await Assert.That(completion.DroppedStragglers).IsEmpty();
    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Total).IsEqualTo(5);
  }

  // -------------------------------------------------------------------------------------------

  private static readonly string _name = TypeNameFormatter.FormatClrTypeName(typeof(DeletedArchiveRewindPerspective));

  // A fixed instant: nothing here depends on the wall clock.
  private static readonly DateTimeOffset _hopTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private static JsonDocument _snapshotOf(DeletedArchiveRewindModel model) {
    var info = (JsonTypeInfo<DeletedArchiveRewindModel>)JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(DeletedArchiveRewindModel));
    return SnapshotEnvelope.Wrap(JsonSerializer.SerializeToElement(model, info), SerializationVersion.CURRENT);
  }

  // A snapshot written before the snapshot envelope existed, which the current reader refuses to restore from.
  private static JsonDocument _staleSnapshotOf(DeletedArchiveRewindModel model) {
    var info = (JsonTypeInfo<DeletedArchiveRewindModel>)JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(DeletedArchiveRewindModel));
    return JsonSerializer.SerializeToDocument(model, info);
  }

  private static IPerspectiveRunner _runner(
      CountingEventStore store,
      InMemoryRows rows,
      IPerspectiveSnapshotStore? snapshots,
      LedgerExtents? ledger,
      List<(LogLevel Level, string Message)>? logs) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<DeletedArchiveRewindPerspective>();
    if (ledger is not null) {
      services.AddSingleton<IArchivedStreamExtents>(ledger);
    }
    var provider = services.BuildServiceProvider();
    // The real generated runner, resolved by name: naming a generated type in source does not compile in a workspace that
    // loads without running the generators (the formatting gate).
    var runnerType = typeof(RewindDeletedArchiveTests).Assembly
      .GetType("Whizbang.Core.Tests.Generated.DeletedArchiveRewindPerspectiveRunner", throwOnError: true)!;
    var logger = Activator.CreateInstance(typeof(CapturingLogger<>).MakeGenericType(runnerType), logs ?? [])!;
    var ctor = runnerType.GetConstructors().Single();
    var args = new object?[ctor.GetParameters().Length];
    args[0] = provider;
    args[1] = logger;
    args[2] = store;
    args[3] = rows;
    args[4] = provider.GetRequiredService<IServiceScopeFactory>();
    args[6] = snapshots;
    return (IPerspectiveRunner)ctor.Invoke(args);
  }

  private static MessageEnvelope<IEvent> _envelope(IEvent payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = _hopTime,
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  /// <summary>Records every log line the runner writes, by level.</summary>
  private sealed class CapturingLogger<T>(List<(LogLevel Level, string Message)> sink) : ILogger<T> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      sink.Add((logLevel, formatter(state, exception)));
  }

  /// <summary>What the stream lifecycle ledger says of each stream's archive, counting the lookups.</summary>
  private sealed class LedgerExtents(Dictionary<Guid, ArchivedStreamExtent> extents) : IArchivedStreamExtents {
    public int Lookups { get; private set; }

    public Task<IReadOnlyDictionary<Guid, ArchivedStreamExtent>> GetExtentsAsync(
        IReadOnlyCollection<Guid> streamIds, CancellationToken cancellationToken = default) {
      Lookups++;
      return Task.FromResult<IReadOnlyDictionary<Guid, ArchivedStreamExtent>>(
        extents.Where(e => streamIds.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value));
    }
  }

  /// <summary>Serves one snapshot floor, or none, to every rewind.</summary>
  private sealed class FloorSnapshotStore(PerspectiveSnapshotFloor? floor) : IPerspectiveSnapshotStore {
    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(floor is null ? null : ((Guid, JsonDocument)?)(floor.SnapshotEventId, floor.SnapshotData));

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      GetLatestSnapshotAsync(streamId, perspectiveName, ct);

    public Task<(Guid SnapshotEventId, int? SnapshotVersion, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeEventInStreamOrderAsync(
        Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult(floor is null ? null : ((Guid, int?, JsonDocument)?)(floor.SnapshotEventId, floor.SnapshotVersion, floor.SnapshotData));

    public Task<PerspectiveSnapshotFloor?> GetSnapshotFloorAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult(floor);

    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(floor is not null);

    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.CompletedTask;
  }

  /// <summary>Serves <see cref="Stream"/> to every polymorphic read, counting the reads a replay makes.</summary>
  private sealed class CountingEventStore : IEventStore {
    public List<MessageEnvelope<IEvent>> Stream { get; } = [];
    public int Reads { get; private set; }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      Reads++;
      await Task.Yield();
      var start = fromEventId is null ? 0 : Stream.FindIndex(e => e.MessageId.Value == fromEventId.Value) + 1;
      for (var i = Math.Max(start, 0); i < Stream.Count; i++) {
        yield return Stream[i];
      }
    }

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(Stream.ToList());

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult((long)Stream.Count);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }

  private sealed class InMemoryRows : IPerspectiveStore<DeletedArchiveRewindModel> {
    private readonly Dictionary<Guid, DeletedArchiveRewindModel> _rows = [];

    public Task<DeletedArchiveRewindModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_rows.TryGetValue(streamId, out var m) ? m : null);

    public Task UpsertAsync(Guid streamId, DeletedArchiveRewindModel model, CancellationToken cancellationToken = default) {
      _rows[streamId] = model;
      return Task.CompletedTask;
    }

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, DeletedArchiveRewindModel model, IDictionary<string, object?> physicalFieldValues, PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
      UpsertAsync(streamId, model, cancellationToken);

    public Task<DeletedArchiveRewindModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      Task.FromResult<DeletedArchiveRewindModel?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, DeletedArchiveRewindModel model, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      Task.CompletedTask;

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) {
      _rows.Remove(streamId);
      return Task.CompletedTask;
    }

    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      Task.CompletedTask;
  }
}
