// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Branch coverage for <see cref="MessageEnvelope{TMessage}"/>'s first-hop readers on an envelope
/// with no hops to read: a hop list that is empty, or missing altogether (an envelope materialized
/// from a payload that carried none). The timestamp falls back to the current time and the
/// correlation and causation ids read as absent, instead of an index or null-reference failure.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/MessageEnvelope.cs</code-under-test>
[Category("Observability")]
public class MessageEnvelopeBranchCoverageTests {

  private static MessageEnvelope<string> _envelope(List<MessageHop> hops) => new() {
    MessageId = MessageId.New(),
    Payload = "payload",
    Hops = hops,
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  [Test]
  public async Task FirstHopReaders_HopListMissing_FallBackInsteadOfFailingAsync() {
    var envelope = _envelope(null!);

    var before = DateTimeOffset.UtcNow;
    var timestamp = envelope.GetMessageTimestamp();
    var after = DateTimeOffset.UtcNow;

    await Assert.That(timestamp).IsGreaterThanOrEqualTo(before)
      .Because("with no first hop to date the message, the current time stands in");
    await Assert.That(timestamp).IsLessThanOrEqualTo(after);
    await Assert.That(envelope.GetCorrelationId()).IsNull();
    await Assert.That(envelope.GetCausationId()).IsNull();
  }

  [Test]
  public async Task FirstHopReaders_HopListEmpty_FallBackInsteadOfFailingAsync() {
    var envelope = _envelope([]);

    var before = DateTimeOffset.UtcNow;
    var timestamp = envelope.GetMessageTimestamp();
    var after = DateTimeOffset.UtcNow;

    await Assert.That(timestamp).IsGreaterThanOrEqualTo(before);
    await Assert.That(timestamp).IsLessThanOrEqualTo(after);
    await Assert.That(envelope.GetCorrelationId()).IsNull()
      .Because("an empty hop list has no first hop to establish a correlation");
    await Assert.That(envelope.GetCausationId()).IsNull();
  }
}
