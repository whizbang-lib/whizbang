// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Where a replay's next read resumes (#2379): after the event last in stream order among those read, never the highest
/// id, which sits anywhere in a stream whose ids and versions disagree.
/// </summary>
/// <docs>fundamentals/perspectives/rewind#one-order-for-a-stream</docs>
public class StreamReadAnchorTests {
  private sealed record Probe : IEvent;

  // Ascending ids: _id(1) < _id(2) < _id(3) < _id(4), as text and as Guid.
  private static Guid _id(int n) => Guid.Parse($"0199b000-0000-7000-8000-{n:D12}");

  private static MessageEnvelope<IEvent> _event(int id, long? version) =>
    new(MessageId.From(_id(id)), new Probe(), []) { LocalStreamVersion = version };

  [Test]
  public async Task After_EveryEventVersioned_IsTheHighestVersion_NotTheHighestIdAsync() {
    // The specimen's shape: the first event has the highest id, the second the lowest, read in an order that agrees
    // with neither.
    var read = new[] { _event(2, version: 3), _event(4, version: 1), _event(1, version: 2), _event(3, version: 4) };

    var anchor = StreamReadAnchor.After(read, current: _id(4));

    await Assert.That(anchor).IsEqualTo(_id(3)).Because("version 4 is last in the stream, whatever the ids say");
  }

  [Test]
  public async Task After_AnEventWithNoVersion_IsTheHighestIdAsync() {
    var read = new[] { _event(3, version: 1), _event(1, version: null), _event(2, version: 2) };

    var anchor = StreamReadAnchor.After(read, current: null);

    await Assert.That(anchor).IsEqualTo(_id(3)).Because("with the stream order unknown the read resumes after the highest id");
  }

  [Test]
  public async Task After_NothingRead_KeepsTheCurrentAnchorAsync() {
    await Assert.That(StreamReadAnchor.After([], current: _id(2))).IsEqualTo(_id(2));
    await Assert.That(StreamReadAnchor.After([], current: null)).IsNull();
  }

  [Test]
  public async Task After_NullRead_ThrowsAsync() {
    await Assert.That(() => StreamReadAnchor.After(null!, current: null)).Throws<ArgumentNullException>();
    await Assert.That(() => new StreamReadAnchor().Offer(null!)).Throws<ArgumentNullException>();
  }
}
