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
  public async Task FlushAndStopAsync_WithCanceledToken_StopsWithoutHangingAsync() {
    var releaseFlush = new TaskCompletionSource();
    var flushEntered = new TaskCompletionSource();
    var sut = new SlidingWindowInboxBatchStrategy(
      flush: async (_, _) => {
        flushEntered.TrySetResult();
        await releaseFlush.Task;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(20),
        MaxWait = TimeSpan.FromMilliseconds(100),
        MaxSize = 100,
      });

    await sut.AppendAsync(_makeMessage());
    await flushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await sut.FlushAndStopAsync(cts.Token);

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
