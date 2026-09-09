using System.Text.Json;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The shutdown deadline of <see cref="SlidingWindowInboxBatchStrategy"/>: with a flush still in flight
/// when the caller's token is already canceled, the drain is abandoned and the stop token canceled instead
/// of being waited on forever. The outbox strategy and the per-stream serializer lock the same contract.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowInboxBatchStrategy.cs</code-under-test>
public class SlidingWindowInboxBatchStrategyStopTests {
  [Test]
  [Timeout(30_000)]
  public async Task FlushAndStopAsync_WithCanceledToken_StopsWithoutHangingAsync(CancellationToken testToken) {
    var releaseFlush = new TaskCompletionSource();
    var flushEntered = new TaskCompletionSource();
    // The token the in-flight flush was handed — the strategy's own stop token.
    var flushToken = CancellationToken.None;
    var sut = new SlidingWindowInboxBatchStrategy(
      flush: async (_, ct) => {
        flushToken = ct;
        flushEntered.TrySetResult();
        await releaseFlush.Task;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(20),
        MaxWait = TimeSpan.FromMilliseconds(100),
        MaxSize = 100,
      });

    await sut.AppendAsync(_makeMessage(), testToken);
    await flushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);

    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    // Returning at all is half the contract — the [Timeout] above is what makes "without hanging"
    // a failure rather than a stuck suite, since the flush is still parked on releaseFlush.
    await sut.FlushAndStopAsync(cts.Token);

    await Assert.That(flushToken.IsCancellationRequested).IsTrue()
      .Because("the drain is ABANDONED, not awaited: the strategy cancels its own stop token so the "
             + "flush still in flight is told to give up. Leaving it un-canceled would strand that "
             + "flush — and any store call inside it — with nothing left to observe or stop it.");

    releaseFlush.TrySetResult();
  }

  private static InboxMessage _makeMessage() {
    var messageId = (Guid)TrackedGuid.NewMedo();
    var envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), JsonDocument.Parse("{}").RootElement, []);
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "test",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = null,
    };
  }
}
