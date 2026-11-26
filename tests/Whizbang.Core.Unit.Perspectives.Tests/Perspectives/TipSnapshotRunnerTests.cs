// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using static Whizbang.Core.Tests.Perspectives.ReapBoundarySnapshotRunnerTests;
using static Whizbang.Core.Tests.Perspectives.SnapshotBootstrapOutcomeTests;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>An ephemeral event folded by <see cref="MixedTipPerspective"/>.</summary>
[Ephemeral]
public sealed record MixedTipPinged : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

/// <summary>A durable event folded by <see cref="MixedTipPerspective"/>.</summary>
public sealed record MixedTipNoted : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

/// <summary>The row <see cref="MixedTipPerspective"/> folds: how many events of either kind it holds.</summary>
public sealed class MixedTipModel {
  [StreamId]
  public Guid Id { get; set; }
  public int Total { get; set; }
}

/// <summary>Applies an ephemeral and a durable event, so it keeps a tip snapshot beside its floor (#2463).</summary>
[DangerouslyAllowMixedEphemeralAndSourcedEvents(Streams = MixedStreams.Allow)]
public sealed class MixedTipPerspective
    : IPerspectiveFor<MixedTipModel, MixedTipPinged>, IPerspectiveFor<MixedTipModel, MixedTipNoted> {
  public MixedTipModel Apply(MixedTipModel currentData, MixedTipPinged @event) {
    currentData.Total++;
    return currentData;
  }

  public MixedTipModel Apply(MixedTipModel currentData, MixedTipNoted @event) {
    currentData.Total++;
    return currentData;
  }
}

/// <summary>
/// #2463 over the real generated runner: a perspective that applies both ephemeral and durable events keeps two
/// snapshots. Its floor is the reap-boundary snapshot, moved only by the reap-boundary bootstrap, exactly as for any
/// ephemeral perspective. Its tip is written by the live and rewind cadences and never counts as a floor, so a rewind
/// starts near the tip instead of replaying every durable event since the last reap boundary. Each rule that decides
/// when the tip may be restored, and when it goes, is locked here; the store's half is locked on both drivers.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#the-tip-snapshot</docs>
public class TipSnapshotRunnerTests {
  private static readonly DateTimeOffset _hopTime = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task ALiveSnapshot_IsWrittenAsTheTip_AndTheBoundaryFloorStaysAsync() {
    var (streamId, events, store) = _stream("PPPNPN");
    var rows = new InMemoryRows<MixedTipModel>();
    await rows.UpsertAsync(streamId, new MixedTipModel { Id = streamId, Total = 5 });
    var snapshots = new TipAwareSnapshotStore(store);
    var runner = _runner<MixedTipPerspective, MixedTipModel>(store, rows, snapshots, _everyEvent());
    var name = _name<MixedTipPerspective>();
    await runner.TryBootstrapSnapshotThroughAsync(streamId, name, lastProcessedEventId: events[4], boundaryEventId: events[2]);
    store.Reap(events[0], events[1], events[2]);

    await runner.RunWithEventsAsync(streamId, name, events[4], [store.Envelope(events[5])], CancellationToken.None);

    await Assert.That(snapshots.Floors).IsEquivalentTo([events[2]])
      .Because("only the reap-boundary bootstrap moves the floor");
    await Assert.That(snapshots.Tips).IsEquivalentTo([events[5]])
      .Because("the live cadence writes the tip, so a rewind need not replay every durable event since the boundary");
    await Assert.That(snapshots.TotalAt(events[5])).IsEqualTo(6);
  }

  [Test]
  public async Task ALateEventAboveTheTip_RewindsFromTheTipAsync() {
    var (streamId, events, store) = _stream("PNPNPN");
    var rows = new InMemoryRows<MixedTipModel>();
    var snapshots = new TipAwareSnapshotStore(store);
    var runner = _runner<MixedTipPerspective, MixedTipModel>(store, rows, snapshots);
    var name = _name<MixedTipPerspective>();
    await _floorAsync(runner, rows, snapshots, streamId, events[1], total: 2);
    // A tip whose model is marked, so the test can tell which snapshot the rewind restored.
    await _tipAsync(runner, rows, snapshots, streamId, events[3], total: 100);

    var result = await runner.RewindAndRunAsync(streamId, name, triggeringEventId: events[4]);

    await Assert.That(result.DroppedStragglers).IsEmpty();
    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Total).IsEqualTo(102)
      .Because("the tip is the newest snapshot before the late event, so the rewind restores it and replays two events");
  }

  [Test]
  public async Task ALateEventBelowTheTip_RewindsFromTheFloor_AndTheTipThatMissedItGoesAsync() {
    var (streamId, events, store) = _stream("PNPNPN");
    var rows = new InMemoryRows<MixedTipModel>();
    var snapshots = new TipAwareSnapshotStore(store);
    var runner = _runner<MixedTipPerspective, MixedTipModel>(store, rows, snapshots);
    var name = _name<MixedTipPerspective>();
    await _floorAsync(runner, rows, snapshots, streamId, events[1], total: 2);
    await _tipAsync(runner, rows, snapshots, streamId, events[4], total: 100);

    var result = await runner.RewindAndRunAsync(streamId, name, triggeringEventId: events[2]);

    await Assert.That(result.DroppedStragglers).IsEmpty();
    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Total).IsEqualTo(6)
      .Because("the floor is the newest snapshot before the late event: two events, then the four from the late one on");
    await Assert.That(snapshots.TipRemovalsFrom).Contains(events[2])
      .Because("a tip at or above a late event holds a fold without it, so a later rewind restoring it would lose it");
    await Assert.That(snapshots.Tips.Select(snapshots.TotalAt)).DoesNotContain(100)
      .Because("the tip that missed the late event is gone");
  }

  [Test]
  public async Task ATipTheRewindCannotRead_FallsBackToTheFloor_InsteadOfDroppingTheLateEventAsync() {
    var (streamId, events, store) = _stream("PNPNPN");
    var rows = new InMemoryRows<MixedTipModel>();
    var snapshots = new TipAwareSnapshotStore(store);
    var runner = _runner<MixedTipPerspective, MixedTipModel>(store, rows, snapshots);
    var name = _name<MixedTipPerspective>();
    await _floorAsync(runner, rows, snapshots, streamId, events[1], total: 2);
    snapshots.Rows.Add((events[3], "{}", IsTip: true));

    var result = await runner.RewindAndRunAsync(streamId, name, triggeringEventId: events[4]);

    await Assert.That(result.DroppedStragglers).IsEmpty()
      .Because("the floor below the tip still serves the late event");
    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Total).IsEqualTo(6)
      .Because("the rewind restores the floor's two events and replays the four after it");
  }

  [Test]
  public async Task ATipThatIsNoFloorInReplayOrder_FallsBackToTheFloorAsync() {
    var (streamId, events, store) = _stream("PNPNPN");
    var rows = new InMemoryRows<MixedTipModel>();
    var snapshots = new TipAwareSnapshotStore(store);
    var runner = _runner<MixedTipPerspective, MixedTipModel>(store, rows, snapshots);
    var name = _name<MixedTipPerspective>();
    await _floorAsync(runner, rows, snapshots, streamId, events[1], total: 2);
    await _tipAsync(runner, rows, snapshots, streamId, events[3], total: 100);
    // The tip records no version, so the replay's order cannot place it (ReplayFloor.Qualifies refuses it).
    snapshots.Unplaced.Add(events[3]);

    var result = await runner.RewindAndRunAsync(streamId, name, triggeringEventId: events[4]);

    await Assert.That(result.DroppedStragglers).IsEmpty();
    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Total).IsEqualTo(6)
      .Because("a tip the replay's order refuses gives way to the floor, never to a dropped late event");
  }

  [Test]
  public async Task ARewind_WritesItsSnapshotAsTheTip_AndKeepsTheFloorItRestoredAsync() {
    var (streamId, events, store) = _stream("PPPNP");
    var rows = new InMemoryRows<MixedTipModel>();
    await rows.UpsertAsync(streamId, new MixedTipModel { Id = streamId, Total = 4 });
    var snapshots = new TipAwareSnapshotStore(store);
    var runner = _runner<MixedTipPerspective, MixedTipModel>(store, rows, snapshots);
    var name = _name<MixedTipPerspective>();
    await runner.TryBootstrapSnapshotThroughAsync(streamId, name, lastProcessedEventId: events[4], boundaryEventId: events[2]);
    store.Reap(events[0], events[1], events[2]);

    _ = await runner.RewindAndRunAsync(streamId, name, triggeringEventId: events[3]);

    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Total).IsEqualTo(5).Because("the control: the rewind ran");
    await Assert.That(snapshots.Floors).IsEquivalentTo([events[2]]).Because("a rewind never moves the floor");
    await Assert.That(snapshots.Tips).IsEquivalentTo([events[4]])
      .Because("the rewind's own snapshot sits at the tip, so it is the tip");
    await Assert.That(snapshots.TotalAt(events[4])).IsEqualTo(5);
  }

  [Test]
  public async Task ARewindsIntermediateSnapshots_AreWrittenAsTheTip_NeverAsAFloorAsync() {
    var (streamId, events, store) = _stream("PPNPNPN");
    var rows = new InMemoryRows<MixedTipModel>();
    await rows.UpsertAsync(streamId, new MixedTipModel { Id = streamId, Total = 6 });
    var snapshots = new TipAwareSnapshotStore(store);
    var runner = _runner<MixedTipPerspective, MixedTipModel>(
      store, rows, snapshots, new PerspectiveSnapshotOptions { Enabled = true, RewindSnapshotIntervalEvents = 1 });
    var name = _name<MixedTipPerspective>();
    await runner.TryBootstrapSnapshotThroughAsync(streamId, name, lastProcessedEventId: events[6], boundaryEventId: events[1]);

    _ = await runner.RewindAndRunAsync(streamId, name, triggeringEventId: events[2]);

    await Assert.That(snapshots.TipWrites).Count().IsGreaterThan(1)
      .Because("the replay's intermediate snapshots are written on its cadence, as tips");
    await Assert.That(snapshots.Floors).IsEquivalentTo([events[1]]).Because("none of them moves the floor");
  }

  [Test]
  public async Task ABootstrapThroughTheBoundary_FoldsFromTheFloor_NotFromATipBelowTheBoundaryAsync() {
    var (streamId, events, store) = _stream("PNPNPN");
    var rows = new InMemoryRows<MixedTipModel>();
    var snapshots = new TipAwareSnapshotStore(store);
    var runner = _runner<MixedTipPerspective, MixedTipModel>(store, rows, snapshots);
    var name = _name<MixedTipPerspective>();
    await _floorAsync(runner, rows, snapshots, streamId, events[0], total: 1);
    await _tipAsync(runner, rows, snapshots, streamId, events[2], total: 100);
    await rows.UpsertAsync(streamId, new MixedTipModel { Id = streamId, Total = 6 });

    var outcome = await runner.TryBootstrapSnapshotThroughAsync(
      streamId, name, lastProcessedEventId: events[5], boundaryEventId: events[3]);

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.Written);
    await Assert.That(snapshots.TotalAt(events[3])).IsEqualTo(4)
      .Because("the floor is folded from the floor below it, so it never depends on a tip");
    await Assert.That(snapshots.Tips).IsEmpty().Because("a tip at or below the floor can never be restored, so it goes");
  }

  [Test]
  public async Task APerspectiveThatAppliesOnlyEphemeralEvents_NeverWritesATipAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var store = new VersionedEventStore();
    var events = Enumerable.Range(0, 6)
      .Select(_ => store.Append(streamId, new KeptFloorPresencePinged { StreamId = streamId }, _hopTime)).ToArray();
    var rows = new InMemoryRows<FloorPresenceModel>();
    await rows.UpsertAsync(streamId, new FloorPresenceModel { Id = streamId, Pings = 5 });
    var snapshots = new TipAwareSnapshotStore(store);
    var runner = _runner<KeptFloorPresencePerspective, FloorPresenceModel>(store, rows, snapshots, _everyEvent());
    var name = _name<KeptFloorPresencePerspective>();
    await runner.TryBootstrapSnapshotThroughAsync(streamId, name, lastProcessedEventId: events[4], boundaryEventId: events[2]);

    await runner.RunWithEventsAsync(streamId, name, events[4], [store.Envelope(events[5])], CancellationToken.None);
    _ = await runner.RewindAndRunAsync(streamId, name, triggeringEventId: events[3]);

    await Assert.That(snapshots.TipWrites).IsEmpty().Because("its one snapshot is its floor, as before");
    await Assert.That(snapshots.Floors).IsEquivalentTo([events[2]]);
  }

  // ---------------------------------------------------------------------------------------------

  // 'P' is an ephemeral ping, 'N' a durable note, at versions 1, 2, 3, ... in the order given.
  private static (Guid StreamId, Guid[] Events, VersionedEventStore Store) _stream(string kinds) {
    var streamId = (Guid)TrackedGuid.New();
    var store = new VersionedEventStore();
    var events = kinds
      .Select(kind => kind == 'P'
        ? store.Append(streamId, new MixedTipPinged { StreamId = streamId }, _hopTime)
        : store.Append(streamId, new MixedTipNoted { StreamId = streamId }, _hopTime))
      .ToArray();
    return (streamId, events, store);
  }

  // A floor at the event whose model holds the given total, written by the runner's own bootstrap at the cursor.
  private static async Task _floorAsync(
      IPerspectiveRunner runner, InMemoryRows<MixedTipModel> rows, TipAwareSnapshotStore snapshots, Guid streamId, Guid at,
      int total) {
    await rows.UpsertAsync(streamId, new MixedTipModel { Id = streamId, Total = total });
    var outcome = await runner.TryBootstrapSnapshotAsync(streamId, _name<MixedTipPerspective>(), at);
    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.Written);
    await Assert.That(snapshots.Floors).Contains(at);
  }

  // A tip at the event whose model holds the given total: a bootstrap's snapshot, then marked the tip.
  private static async Task _tipAsync(
      IPerspectiveRunner runner, InMemoryRows<MixedTipModel> rows, TipAwareSnapshotStore snapshots, Guid streamId, Guid at,
      int total) {
    await _floorAsync(runner, rows, snapshots, streamId, at, total);
    snapshots.MakeTip(at);
  }

  private static PerspectiveSnapshotOptions _everyEvent() => new() { Enabled = true, EphemeralSnapshotEveryNEvents = 1 };

  private static IPerspectiveRunner _runner<TPerspective, TModel>(
      IEventStore events, InMemoryRows<TModel> rows, IPerspectiveSnapshotStore snapshots,
      PerspectiveSnapshotOptions? options = null)
      where TPerspective : class
      where TModel : class {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<TPerspective>();
    var provider = services.BuildServiceProvider();
    var runnerType = typeof(TipSnapshotRunnerTests).Assembly
      .GetType($"Whizbang.Core.Unit.Perspectives.Tests.Generated.{typeof(TPerspective).Name}Runner", throwOnError: true)!;
    var logger = Activator.CreateInstance(typeof(CapturingLogger<>).MakeGenericType(runnerType), new List<(LogLevel, string)>())!;
    var ctor = runnerType.GetConstructors().Single();
    var args = new object?[ctor.GetParameters().Length];
    args[0] = provider;
    args[1] = logger;
    args[2] = events;
    args[3] = rows;
    args[4] = provider.GetRequiredService<IServiceScopeFactory>();
    args[6] = snapshots;
    args[7] = Options.Create(options ?? new PerspectiveSnapshotOptions { Enabled = true });
    return (IPerspectiveRunner)ctor.Invoke(args);
  }

  /// <summary>
  /// A snapshot store that keeps floors and one tip, to the contract the Postgres stores keep (#2463): a tip replaces
  /// the previous tip and is kept only above the newest floor; a floor written at the tip's event takes its place;
  /// pruning counts floors alone and removes a tip at or below the newest floor; the floor lookup never returns a tip.
  /// </summary>
  internal sealed class TipAwareSnapshotStore(VersionedEventStore events) : IPerspectiveSnapshotStore {
    public List<(Guid EventId, string Data, bool IsTip)> Rows { get; } = [];

    /// <summary>Snapshots placed in the stream by no version, as a snapshot written before versions were recorded.</summary>
    public HashSet<Guid> Unplaced { get; } = [];

    public List<Guid> TipWrites { get; } = [];

    public List<Guid> TipRemovalsFrom { get; } = [];

    public IEnumerable<Guid> Floors => Rows.Where(r => !r.IsTip).Select(r => r.EventId);

    public IEnumerable<Guid> Tips => Rows.Where(r => r.IsTip).Select(r => r.EventId);

    public void MakeTip(Guid eventId) {
      var index = Rows.FindIndex(r => r.EventId == eventId);
      Rows[index] = Rows[index] with { IsTip = true };
    }

    public int TotalAt(Guid eventId) {
      using var document = JsonDocument.Parse(Rows.Single(r => r.EventId == eventId).Data);
      return _total(document.RootElement);
    }

    // The model sits somewhere inside the snapshot envelope; its Total is the one number the model has.
    private static int _total(JsonElement element) {
      if (element.ValueKind == JsonValueKind.Object) {
        foreach (var property in element.EnumerateObject()) {
          if (string.Equals(property.Name, "Total", StringComparison.OrdinalIgnoreCase)) {
            return property.Value.GetInt32();
          }
          if (_total(property.Value) is var nested and >= 0) {
            return nested;
          }
        }
      }
      return -1;
    }

    private long _version(Guid eventId) => events.VersionOf(eventId) ?? long.MaxValue;

    private long _newestFloor() => Rows.Where(r => !r.IsTip).Select(r => _version(r.EventId)).DefaultIfEmpty(0).Max();

    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) {
      Rows.RemoveAll(r => r.EventId == snapshotEventId);
      Rows.Add((snapshotEventId, snapshotData.RootElement.GetRawText(), IsTip: false));
      return Task.CompletedTask;
    }

    public Task CreateTipSnapshotAsync(
        Guid streamId, string perspectiveName, Guid snapshotEventId, long? snapshotCommitSequence, JsonDocument snapshotData,
        long? collectivesThroughSequence, CancellationToken ct = default) {
      TipWrites.Add(snapshotEventId);
      Rows.RemoveAll(r => r.IsTip);
      if (_version(snapshotEventId) > _newestFloor()) {
        Rows.Add((snapshotEventId, snapshotData.RootElement.GetRawText(), IsTip: true));
      }
      return Task.CompletedTask;
    }

    public Task<PerspectiveSnapshotFloor?> GetSnapshotFloorAsync(
        Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult(_floorBefore(beforeEventId, floorsOnly: false));

    public Task<PerspectiveSnapshotFloor?> GetFloorSnapshotAsync(
        Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult(_floorBefore(beforeEventId, floorsOnly: true));

    private PerspectiveSnapshotFloor? _floorBefore(Guid beforeEventId, bool floorsOnly) {
      var before = _version(beforeEventId);
      return Rows
        .Where(r => (!floorsOnly || !r.IsTip) && _version(r.EventId) < before)
        .OrderByDescending(r => _version(r.EventId))
        .Select(r => new PerspectiveSnapshotFloor(
          r.EventId, Unplaced.Contains(r.EventId) ? null : (int)_version(r.EventId), JsonDocument.Parse(r.Data),
          ArchivedThroughVersion: null) { IsTip = r.IsTip })
        .FirstOrDefault();
    }

    public Task DeleteTipSnapshotFromAsync(Guid streamId, string perspectiveName, Guid eventId, CancellationToken ct = default) {
      TipRemovalsFrom.Add(eventId);
      Rows.RemoveAll(r => r.IsTip && _version(r.EventId) >= _version(eventId));
      return Task.CompletedTask;
    }

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<(Guid, JsonDocument)?>(null);

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult<(Guid, JsonDocument)?>(null);

    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(Rows.Count > 0);

    // Keeps the newest keepCount floors, in the order they were written, and a tip only above the newest floor.
    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) {
      var floors = Rows.Where(r => !r.IsTip).ToList();
      foreach (var gone in floors.Take(Math.Max(0, floors.Count - keepCount))) {
        Rows.Remove(gone);
      }
      var newest = _newestFloor();
      Rows.RemoveAll(r => r.IsTip && _version(r.EventId) <= newest);
      return Task.CompletedTask;
    }

    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) {
      Rows.Clear();
      return Task.CompletedTask;
    }
  }
}
