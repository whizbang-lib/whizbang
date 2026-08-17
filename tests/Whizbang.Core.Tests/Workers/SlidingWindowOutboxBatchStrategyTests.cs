using System.Text.Json;
using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the contract of <see cref="SlidingWindowOutboxBatchStrategy"/> — the producer-side
/// stream-affinity batcher (Half B of plans/pump-then-process.md). Per-stream-keyed sliding
/// window: same-stream events serialize through one buffer; different streams batch
/// independently. Defaults 50ms / 1s / 100 (mirror of inbox).
/// </summary>
public class SlidingWindowOutboxBatchStrategyTests {
  private readonly Uuid7IdProvider _idProvider = new();

  // ===== same-stream batching =====

  [Test]
  public async Task AppendAsync_SameStream_SingleBatchSortedByMessageIdAsync() {
    var captured = new List<OutboxMessage[]>();
    var flushedSignal = new TaskCompletionSource();
    var streamId = _idProvider.NewGuid();

    await using var sut = new SlidingWindowOutboxBatchStrategy(
      flush: (msgs, ct) => {
        captured.Add(msgs);
        flushedSignal.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    var m1 = _make(streamId);
    var m2 = _make(streamId);
    var m3 = _make(streamId);

    // Enqueue out-of-order on purpose
    await sut.AppendAsync(m3);
    await sut.AppendAsync(m1);
    await sut.AppendAsync(m2);

    await flushedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(captured.Count).IsEqualTo(1);
    var batch = captured[0];
    await Assert.That(batch.Length).IsEqualTo(3);
    // Batch must be ordered by MessageId — locks the producer-side stream affinity invariant.
    await Assert.That(batch[0].MessageId).IsEqualTo(m1.MessageId);
    await Assert.That(batch[1].MessageId).IsEqualTo(m2.MessageId);
    await Assert.That(batch[2].MessageId).IsEqualTo(m3.MessageId);
  }

  // ===== cross-stream concurrency =====

  [Test]
  public async Task AppendAsync_DifferentStreams_FlushIndependentlyAsync() {
    var streamA = _idProvider.NewGuid();
    var streamB = _idProvider.NewGuid();
    var captured = new List<(Guid? Stream, int Count)>();
    var lockObj = new object();
    var bothFlushed = new TaskCompletionSource();
    var seenA = false;
    var seenB = false;

    await using var sut = new SlidingWindowOutboxBatchStrategy(
      flush: (msgs, ct) => {
        var stream = msgs[0].StreamId;
        lock (lockObj) {
          captured.Add((stream, msgs.Length));
          if (stream == streamA) {
            seenA = true;
          }
          if (stream == streamB) {
            seenB = true;
          }
          if (seenA && seenB) {
            bothFlushed.TrySetResult();
          }
        }
        return Task.CompletedTask;
      },
      options: new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    await sut.AppendAsync(_make(streamA));
    await sut.AppendAsync(_make(streamB));
    await sut.AppendAsync(_make(streamA));

    await bothFlushed.Task.WaitAsync(TimeSpan.FromSeconds(2));

    // Stream A and Stream B each had their own window. They produced separate batches.
    var aBatches = captured.Where(c => c.Stream == streamA).ToList();
    var bBatches = captured.Where(c => c.Stream == streamB).ToList();
    await Assert.That(aBatches.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(bBatches.Count).IsGreaterThanOrEqualTo(1);
    // The two A messages are serialized through ONE stream-A buffer; total A messages flushed = 2.
    await Assert.That(aBatches.Sum(c => c.Count)).IsEqualTo(2);
    await Assert.That(bBatches.Sum(c => c.Count)).IsEqualTo(1);
  }

  // ===== MaxSize early flush =====

  [Test]
  public async Task AppendAsync_BatchExceedsMaxSize_FlushesEarlyAsync() {
    var captured = new List<OutboxMessage[]>();
    var firstFlush = new TaskCompletionSource();
    var streamId = _idProvider.NewGuid();

    await using var sut = new SlidingWindowOutboxBatchStrategy(
      flush: (msgs, ct) => {
        captured.Add(msgs);
        firstFlush.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(50),
        MaxWait = TimeSpan.FromSeconds(10),
        MaxSize = 5,
      });

    for (var i = 0; i < 5; i++) {
      await sut.AppendAsync(_make(streamId));
    }

    await firstFlush.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(captured.Count).IsEqualTo(1);
    await Assert.That(captured[0].Length).IsEqualTo(5);
  }

  // ===== Null stream id routes to default buffer =====

  [Test]
  public async Task AppendAsync_NullStreamId_RoutesToDefaultBufferAsync() {
    var captured = new List<OutboxMessage[]>();
    var flushed = new TaskCompletionSource();

    await using var sut = new SlidingWindowOutboxBatchStrategy(
      flush: (msgs, ct) => {
        captured.Add(msgs);
        flushed.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    await sut.AppendAsync(_make(streamId: null));
    await sut.AppendAsync(_make(streamId: null));

    await flushed.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(captured.Sum(b => b.Length)).IsEqualTo(2);
  }

  // ===== Shutdown drains all stream buffers =====

  [Test]
  public async Task FlushAndStopAsync_DrainsAllStreamsAsync() {
    var captured = new List<OutboxMessage[]>();
    var streamA = _idProvider.NewGuid();
    var streamB = _idProvider.NewGuid();

    var sut = new SlidingWindowOutboxBatchStrategy(
      flush: (msgs, ct) => {
        // Stream buffers drain CONCURRENTLY (independent parallel windows), so the capture
        // must be synchronized — an unlocked List.Add race silently loses a batch.
        lock (captured) {
          captured.Add(msgs);
        }
        return Task.CompletedTask;
      },
      options: new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMinutes(1),  // very long; only Stop drains
        MaxSize = 1000,
      });

    await sut.AppendAsync(_make(streamA));
    await sut.AppendAsync(_make(streamB));
    await sut.AppendAsync(_make(streamA));

    await sut.FlushAndStopAsync();

    await Assert.That(captured.Sum(b => b.Length)).IsEqualTo(3);
  }

  // ===== Default options locked =====

  [Test]
  public async Task DefaultOptions_50ms_1s_100Async() {
    var defaults = new SlidingWindowOutboxOptions();
    await Assert.That(defaults.SlidingWindow).IsEqualTo(TimeSpan.FromMilliseconds(50));
    await Assert.That(defaults.MaxWait).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(defaults.MaxSize).IsEqualTo(100);
  }

  // ===== Append-after-stop throws =====

  [Test]
  public async Task AppendAsync_AfterStop_ThrowsAsync() {
    await using var sut = new SlidingWindowOutboxBatchStrategy(
      flush: (_, _) => Task.CompletedTask);

    await sut.FlushAndStopAsync();

    await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
      await sut.AppendAsync(_make(_idProvider.NewGuid())));
  }

  // ===== helpers =====

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
