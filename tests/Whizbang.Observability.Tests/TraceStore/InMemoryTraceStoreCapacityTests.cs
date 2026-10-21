// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Observability.Tests.TraceStore;

/// <summary>
/// The in-memory trace store keeps only its most recent envelopes (#1601): past its capacity the
/// oldest is evicted, and no query ever returns an evicted envelope.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/InMemoryTraceStore.cs</code-under-test>
/// <docs>fundamentals/persistence/observability#trace-store-registration</docs>
[Category("Observability")]
public class InMemoryTraceStoreCapacityTests {
  private static readonly DateTimeOffset _epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private sealed record CapacityCommand(int Index);

  private static MessageEnvelope<CapacityCommand> _envelope(int index, CorrelationId correlationId, MessageId? causationId = null) {
    var envelope = new MessageEnvelope<CapacityCommand> {
      MessageId = MessageId.New(),
      Payload = new CapacityCommand(index),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = []
    };
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "CapacityService",
        InstanceId = Guid.Empty,
        HostName = "capacity-host",
        ProcessId = 1
      },
      Type = HopType.Current,
      Timestamp = _epoch.AddSeconds(index),
      CorrelationId = correlationId,
      CausationId = causationId
    });
    return envelope;
  }

  [Test]
  public async Task Constructor_Parameterless_UsesTheDefaultCapacityAsync() {
    var store = new InMemoryTraceStore();

    await Assert.That(store.Capacity).IsEqualTo(10_000)
      .Because("the documented default is InMemoryTraceStore.DEFAULT_CAPACITY, 10,000 envelopes");
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task Constructor_CapacityNotPositive_ThrowsAsync(int capacity) {
    var exception = await Assert.That(() => new InMemoryTraceStore(capacity))
      .ThrowsExactly<ArgumentOutOfRangeException>();

    await Assert.That(exception!.ParamName).IsEqualTo("capacity");
  }

  [Test]
  public async Task StoreAsync_PastCapacity_EvictsTheOldestFirstAsync() {
    var store = new InMemoryTraceStore(3);
    var correlation = CorrelationId.New();
    var envelopes = Enumerable.Range(0, 5).Select(i => _envelope(i, correlation)).ToList();

    foreach (var envelope in envelopes) {
      await store.StoreAsync(envelope);
    }

    await Assert.That(store.Count).IsEqualTo(3)
      .Because("the store never holds more than its capacity");
    await Assert.That(await store.GetByMessageIdAsync(envelopes[0].MessageId)).IsNull();
    await Assert.That(await store.GetByMessageIdAsync(envelopes[1].MessageId)).IsNull();
    await Assert.That(await store.GetByMessageIdAsync(envelopes[2].MessageId)).IsNotNull();
    await Assert.That(await store.GetByMessageIdAsync(envelopes[4].MessageId)).IsNotNull();
  }

  [Test]
  public async Task Queries_AfterEviction_NeverReturnAnEvictedEnvelopeAsync() {
    var store = new InMemoryTraceStore(2);
    var correlation = CorrelationId.New();
    var parent = _envelope(0, correlation);
    var child = _envelope(1, correlation, parent.MessageId);
    var grandchild = _envelope(2, correlation, child.MessageId);

    await store.StoreAsync(parent);
    await store.StoreAsync(child);
    await store.StoreAsync(grandchild);

    var byCorrelation = await store.GetByCorrelationAsync(correlation);
    var byTime = await store.GetByTimeRangeAsync(_epoch, _epoch.AddSeconds(10));
    var chain = await store.GetCausalChainAsync(grandchild.MessageId);
    var evictedChain = await store.GetCausalChainAsync(parent.MessageId);

    MessageId[] kept = [child.MessageId, grandchild.MessageId];
    await Assert.That(byCorrelation.ConvertAll(e => e.MessageId)).IsEquivalentTo(kept);
    await Assert.That(byTime.ConvertAll(e => e.MessageId)).IsEquivalentTo(kept);
    await Assert.That(chain.ConvertAll(e => e.MessageId)).IsEquivalentTo(kept)
      .Because("the walk up the causation chain stops at the evicted parent");
    await Assert.That(evictedChain).IsEmpty();
  }

  [Test]
  public async Task StoreAsync_SameMessageTwice_KeepsOneEntryAndEvictsNothingAsync() {
    var store = new InMemoryTraceStore(2);
    var correlation = CorrelationId.New();
    var first = _envelope(0, correlation);
    var second = _envelope(1, correlation);

    await store.StoreAsync(first);
    await store.StoreAsync(second);
    await store.StoreAsync(first);

    await Assert.That(store.Count).IsEqualTo(2);
    await Assert.That(await store.GetByMessageIdAsync(first.MessageId)).IsNotNull()
      .Because("a repeat store of a held envelope is not a new entry, so it evicts nothing");
    await Assert.That(await store.GetByMessageIdAsync(second.MessageId)).IsNotNull();
  }
}
