// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using static Whizbang.Core.Tests.Perspectives.SnapshotBootstrapOutcomeTests;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>An ephemeral event folded by <see cref="NoFloorPresencePerspective"/> only.</summary>
[Ephemeral]
public sealed record NoFloorPresencePinged : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

/// <summary>An ephemeral event folded by <see cref="KeptFloorPresencePerspective"/> only.</summary>
[Ephemeral]
public sealed record KeptFloorPresencePinged : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

/// <summary>The row both presence perspectives fold.</summary>
public sealed class FloorPresenceModel {
  [StreamId]
  public Guid Id { get; set; }
  public int Pings { get; set; }
}

/// <summary>Applies only ephemeral events and does not opt in: it keeps no rewind floor.</summary>
public sealed class NoFloorPresencePerspective : IPerspectiveFor<FloorPresenceModel, NoFloorPresencePinged> {
  public FloorPresenceModel Apply(FloorPresenceModel currentData, NoFloorPresencePinged @event) {
    currentData.Pings++;
    return currentData;
  }
}

/// <summary>Applies only ephemeral events and opts in to a rewind floor.</summary>
[KeepEphemeralRewindFloor]
public sealed class KeptFloorPresencePerspective : IPerspectiveFor<FloorPresenceModel, KeptFloorPresencePinged> {
  public FloorPresenceModel Apply(FloorPresenceModel currentData, KeptFloorPresencePinged @event) {
    currentData.Pings++;
    return currentData;
  }
}

/// <summary>
/// #2295 over the real generated runners: a perspective that applies only ephemeral events takes no snapshots by
/// default, live or bootstrapped, and one that opts in with <see cref="KeepEphemeralRewindFloorAttribute"/> takes them on
/// the ephemeral cadence as before.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#snapshots-follow-what-a-read-model-consumes</docs>
public class EphemeralRewindFloorRunnerTests {
  private static readonly DateTimeOffset _hopTime = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

  // Every event is due a snapshot, so a perspective that takes snapshots takes one on the first run.
  private static PerspectiveSnapshotOptions _everyEvent() => new() { EphemeralSnapshotEveryNEvents = 1, SnapshotEveryNEvents = 1 };

  [Test]
  public async Task ALiveRun_OfAPerspectiveThatKeepsNoFloor_WritesNoSnapshotAsync() {
    var streamId = Guid.NewGuid();
    var snapshots = new RecordingSnapshotStore();
    var rows = new InMemoryRows<FloorPresenceModel>();
    var runner = _runner<NoFloorPresencePerspective, FloorPresenceModel>(rows, snapshots, enabled: true, logs: null, _everyEvent());

    await runner.RunWithEventsAsync(streamId, _name<NoFloorPresencePerspective>(), null,
      [_envelope(new NoFloorPresencePinged { StreamId = streamId })], CancellationToken.None);

    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Pings).IsEqualTo(1).Because("the event is applied as ever");
    await Assert.That(snapshots.Written).IsEmpty()
      .Because("a perspective that applies only ephemeral events keeps no rewind floor by default");
  }

  [Test]
  public async Task ALiveRun_OfAPerspectiveThatOptsIn_WritesItsSnapshotAsync() {
    var streamId = Guid.NewGuid();
    var snapshots = new RecordingSnapshotStore();
    var rows = new InMemoryRows<FloorPresenceModel>();
    var runner = _runner<KeptFloorPresencePerspective, FloorPresenceModel>(rows, snapshots, enabled: true, logs: null, _everyEvent());
    var ping = _envelope(new KeptFloorPresencePinged { StreamId = streamId });

    await runner.RunWithEventsAsync(streamId, _name<KeptFloorPresencePerspective>(), null, [ping], CancellationToken.None);

    await Assert.That(snapshots.Written).IsEquivalentTo([(streamId, ping.MessageId.Value)])
      .Because("the positive control: [KeepEphemeralRewindFloor] keeps the floor on the ephemeral cadence");
  }

  [Test]
  public async Task ABootstrap_OfAPerspectiveThatKeepsNoFloor_WritesNothing_AndReportsSnapshotsOffAsync() {
    var streamId = Guid.NewGuid();
    var rows = new InMemoryRows<FloorPresenceModel>();
    await rows.UpsertAsync(streamId, new FloorPresenceModel { Id = streamId, Pings = 4 });
    var snapshots = new RecordingSnapshotStore();
    var runner = _runner<NoFloorPresencePerspective, FloorPresenceModel>(rows, snapshots, enabled: true, logs: null);

    var outcome = await runner.TryBootstrapSnapshotAsync(streamId, _name<NoFloorPresencePerspective>(), Guid.NewGuid());

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.SnapshotsOff);
    await Assert.That(snapshots.Written).IsEmpty();
  }

  [Test]
  public async Task ABootstrap_OfAPerspectiveThatOptsIn_WritesTheSnapshotAsync() {
    var streamId = Guid.NewGuid();
    var rows = new InMemoryRows<FloorPresenceModel>();
    await rows.UpsertAsync(streamId, new FloorPresenceModel { Id = streamId, Pings = 4 });
    var snapshots = new RecordingSnapshotStore();
    var runner = _runner<KeptFloorPresencePerspective, FloorPresenceModel>(rows, snapshots, enabled: true, logs: null);

    var outcome = await runner.TryBootstrapSnapshotAsync(streamId, _name<KeptFloorPresencePerspective>(), Guid.NewGuid());

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.Written);
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
}
