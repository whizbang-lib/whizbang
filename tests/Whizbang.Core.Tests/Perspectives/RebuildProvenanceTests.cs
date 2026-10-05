// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Events.System;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Provenance on the rebuild lifecycle events (#1135). A rebuild request is acknowledged as accepted, not as
/// done, and the command is broadcast to every service so each can rebuild only what it hosts. These tests fix
/// the parts of that contract a caller depends on when it goes looking for its own rebuild afterwards.
/// </summary>
/// <tests>src/Whizbang.Core/Perspectives/IPerspectiveRebuilder.cs</tests>
/// <tests>src/Whizbang.Core/Events/System/SystemEvents.cs</tests>
public class RebuildProvenanceTests {

  [Test]
  public async Task RebuildOrigin_Defaults_RecordNothingRatherThanGuessAsync() {
    var origin = new RebuildOrigin();

    await Assert.That(origin.RequestId).IsNull();
    await Assert.That(origin.Trigger).IsEqualTo(RebuildTrigger.Unknown);
    await Assert.That(origin.RequestedBy).IsNull();
  }

  // The enum's zero matters beyond style. An event stored before provenance existed deserializes with Trigger
  // absent, which lands on whatever value is 0. If that were Requested, every historical rebuild would claim an
  // operator asked for it, and the one distinction these events exist to draw would be inverted.
  // Converted at runtime rather than compared as literals: a literal comparison is folded by the compiler, so it
  // would pass whatever the enum said.
  [Test]
  public async Task RebuildTrigger_ZeroMeansUnknownAsync() {
    var fromZero = (RebuildTrigger)Enum.ToObject(typeof(RebuildTrigger), 0);

    await Assert.That(fromZero).IsEqualTo(RebuildTrigger.Unknown);
  }

  // Two members sharing a value would make them indistinguishable once stored, and nothing else would notice.
  [Test]
  public async Task RebuildTrigger_MembersAllHaveDistinctValuesAsync() {
    var values = Enum.GetValues<RebuildTrigger>();
    var distinct = values.Select(v => (int)v).Distinct().Count();

    await Assert.That(values.Length).IsEqualTo(4);
    await Assert.That(distinct).IsEqualTo(values.Length);
  }

  // Every existing caller constructs these events without an origin. If that stopped compiling, or started
  // yielding something other than null, an event written by an older build could not be read back.
  [Test]
  public async Task RebuildEvents_WithoutAnOrigin_CarryNullRatherThanAnEmptyOriginAsync() {
    var id = Guid.NewGuid();

    var started = new PerspectiveRebuildStarted(id, "P", RebuildMode.InPlace, 3, DateTimeOffset.UnixEpoch);
    var completed = new PerspectiveRebuildCompleted(id, "P", RebuildMode.InPlace, 3, 9, TimeSpan.FromSeconds(1));
    var failed = new PerspectiveRebuildFailed(id, "P", RebuildMode.InPlace, "boom", 1, TimeSpan.FromSeconds(1));

    await Assert.That(started.Origin).IsNull();
    await Assert.That(completed.Origin).IsNull();
    await Assert.That(failed.Origin).IsNull();
  }

  [Test]
  public async Task RebuildStarted_CarriesTheOriginItWasGivenAsync() {
    var requestId = Guid.NewGuid();
    var origin = new RebuildOrigin(requestId, RebuildTrigger.Requested, "an operator");

    var evt = new PerspectiveRebuildStarted(
        Guid.NewGuid(), "InventoryLevels", RebuildMode.SelectedStreams, 7, DateTimeOffset.UnixEpoch, origin);

    await Assert.That(evt.Origin!.RequestId).IsEqualTo(requestId);
    await Assert.That(evt.Origin!.Trigger).IsEqualTo(RebuildTrigger.Requested);
    await Assert.That(evt.Origin!.RequestedBy).IsEqualTo("an operator");
  }

  [Test]
  public async Task RebuildCompleted_CarriesTheOriginAlongsideTheOutcomeAsync() {
    var requestId = Guid.NewGuid();
    var origin = new RebuildOrigin(requestId, RebuildTrigger.Requested, "an operator");
    var duration = TimeSpan.FromSeconds(45);

    var evt = new PerspectiveRebuildCompleted(
        Guid.NewGuid(), "InventoryLevels", RebuildMode.SelectedStreams, 100, 500, duration, origin);

    await Assert.That(evt.StreamsProcessed).IsEqualTo(100);
    await Assert.That(evt.EventsReplayed).IsEqualTo(500);
    await Assert.That(evt.Duration).IsEqualTo(duration);
    await Assert.That(evt.Origin!.RequestId).IsEqualTo(requestId);
  }

  [Test]
  public async Task RebuildFailed_CarriesTheOriginAlongsideTheErrorAsync() {
    var requestId = Guid.NewGuid();
    var origin = new RebuildOrigin(requestId, RebuildTrigger.Migration);

    var evt = new PerspectiveRebuildFailed(
        Guid.NewGuid(), "InventoryLevels", RebuildMode.BlueGreen, "no runner", 4, TimeSpan.FromSeconds(2), origin);

    await Assert.That(evt.Error).IsEqualTo("no runner");
    await Assert.That(evt.StreamsProcessedBeforeFailure).IsEqualTo(4);
    await Assert.That(evt.Origin!.Trigger).IsEqualTo(RebuildTrigger.Migration);
    await Assert.That(evt.Origin!.RequestedBy).IsNull();
  }

  // The Started/Completed pair for one run shares a rebuild stream id. Without that, two concurrent rebuilds of
  // different perspectives under one request id cannot be told apart.
  [Test]
  public async Task RebuildStartedAndCompleted_ShareTheRunsStreamIdAsync() {
    var runId = Guid.NewGuid();
    var origin = new RebuildOrigin(Guid.NewGuid(), RebuildTrigger.Requested);

    var started = new PerspectiveRebuildStarted(runId, "P", RebuildMode.SelectedStreams, 2, DateTimeOffset.UnixEpoch, origin);
    var completed = new PerspectiveRebuildCompleted(runId, "P", RebuildMode.SelectedStreams, 2, 4, TimeSpan.Zero, origin);

    await Assert.That(started.StreamId).IsEqualTo(completed.StreamId);
    await Assert.That(started.Origin!.RequestId).IsEqualTo(completed.Origin!.RequestId);
  }
}
