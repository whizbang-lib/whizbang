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
/// Locks the contract of <see cref="SlidingWindowInboxBatchStrategy"/> — the default
/// IInboxBatchStrategy that batches inbox writes via SlidingWindowBatcher. Defaults baked at
/// 50ms / 1s / 100 per plans/pump-then-process.md.
/// </summary>
public class SlidingWindowInboxBatchStrategyTests {
  private readonly Uuid7IdProvider _idProvider = new();

  [Test]
  public async Task AppendAsync_SingleMessage_FlushedAfterSlidingWindowAsync() {
    // Real time provider — FakeTimeProvider is flaky with SlidingWindowBatcher per
    // SlidingWindowBatcherTests' guidance. 30ms sliding / 200ms max-wait is fast + reliable.
    var captured = new List<InboxMessage[]>();
    var flushedSignal = new TaskCompletionSource();

    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, ct) => {
        captured.Add(msgs);
        flushedSignal.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    await sut.AppendAsync(_makeMessage());

    // Sliding-window debounce fires after 30ms idle → flush
    await flushedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(captured.Count).IsEqualTo(1);
    await Assert.That(captured[0].Length).IsEqualTo(1);
  }

  [Test]
  public async Task AppendAsync_BatchExceedsMaxSize_FlushesEarlyAsync() {
    var captured = new List<InboxMessage[]>();
    var firstFlush = new TaskCompletionSource();

    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, ct) => {
        captured.Add(msgs);
        firstFlush.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(50),
        MaxWait = TimeSpan.FromSeconds(10),  // generous so MaxSize is what flushes
        MaxSize = 5,
      });

    for (var i = 0; i < 5; i++) {
      await sut.AppendAsync(_makeMessage());
    }

    // MaxSize=5 should trigger flush regardless of timing
    await firstFlush.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(captured.Count).IsEqualTo(1);
    await Assert.That(captured[0].Length).IsEqualTo(5);
  }

  [Test]
  public async Task FlushAndStopAsync_DrainsBufferedMessagesAsync() {
    var captured = new List<InboxMessage[]>();

    var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, ct) => {
        captured.Add(msgs);
        return Task.CompletedTask;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(50),
        MaxWait = TimeSpan.FromMinutes(1),  // very long — won't fire before stop
        MaxSize = 1000,
      });

    await sut.AppendAsync(_makeMessage());
    await sut.AppendAsync(_makeMessage());

    // Stop drains the remaining buffered messages
    await sut.FlushAndStopAsync();

    var totalFlushed = captured.Sum(b => b.Length);
    await Assert.That(totalFlushed).IsEqualTo(2);
  }

  [Test]
  public async Task AppendAsync_AfterStop_ThrowsAsync() {
    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (_, _) => Task.CompletedTask);

    await sut.FlushAndStopAsync();

    await Assert.ThrowsAsync<ChannelClosedException>(async () =>
      await sut.AppendAsync(_makeMessage()));
  }

  [Test]
  public async Task DefaultOptions_50ms_1s_100Async() {
    // Lock the documented defaults from plans/pump-then-process.md
    var defaults = new SlidingWindowInboxOptions();
    await Assert.That(defaults.SlidingWindow).IsEqualTo(TimeSpan.FromMilliseconds(50));
    await Assert.That(defaults.MaxWait).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(defaults.MaxSize).IsEqualTo(100);
  }

  [Test]
  public async Task AppendAsync_OutOfOrderArrivals_FlushedSortedByMessageIdAsync() {
    // Slice 18 invariant: every batch boundary delivers event_id-sorted output. Concurrent
    // producers (transport consumers across multiple RabbitMQ channels) can deposit messages
    // into the inbox sliding window in non-deterministic order. Cursor-by-event_id on the
    // perspective-apply side depends on lex order matching commit order, so the window must
    // sort before flushing to wh_inbox.
    var captured = new List<InboxMessage[]>();
    var flushedSignal = new TaskCompletionSource();

    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, ct) => {
        captured.Add(msgs);
        flushedSignal.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    // Generate three messages — Uuid7 provider guarantees m1 < m2 < m3 lex order.
    var m1 = _makeMessage();
    var m2 = _makeMessage();
    var m3 = _makeMessage();

    // Enqueue out-of-order on purpose — mirror of the producer race that triggers
    // cursor inversion downstream.
    await sut.AppendAsync(m3);
    await sut.AppendAsync(m1);
    await sut.AppendAsync(m2);

    await flushedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(captured.Count).IsEqualTo(1);
    var batch = captured[0];
    await Assert.That(batch.Length).IsEqualTo(3);
    // Batch MUST be sorted by MessageId ASC — locks the slice-18 invariant.
    await Assert.That(batch[0].MessageId).IsEqualTo(m1.MessageId);
    await Assert.That(batch[1].MessageId).IsEqualTo(m2.MessageId);
    await Assert.That(batch[2].MessageId).IsEqualTo(m3.MessageId);
  }

  // ===== helpers =====

  private InboxMessage _makeMessage() {
    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(messageId),
      JsonDocument.Parse("{}").RootElement,
      []);
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "test",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
    };
  }
}
