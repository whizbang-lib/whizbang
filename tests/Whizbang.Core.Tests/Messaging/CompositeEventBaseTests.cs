using System;
using System.Collections.Generic;
using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the turnkey authoring ergonomics for <see cref="CompositeEventBase"/>: a consumer declares a
/// composite in one line, the base stamps the shared <c>[StreamId]</c>, yields inner events in
/// producer order, exposes an overridable cap, and fails fast (producer-side) when the inner count
/// exceeds the cap. Serialization round-trip for a base-derived composite is covered in
/// JsonContextRegistryTests.
/// </summary>
[Category("Messaging")]
public class CompositeEventBaseTests {
  private sealed record _innerA(int N) : IMessage;
  private sealed record _innerB(string S) : IMessage;

  /// <summary>One-line consumer composite — the whole point of the helper.</summary>
  private sealed class _testComposite : CompositeEventBase;

  [Test]
  public async Task StampsStreamId_AndYieldsInnerInProducerOrderAsync() {
    var streamId = Guid.NewGuid();
    var a = new _innerA(1);
    var b = new _innerB("two");
    var c = new _testComposite { StreamId = streamId, Inner = [a, b] };

    await Assert.That(c.StreamId).IsEqualTo(streamId);
    // ICompositeEvent.InnerEvents yields the inner messages in the order supplied.
    var inner = c.InnerEvents.ToList();
    await Assert.That(inner.Count).IsEqualTo(2);
    await Assert.That(inner[0]).IsSameReferenceAs(a);
    await Assert.That(inner[1]).IsSameReferenceAs(b);
  }

  [Test]
  public async Task MaxInnerEventsAllowed_DefaultsTo10K_AndIsOverridableAsync() {
    var c = new _testComposite { StreamId = Guid.NewGuid(), Inner = [] };
    await Assert.That(c.MaxInnerEventsAllowed).IsEqualTo(10_000);

    var custom = new _testComposite { StreamId = Guid.NewGuid(), Inner = [], MaxInnerEventsAllowed = 42 };
    await Assert.That(custom.MaxInnerEventsAllowed).IsEqualTo(42);
  }

  [Test]
  public async Task EnsureWithinCap_ThrowsWhenInnerCountExceedsCapAsync() {
    var c = new _testComposite {
      StreamId = Guid.NewGuid(),
      Inner = [new _innerA(1), new _innerA(2)],
      MaxInnerEventsAllowed = 1
    };
    // Producer-side fail-fast: construction is cheap; the guard surfaces a malformed composite
    // synchronously (in the publishing handler's try/catch) rather than silently at the receiver.
    await Assert.That(() => c.EnsureWithinCap()).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task EnsureWithinCap_PassesWhenWithinCapAsync() {
    var c = new _testComposite { StreamId = Guid.NewGuid(), Inner = [new _innerA(1)] };
    c.EnsureWithinCap(); // does not throw
    await Assert.That(c.InnerEvents.Count()).IsEqualTo(1);
  }

  [Test]
  public async Task Inner_DefaultsToEmpty_NotNullAsync() {
    var c = new _testComposite { StreamId = Guid.NewGuid() };
    await Assert.That(c.InnerEvents).IsNotNull();
    await Assert.That(c.InnerEvents.Any()).IsFalse();
  }
}
