using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Events.System;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests;

/// <summary>
/// Covers <see cref="StreamIdResolver"/>, the thin facade that delegates to the
/// source-generated, zero-reflection extractor table.
/// </summary>
[Category("Core")]
public class StreamIdResolverTests {
  private sealed record UnmappedEvent(Guid Id) : IEvent;

  [Test]
  public async Task Resolve_ForEventWithStreamId_ReturnsGeneratedStreamIdAsync() {
    var streamId = Guid.CreateVersion7();
    var @event = new PerspectiveRebuildStarted(
        streamId,
        "OrderSummary",
        RebuildMode.InPlace,
        TotalStreams: 3,
        DateTimeOffset.UnixEpoch);

    var resolved = StreamIdResolver.Resolve(@event);

    await Assert.That(resolved).IsEqualTo(streamId.ToString());
  }

  [Test]
  public async Task Resolve_ForEventWithoutExtractor_ThrowsInvalidOperationAsync() {
    await Assert.That(() => StreamIdResolver.Resolve(new UnmappedEvent(Guid.CreateVersion7())))
        .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task Resolve_WithNullEvent_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => StreamIdResolver.Resolve(null!))
        .ThrowsExactly<ArgumentNullException>();
  }
}
