using System.Text.Json;
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
/// Locks the passthrough behavior of <see cref="ImmediateInboxBatchStrategy"/> and
/// <see cref="ImmediateOutboxBatchStrategy"/>. Slices 7 and 12 introduced these as the
/// no-batching opt-out for low-throughput tenants; only the DI registration was tested.
/// A passthrough that silently regressed (e.g., dropping the message, retaining it
/// instead of forwarding) would be hard to detect — both invocations look the same to
/// callers. These behavior tests lock the contract.
/// </summary>
public class ImmediateBatchStrategyBehaviorTests {

  private static InboxMessage _makeInboxMessage() {
    var msgId = (Guid)TrackedGuid.NewMedo();
    return new InboxMessage {
      MessageId = msgId,
      HandlerName = "TestHandler",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(msgId),
        Hops = [],
      },
    };
  }

  private static OutboxMessage _makeOutboxMessage() {
    var msgId = (Guid)TrackedGuid.NewMedo();
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(msgId),
      JsonDocument.Parse("{}").RootElement,
      []);
    return new OutboxMessage {
      MessageId = msgId,
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(msgId),
        Hops = [],
      },
    };
  }

  // ===== ImmediateInboxBatchStrategy =====

  [Test]
  public async Task ImmediateInbox_AppendAsync_FlushesEachMessageAsSingletonBatchAsync() {
    var captured = new List<InboxMessage[]>();
    await using var sut = new ImmediateInboxBatchStrategy((msgs, _) => {
      captured.Add(msgs);
      return Task.CompletedTask;
    });

    var m1 = _makeInboxMessage();
    var m2 = _makeInboxMessage();
    await sut.AppendAsync(m1);
    await sut.AppendAsync(m2);

    await Assert.That(captured.Count).IsEqualTo(2)
      .Because("Two AppendAsync calls must produce two flush callbacks — no batching.");
    await Assert.That(captured[0].Length).IsEqualTo(1);
    await Assert.That(captured[0][0].MessageId).IsEqualTo(m1.MessageId);
    await Assert.That(captured[1].Length).IsEqualTo(1);
    await Assert.That(captured[1][0].MessageId).IsEqualTo(m2.MessageId);
  }

  [Test]
  public async Task ImmediateInbox_AppendAsync_NullMessage_ThrowsAsync() {
    await using var sut = new ImmediateInboxBatchStrategy((_, _) => Task.CompletedTask);
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await sut.AppendAsync(null!));
  }

  [Test]
  public async Task ImmediateInbox_AppendAsync_AfterFlushAndStop_ThrowsObjectDisposedAsync() {
    var sut = new ImmediateInboxBatchStrategy((_, _) => Task.CompletedTask);
    await sut.FlushAndStopAsync();
    await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
      await sut.AppendAsync(_makeInboxMessage()));
  }

  [Test]
  public async Task ImmediateInbox_FlushAndStop_IsIdempotentAsync() {
    var sut = new ImmediateInboxBatchStrategy((_, _) => Task.CompletedTask);
    await sut.FlushAndStopAsync();
    await sut.FlushAndStopAsync();   // second call must not throw
    await sut.DisposeAsync();        // dispose must not throw
  }

  // ===== ImmediateOutboxBatchStrategy =====

  [Test]
  public async Task ImmediateOutbox_AppendAsync_FlushesEachMessageAsSingletonBatchAsync() {
    var captured = new List<OutboxMessage[]>();
    await using var sut = new ImmediateOutboxBatchStrategy((msgs, _) => {
      captured.Add(msgs);
      return Task.CompletedTask;
    });

    var m1 = _makeOutboxMessage();
    var m2 = _makeOutboxMessage();
    await sut.AppendAsync(m1);
    await sut.AppendAsync(m2);

    await Assert.That(captured.Count).IsEqualTo(2);
    await Assert.That(captured[0].Length).IsEqualTo(1);
    await Assert.That(captured[0][0].MessageId).IsEqualTo(m1.MessageId);
    await Assert.That(captured[1].Length).IsEqualTo(1);
    await Assert.That(captured[1][0].MessageId).IsEqualTo(m2.MessageId);
  }

  [Test]
  public async Task ImmediateOutbox_AppendAsync_NullMessage_ThrowsAsync() {
    await using var sut = new ImmediateOutboxBatchStrategy((_, _) => Task.CompletedTask);
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await sut.AppendAsync(null!));
  }

  [Test]
  public async Task ImmediateOutbox_AppendAsync_AfterFlushAndStop_ThrowsObjectDisposedAsync() {
    var sut = new ImmediateOutboxBatchStrategy((_, _) => Task.CompletedTask);
    await sut.FlushAndStopAsync();
    await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
      await sut.AppendAsync(_makeOutboxMessage()));
  }

  [Test]
  public async Task ImmediateOutbox_FlushAndStop_IsIdempotentAsync() {
    var sut = new ImmediateOutboxBatchStrategy((_, _) => Task.CompletedTask);
    await sut.FlushAndStopAsync();
    await sut.FlushAndStopAsync();   // second call must not throw
    await sut.DisposeAsync();        // dispose must not throw
  }

  // ===== Cross-cutting: passthrough propagates flush exceptions =====

  [Test]
  public async Task ImmediateInbox_FlushCallbackThrows_PropagatesToCallerAsync() {
    // The strategy is a passthrough — exceptions from the flush callback must propagate
    // to AppendAsync's caller, NOT swallow silently. SlidingWindow strategies log + drop
    // (transport redelivery covers it); Immediate's contract is direct.
    await using var sut = new ImmediateInboxBatchStrategy((_, _)
      => throw new InvalidOperationException("flush failed"));
    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await sut.AppendAsync(_makeInboxMessage()));
  }

  [Test]
  public async Task ImmediateOutbox_FlushCallbackThrows_PropagatesToCallerAsync() {
    await using var sut = new ImmediateOutboxBatchStrategy((_, _)
      => throw new InvalidOperationException("flush failed"));
    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await sut.AppendAsync(_makeOutboxMessage()));
  }
}
