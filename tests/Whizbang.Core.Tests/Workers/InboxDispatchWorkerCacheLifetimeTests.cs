// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1793: a deserialize-cache entry lived for its whole TTL after its row completed, so the cache's memory was
/// the last two minutes of traffic rather than the rows still in flight. Dispatch now records each payload's size in
/// the cache and drops the entry when the row completes.
/// </summary>
[Category("Workers")]
public sealed class InboxDispatchWorkerCacheLifetimeTests {
  private static InboxDeserializeCache _cache() =>
    new(new SystemTimeProvider(new FakeTimeProvider(InboxDispatchUnitHarness.Start)));

  [Test]
  public async Task Dispatch_CachesThePayloadWithItsSize_AndDropsItWhenTheRowCompletesAsync() {
    var cache = _cache();
    var h = new InboxDispatchUnitHarness(cache: cache);
    const string payload = """{"Name":"abcdefghij"}""";
    var work = InboxDispatchUnitHarness.Work(payload: payload);
    long bytesAtCommit = -1;
    h.Commits.OnEnqueue = _ => bytesAtCommit = cache.Bytes;

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(bytesAtCommit).IsEqualTo(System.Text.Encoding.UTF8.GetByteCount(payload))
      .Because("an entry is sized by the payload it was parsed from, so the byte budget sees what it holds");
    await Assert.That(cache.TryGet(work.MessageId, out _)).IsFalse()
      .Because("a completed row is never parsed again, so its entry must not occupy the cache for its TTL");
    await Assert.That(cache.Bytes).IsEqualTo(0);
  }

  [Test]
  public async Task Dispatch_DeadLetteringARow_DropsItsCacheEntryAsync() {
    var cache = _cache();
    var h = new InboxDispatchUnitHarness(cache: cache);
    var work = InboxDispatchUnitHarness.Work(attempts: 11, messageType: "Contracts.Orders.OrderPlaced, Contracts");
    cache.Set(work.MessageId, new object(), 100);

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(cache.TryGet(work.MessageId, out _)).IsFalse()
      .Because("dead-lettering is terminal: the row is gone from the inbox and its parsed payload is dead weight");
  }

  [Test]
  public async Task Dispatch_HandingACommitOn_DropsItsCacheEntryAsync() {
    var cache = _cache();
    var h = new InboxDispatchUnitHarness(cache: cache);
    var work = InboxDispatchUnitHarness.Work(attempts: 11, messageType: TypeNameFormatter.Format(typeof(IntegrityCheckpoint)));
    cache.Set(work.MessageId, new object(), 100);

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(cache.TryGet(work.MessageId, out _)).IsFalse();
  }
}
