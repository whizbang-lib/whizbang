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
/// A snapshot is taken only where a replay's fold is every event up to some version, and at the event with that version
/// (#2379).
/// </summary>
/// <docs>fundamentals/perspectives/rewind#snapshot-selection</docs>
public class FoldedVersionPrefixTests {
  private sealed record Probe : IEvent;

  private static MessageEnvelope<IEvent> _event(int n, long? version) =>
    new(MessageId.From(Guid.Parse($"0199b000-0000-7000-8000-{n:D12}")), new Probe(), []) { LocalStreamVersion = version };

  [Test]
  public async Task Folded_InVersionOrder_IsAPrefixAtEveryEventAsync() {
    MessageEnvelope<IEvent>[] read = [_event(1, 5), _event(2, 6), _event(3, 7)];
    var prefix = new FoldedVersionPrefix();
    prefix.Read(read);

    foreach (var envelope in read) {
      await Assert.That(prefix.Folded(envelope)).IsSameReferenceAs(envelope);
    }
  }

  [Test]
  public async Task Folded_AheadOfAnEarlierVersion_IsNoPrefixUntilItIsFoldedAsync() {
    var (v2, v3, v4) = (_event(1, 2), _event(2, 3), _event(3, 4));
    var prefix = new FoldedVersionPrefix();
    prefix.Read([v2, v3, v4]);

    await Assert.That(prefix.Folded(v3)).IsNull().Because("version 3 is folded and version 2 is not yet");
    await Assert.That(prefix.Folded(v2)).IsSameReferenceAs(v3).Because("versions 2 and 3 are folded: the snapshot is at 3");
    prefix.Read([_event(4, 5)]);
    await Assert.That(prefix.Folded(v4)).IsSameReferenceAs(v4)
      .Because("a later pass reads above everything folded, so the prefix holds through version 4");
  }

  [Test]
  public async Task Folded_AnEventWithNoVersion_IsNeverAPrefixAsync() {
    var (versioned, unversioned) = (_event(1, 1), _event(2, null));
    var prefix = new FoldedVersionPrefix();
    prefix.Read([versioned, unversioned]);

    await Assert.That(prefix.Folded(versioned)).IsNull().Because("the read holds an event no version places");
    var unread = new FoldedVersionPrefix();
    await Assert.That(unread.Folded(unversioned)).IsNull();
    await Assert.That(() => prefix.Read(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => prefix.Folded(null!)).Throws<ArgumentNullException>();
  }
}
