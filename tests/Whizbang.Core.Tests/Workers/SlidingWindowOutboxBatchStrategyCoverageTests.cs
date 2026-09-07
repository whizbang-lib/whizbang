using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Round-23 coverage for <see cref="SlidingWindowOutboxBatchStrategy"/>'s flush-observes-shutdown
/// path. See the class remarks on <c>_drainBufferAsync</c> for the five line shapes already
/// declined for this family (empty-batch continue, the outer shutdown catch, the disposed-guard
/// timing gap, the two-sweeps TryRemove race, and the catch-all around the evicted worker) —
/// this file does not re-attempt those; it covers the one remaining target line that IS
/// deterministically drivable.
/// </summary>
public class SlidingWindowOutboxBatchStrategyCoverageTests {
  private readonly Uuid7IdProvider _idProvider = new();

  // Target: src/Whizbang.Core/Workers/SlidingWindowOutboxBatchStrategy.cs:140 — `return;` in
  // `catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)` around the flush
  // call. FlushAndStopAsync always completes the stream's channel writer before it ever cancels
  // _stopCts, so a flush callback that itself awaits _stopCts's own token (as production flush
  // callbacks resolving a DI scope legitimately can, via the token this class hands them) is the
  // one deterministic way to observe that cancellation from inside the flush — no race, since
  // Task.Delay(Timeout.Infinite, ct) has no other way to complete. If this catch let the
  // exception escape uncaught, the drain task would fault instead of returning cleanly, and
  // Task.WhenAll(workers) inside FlushAndStopAsync would surface an unrelated fault instead of
  // the clean shutdown callers rely on.
  [Test]
  [Timeout(15000)]
  public async Task FlushObservingStopToken_DuringForcedShutdown_ReturnsWithoutFaultingAsync(
      CancellationToken cancellationToken) {
    var flushEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var sut = new SlidingWindowOutboxBatchStrategy(
      flush: async (_, ct) => {
        flushEntered.TrySetResult();
        // The ONLY way this ever completes is via ct (== the strategy's own _stopCts) being
        // canceled -- there is no competing "normal" completion path, so there is no race.
        await Task.Delay(Timeout.Infinite, ct);
      },
      options: new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(20),
        MaxWait = TimeSpan.FromMilliseconds(100),
        MaxSize = 100,
      });

    await sut.AppendAsync(_make(_idProvider.NewGuid()), cancellationToken);
    await flushEntered.Task.WaitAsync(cancellationToken);

    using var stopCts = new CancellationTokenSource();
    await stopCts.CancelAsync();

    // FlushAndStopAsync's own WaitAsync(cancellationToken) throws immediately on the pre-canceled
    // token, which is what makes it cancel the strategy's internal _stopCts -- the token the
    // pending flush above is blocked on. Must return promptly, not hang or throw.
    await sut.FlushAndStopAsync(stopCts.Token).WaitAsync(cancellationToken);

    await Assert.That(async () => await sut.AppendAsync(_make(_idProvider.NewGuid())))
      .ThrowsExactly<ObjectDisposedException>()
      .Because("the strategy must have fully stopped, not merely returned from a faulted drain task");
  }

  private OutboxMessage _make(Guid? streamId) {
    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(messageId),
      JsonDocument.Parse("{}").RootElement,
      []);
    return new OutboxMessage {
      MessageId = messageId,
      StreamId = streamId,
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = [],
      },
    };
  }
}
