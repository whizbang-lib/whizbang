using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Events.System;

namespace Whizbang.Core.Tests.Events.System;

/// <summary>
/// Tests for stream-level rewind system events.
/// These bracket all per-perspective rewinds for a given stream.
/// </summary>
/// <tests>src/Whizbang.Core/Events/System/SystemEvents.cs</tests>
public class StreamRewindEventTests {

  [Test]
  public async Task StreamRewindStarted_Properties_SetCorrectlyAsync() {
    var streamId = Guid.CreateVersion7();
    var triggerEventId = Guid.CreateVersion7();
    var perspectiveNames = new[] { "OrderPerspective", "InventoryPerspective" };
    var startedAt = DateTimeOffset.UtcNow;

    var evt = new StreamRewindStarted(streamId, perspectiveNames, triggerEventId, startedAt);

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.PerspectiveNames).IsEqualTo(perspectiveNames);
    await Assert.That(evt.TriggerEventId).IsEqualTo(triggerEventId);
    await Assert.That(evt.StartedAt).IsEqualTo(startedAt);
  }

  [Test]
  public async Task StreamRewindCompleted_Properties_SetCorrectlyAsync() {
    var streamId = Guid.CreateVersion7();
    var perspectiveNames = new[] { "OrderPerspective" };
    var startedAt = DateTimeOffset.UtcNow;
    var completedAt = startedAt.AddMilliseconds(500);

    var evt = new StreamRewindCompleted(streamId, perspectiveNames, 42, startedAt, completedAt);

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.PerspectiveNames).IsEqualTo(perspectiveNames);
    await Assert.That(evt.TotalEventsReplayed).IsEqualTo(42);
    await Assert.That(evt.StartedAt).IsEqualTo(startedAt);
    await Assert.That(evt.CompletedAt).IsEqualTo(completedAt);
  }

  [Test]
  public async Task StreamRewindStarted_ImplementsIEventAsync() {
    var evt = new StreamRewindStarted(Guid.CreateVersion7(), ["Test"], Guid.CreateVersion7(), DateTimeOffset.UtcNow);
    await Assert.That(evt).IsAssignableTo<IEvent>();
  }

  [Test]
  public async Task StreamRewindCompleted_ImplementsIEventAsync() {
    var evt = new StreamRewindCompleted(Guid.CreateVersion7(), ["Test"], 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    await Assert.That(evt).IsAssignableTo<IEvent>();
  }
}
