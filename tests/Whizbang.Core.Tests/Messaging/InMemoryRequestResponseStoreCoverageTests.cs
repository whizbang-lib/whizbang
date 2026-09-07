using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage-round-23 targeted test for <see cref="InMemoryRequestResponseStore.CleanupExpiredAsync"/>'s
/// own expiry-detection scan (finding an already-expired record still present in the
/// dictionary and removing it), as distinct from a request's self-expiring background
/// timeout task racing ahead and removing it first. The main suite
/// (InMemoryRequestResponseStoreTests) only ever observes the end state after both
/// mechanisms have had a chance to run, so the store's own scan logic is never actually
/// proven to be the one doing the removal.
/// </summary>
/// <tests>src/Whizbang.Core/Messaging/InMemoryRequestResponseStore.cs</tests>
public class InMemoryRequestResponseStoreCoverageTests {

  // CleanupExpiredAsync's own scan - as opposed to a request's self-expiring background
  // timeout task - must find an already-expired-but-still-present record and remove it;
  // otherwise a request whose background timeout hasn't fired yet (starved thread pool,
  // long GC pause, etc.) would never be reclaimed by the periodic sweep, leaking its
  // TaskCompletionSource and RequestRecord forever.
  [Test]
  public async Task CleanupExpiredAsync_RecordStillPresentPastItsExpiry_DetectsAndRemovesItAsync() {
    var store = new InMemoryRequestResponseStore();
    var correlationId = CorrelationId.New();
    var requestId = MessageId.New();

    // Timeout.InfiniteTimeSpan is -1ms, so ExpiresAt (UtcNow + timeout) is already in the
    // past the instant the record is saved. The record's own background delay task waits
    // on Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None) - an unbounded wait
    // on a token that's never canceled - so it never fires and never touches _requests.
    // Nothing but CleanupExpiredAsync's own scan can remove this record.
    await store.SaveRequestAsync(correlationId, requestId, Timeout.InfiniteTimeSpan, CancellationToken.None);

    await store.CleanupExpiredAsync(CancellationToken.None);

    var result = await store.WaitForResponseAsync(correlationId, CancellationToken.None);
    await Assert.That(result).IsNull()
      .Because("the expired record must have been found and removed by CleanupExpiredAsync's own scan, not left dangling in the dictionary");
  }
}
