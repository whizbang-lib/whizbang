// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using static Whizbang.Core.Tests.Perspectives.SnapshotBootstrapOutcomeTests;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// #2296 over the real generated runner: before a maintenance cycle reaps an ephemeral stream's consumed bodies, the
/// reap-driven pass takes a snapshot THROUGH the reap boundary (the last body it is about to reap), not at the cursor.
/// A snapshot at the cursor was a floor only for a late event placed after it; a late event below the cursor found no
/// floor once the bodies below were reaped, and an ephemeral perspective dropped it. The boundary snapshot holds exactly
/// the events up to the boundary, so a late event below the cursor but above the boundary rewinds from it.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#the-reap-boundary-snapshot</docs>
public class ReapBoundarySnapshotRunnerTests {
  private static readonly DateTimeOffset _hopTime = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task ABootstrapThroughTheBoundary_SnapshotsTheModelAsOfTheBoundary_NotTheRowAtTheCursorAsync() {
    var (streamId, events, store) = _stream(5);
    var rows = new InMemoryRows<FloorPresenceModel>();
    await rows.UpsertAsync(streamId, new FloorPresenceModel { Id = streamId, Pings = 4 });
    var snapshots = new VersionedSnapshotStore(store);
    var runner = _runner(store, rows, snapshots);

    var outcome = await runner.TryBootstrapSnapshotThroughAsync(
      streamId, _name<KeptFloorPresencePerspective>(), lastProcessedEventId: events[4], boundaryEventId: events[2]);

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.Written);
    await Assert.That(snapshots.Written.Select(w => w.EventId)).IsEquivalentTo([events[2]])
      .Because("the snapshot is taken at the reap boundary, the last body about to be reaped");
    await Assert.That(snapshots.PingsAt(events[2])).IsEqualTo(3)
      .Because("it holds exactly the events up to the boundary, folded from the bodies still present");
  }

  [Test]
  public async Task ALateEventBelowTheCursorAndAboveTheBoundary_RewindsFromTheBoundarySnapshot_AfterTheBodiesBelowAreReapedAsync() {
    // The row holds events 1, 2, 3 and 5; event 4 is late. The cursor is at event 5 and the cycle reaps through event 3.
    var (streamId, events, store) = _stream(5);
    var rows = new InMemoryRows<FloorPresenceModel>();
    await rows.UpsertAsync(streamId, new FloorPresenceModel { Id = streamId, Pings = 4 });
    var snapshots = new VersionedSnapshotStore(store);
    var runner = _runner(store, rows, snapshots);
    var name = _name<KeptFloorPresencePerspective>();

    await runner.TryBootstrapSnapshotThroughAsync(streamId, name, lastProcessedEventId: events[4], boundaryEventId: events[2]);
    store.Reap(events[0], events[1], events[2]);
    var result = await runner.RewindAndRunAsync(streamId, name, triggeringEventId: events[3]);

    await Assert.That(result.DroppedStragglers).IsEmpty()
      .Because("the boundary snapshot is a floor below the late event, so it is not dropped");
    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Pings).IsEqualTo(5)
      .Because("the rewind restores the three events the snapshot holds and replays events 4 and 5");
  }

  [Test]
  public async Task ABootstrapWhoseBoundaryIsTheCursor_SnapshotsTheRowAtTheCursor_AsBeforeAsync() {
    var (streamId, events, store) = _stream(3);
    var rows = new InMemoryRows<FloorPresenceModel>();
    await rows.UpsertAsync(streamId, new FloorPresenceModel { Id = streamId, Pings = 3 });
    var snapshots = new VersionedSnapshotStore(store);
    var runner = _runner(store, rows, snapshots);

    var outcome = await runner.TryBootstrapSnapshotThroughAsync(
      streamId, _name<KeptFloorPresencePerspective>(), lastProcessedEventId: events[2], boundaryEventId: events[2]);

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.Written);
    await Assert.That(snapshots.Written.Select(w => w.EventId)).IsEquivalentTo([events[2]]);
  }

  // ---------------------------------------------------------------------------------------------

  private static (Guid StreamId, Guid[] Events, VersionedEventStore Store) _stream(int count) {
    var streamId = (Guid)TrackedGuid.New();
    var store = new VersionedEventStore();
    var events = new Guid[count];
    for (var i = 0; i < count; i++) {
      events[i] = store.Append(streamId, new KeptFloorPresencePinged { StreamId = streamId }, _hopTime);
    }
    return (streamId, events, store);
  }

  private static IPerspectiveRunner _runner(
      IEventStore events, InMemoryRows<FloorPresenceModel> rows, IPerspectiveSnapshotStore snapshots) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<KeptFloorPresencePerspective>();
    var provider = services.BuildServiceProvider();
    var runnerType = typeof(ReapBoundarySnapshotRunnerTests).Assembly
      .GetType($"Whizbang.Core.Unit.Perspectives.Tests.Generated.{nameof(KeptFloorPresencePerspective)}Runner", throwOnError: true)!;
    var logger = Activator.CreateInstance(typeof(CapturingLogger<>).MakeGenericType(runnerType), new List<(LogLevel, string)>())!;
    var ctor = runnerType.GetConstructors().Single();
    var args = new object?[ctor.GetParameters().Length];
    args[0] = provider;
    args[1] = logger;
    args[2] = events;
    args[3] = rows;
    args[4] = provider.GetRequiredService<IServiceScopeFactory>();
    args[6] = snapshots;
    args[7] = Options.Create(new PerspectiveSnapshotOptions { Enabled = true });
    return (IPerspectiveRunner)ctor.Invoke(args);
  }

  /// <summary>
  /// An event store of one perspective's events with their stream versions and commit sequences (the version). A reaped
  /// body is no longer read, as an ephemeral stream's reaped history is not, but its position is still known.
  /// </summary>
  internal sealed class VersionedEventStore : IEventStore {
    private readonly List<(Guid StreamId, MessageEnvelope<IEvent> Envelope, long Version)> _events = [];
    private readonly HashSet<Guid> _reaped = [];

    public Guid Append(Guid streamId, IEvent payload, DateTimeOffset at) {
      var envelope = new MessageEnvelope<IEvent> {
        MessageId = MessageId.New(),
        Payload = payload,
        Hops = [new MessageHop { Type = HopType.Current, ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = at }],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      };
      var version = _events.Count(e => e.StreamId == streamId) + 1L;
      envelope.LocalStreamVersion = version;
      envelope.LocalCommitSequence = version;
      envelope.IsFirstInLocalStream = version == 1;
      _events.Add((streamId, envelope, version));
      return envelope.MessageId.Value;
    }

    public void Reap(params Guid[] eventIds) => _reaped.UnionWith(eventIds);

    public long? VersionOf(Guid eventId) => _events.Where(e => e.Envelope.MessageId.Value == eventId).Select(e => (long?)e.Version).FirstOrDefault();

    public Task<long?> GetCommitSequenceAsync(Guid eventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(VersionOf(eventId));

    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
      var after = fromEventId is Guid from ? VersionOf(from) ?? 0 : 0;
      foreach (var (_, envelope, version) in _events.Where(e => e.StreamId == streamId).OrderBy(e => e.Version).ToList()) {
        if (version > after && !_reaped.Contains(envelope.MessageId.Value)) {
          yield return envelope;
        }
      }
      await Task.CompletedTask;
    }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(0L);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }

  /// <summary>Keeps every snapshot with its data and places it in the stream by its event's version.</summary>
  internal sealed class VersionedSnapshotStore(VersionedEventStore events) : IPerspectiveSnapshotStore {
    public List<(Guid EventId, string Data)> Written { get; } = [];

    public int PingsAt(Guid eventId) {
      using var document = JsonDocument.Parse(Written.Single(w => w.EventId == eventId).Data);
      return _pings(document.RootElement);
    }

    // The model sits somewhere inside the snapshot envelope; its Pings is the one number the model has.
    private static int _pings(JsonElement element) {
      if (element.ValueKind == JsonValueKind.Object) {
        foreach (var property in element.EnumerateObject()) {
          if (string.Equals(property.Name, "Pings", StringComparison.OrdinalIgnoreCase)) {
            return property.Value.GetInt32();
          }
          if (_pings(property.Value) is var nested and >= 0) {
            return nested;
          }
        }
      }
      return -1;
    }

    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) {
      Written.Add((snapshotEventId, snapshotData.RootElement.GetRawText()));
      return Task.CompletedTask;
    }

    public Task<(Guid SnapshotEventId, int? SnapshotVersion, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeEventInStreamOrderAsync(
        Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) {
      var before = events.VersionOf(beforeEventId) ?? long.MaxValue;
      var floor = Written
        .Select(w => (w.EventId, w.Data, Version: events.VersionOf(w.EventId) ?? long.MaxValue))
        .Where(w => w.Version < before)
        .OrderByDescending(w => w.Version)
        .Select(w => ((Guid, int?, JsonDocument)?)(w.EventId, (int)w.Version, JsonDocument.Parse(w.Data)))
        .FirstOrDefault();
      return Task.FromResult(floor);
    }

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<(Guid, JsonDocument)?>(null);

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult<(Guid, JsonDocument)?>(null);

    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(Written.Count > 0);

    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.CompletedTask;
  }
}
