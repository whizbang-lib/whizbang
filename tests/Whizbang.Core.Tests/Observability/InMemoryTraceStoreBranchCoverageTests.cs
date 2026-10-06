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
/// Branch coverage for <see cref="InMemoryTraceStore"/>'s timestamp fallback: an envelope whose
/// hop list is null, one with hops but no Current hop, and one with a Current hop must all sort
/// and filter, with the first two treated as <see cref="DateTimeOffset.MinValue"/>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/InMemoryTraceStore.cs</code-under-test>
[Category("Observability")]
public class InMemoryTraceStoreBranchCoverageTests {
  private sealed record TraceProbe(string Data);

  private static ServiceInstanceInfo _instance() => new() {
    ServiceName = "TestService",
    InstanceId = TrackedGuid.New(),
    HostName = "test-host",
    ProcessId = 1
  };

  private static MessageEnvelope<TraceProbe> _envelope(List<MessageHop> hops) => new() {
    MessageId = MessageId.New(),
    Payload = new TraceProbe("probe"),
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Hops = hops
  };

  private static MessageEnvelope<TraceProbe> _withNullHops() => _envelope(null!);

  private static MessageEnvelope<TraceProbe> _withHop(HopType type, DateTimeOffset timestamp, MessageId? causationId = null) =>
    _envelope([new MessageHop {
      ServiceInstance = _instance(),
      Type = type,
      Timestamp = timestamp,
      CausationId = causationId
    }]);

  [Test]
  public async Task GetCausalChainAsync_MixOfNullHopsMissingCurrentHopAndCurrentHop_SortsFallbacksFirstAsync() {
    var store = new InMemoryTraceStore();
    var now = DateTimeOffset.UtcNow;
    // Parent with a null hop list: reachable by the upward walk, sorts as MinValue.
    var parent = _withNullHops();
    var queried = _withHop(HopType.Current, now, parent.MessageId);
    // Two children carrying only a Causation hop: found by causation id, but no Current timestamp.
    var noCurrentA = _withHop(HopType.Causation, now.AddSeconds(5), queried.MessageId);
    var noCurrentB = _withHop(HopType.Causation, now.AddSeconds(6), queried.MessageId);
    var later = _withHop(HopType.Current, now.AddSeconds(1), queried.MessageId);
    foreach (var e in new IMessageEnvelope[] { parent, queried, noCurrentA, noCurrentB, later }) {
      await store.StoreAsync(e);
    }

    var chain = await store.GetCausalChainAsync(queried.MessageId);

    await Assert.That(chain).Count().IsEqualTo(5);
    await Assert.That(chain.Take(3).Select(e => e.MessageId))
      .IsEquivalentTo([parent.MessageId, noCurrentA.MessageId, noCurrentB.MessageId])
      .Because("a null hop list and a hop list without a Current hop both sort as MinValue, ahead of real timestamps; "
             + "the Causation hop's own timestamp must not be used");
    await Assert.That(chain[3].MessageId).IsEqualTo(queried.MessageId);
    await Assert.That(chain[4].MessageId).IsEqualTo(later.MessageId);
  }

  [Test]
  public async Task GetByTimeRangeAsync_MixOfNullHopsMissingCurrentHopAndCurrentHop_FiltersAndOrdersOnTheFallbackAsync() {
    var store = new InMemoryTraceStore();
    var now = DateTimeOffset.UtcNow;
    var nullHops = _withNullHops();
    var noCurrent = _withHop(HopType.Causation, now);
    var first = _withHop(HopType.Current, now.AddSeconds(-2));
    var second = _withHop(HopType.Current, now.AddSeconds(-1));
    foreach (var e in new IMessageEnvelope[] { second, nullHops, first, noCurrent }) {
      await store.StoreAsync(e);
    }

    var everything = await store.GetByTimeRangeAsync(DateTimeOffset.MinValue, now.AddHours(1));
    var recent = await store.GetByTimeRangeAsync(now.AddMinutes(-1), now.AddHours(1));

    await Assert.That(everything).Count().IsEqualTo(4);
    await Assert.That(everything.Take(2).Select(e => e.MessageId))
      .IsEquivalentTo([nullHops.MessageId, noCurrent.MessageId])
      .Because("both fallbacks resolve to MinValue, so a range starting at MinValue includes them and orders them first");
    await Assert.That(everything[2].MessageId).IsEqualTo(first.MessageId);
    await Assert.That(everything[3].MessageId).IsEqualTo(second.MessageId);
    await Assert.That(recent.Select(e => e.MessageId)).IsEquivalentTo([first.MessageId, second.MessageId])
      .Because("a range that excludes MinValue drops the envelopes with no Current hop timestamp, null hop list included");
  }
}
