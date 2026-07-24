#pragma warning disable CA1707

using System.Collections.Concurrent;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the <see cref="IEventStore.AppendBatchAsync{TMessage}"/> default
/// implementation. Backends MAY override for bulk performance — composite
/// expansion (W3 slice 10) leans on this for 5K-inner composites to land
/// in a single round-trip on backends that override (e.g., EFCore Postgres
/// via bulk INSERT). The default impl preserves correctness by looping
/// AppendAsync; the contract test pins the observable behavior.
/// </summary>
/// <docs>fundamentals/events/event-store#append-batch</docs>
public class EventStoreAppendBatchTests {

  [Test]
  public async Task AppendBatchAsync_EmptyList_NoOpAsync() {
    var capture = new _capturingStore();
    IEventStore store = capture;
    var entries = new List<(Guid streamId, MessageEnvelope<_evt> envelope)>();

    await store.AppendBatchAsync(entries, CancellationToken.None);

    await Assert.That(capture.AppendCalls.Count).IsEqualTo(0)
      .Because("Empty batch MUST be a no-op — receivers may pass an empty list when a composite contains no inner events; no event-store activity should result.");
  }

  [Test]
  public async Task AppendBatchAsync_DefaultImpl_LoopsAppendAsyncInOrderAsync() {
    var capture = new _capturingStore();
    IEventStore store = capture;
    var s1 = Guid.NewGuid();
    var s2 = Guid.NewGuid();
    var s3 = Guid.NewGuid();
    var entries = new List<(Guid streamId, MessageEnvelope<_evt> envelope)> {
      (s1, _env("first")),
      (s2, _env("second")),
      (s3, _env("third")),
    };

    await store.AppendBatchAsync(entries, CancellationToken.None);

    await Assert.That(capture.AppendCalls.Count).IsEqualTo(3);
    await Assert.That(capture.AppendCalls[0].StreamId).IsEqualTo(s1);
    await Assert.That(capture.AppendCalls[1].StreamId).IsEqualTo(s2);
    await Assert.That(capture.AppendCalls[2].StreamId).IsEqualTo(s3);
    await Assert.That(((_evt)capture.AppendCalls[0].Payload).Tag).IsEqualTo("first");
    await Assert.That(((_evt)capture.AppendCalls[2].Payload).Tag).IsEqualTo("third");
  }

  [Test]
  public async Task AppendBatchAsync_RespectsCancellationBetweenEntriesAsync() {
    var capture = new _capturingStore();
    IEventStore store = capture;
    using var cts = new CancellationTokenSource();
    var entries = new List<(Guid streamId, MessageEnvelope<_evt> envelope)> {
      (Guid.NewGuid(), _env("ok")),
      (Guid.NewGuid(), _env("cancel-after-this")),
      (Guid.NewGuid(), _env("never-reached")),
    };
    // Cancel after the second append fires.
    capture.OnAppend = call => {
      if (((_evt)call.Payload).Tag == "cancel-after-this") {
        cts.Cancel();
      }
    };

    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
      await store.AppendBatchAsync(entries, cts.Token));

    await Assert.That(capture.AppendCalls.Count).IsEqualTo(2)
      .Because("Cancellation between entries MUST stop the loop — entries past the cancel point MUST NOT be appended.");
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static MessageEnvelope<_evt> _env(string tag) =>
    new() {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = new _evt(tag),
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
    };

  private sealed record _evt(string Tag) : IEvent;

  /// <summary>
  /// Bare-minimum IEventStore that captures AppendAsync calls so tests can
  /// prove the default AppendBatchAsync loops through them.
  /// </summary>
  private sealed class _capturingStore : IEventStore {
    public List<(Guid StreamId, object Payload)> AppendCalls { get; } = [];
    public Action<(Guid StreamId, object Payload)>? OnAppend { get; set; }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
      var call = (streamId, (object)envelope.Payload!);
      AppendCalls.Add(call);
      OnAppend?.Invoke(call);
      return Task.CompletedTask;
    }

    // The rest of IEventStore is not exercised by these tests — minimal stubs.
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull
      => throw new NotImplementedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default)
      => throw new NotImplementedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default)
      => throw new NotImplementedException();
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default)
      => throw new NotImplementedException();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default)
      => throw new NotImplementedException();
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default)
      => throw new NotImplementedException();
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default)
      => throw new NotImplementedException();
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes)
      => throw new NotImplementedException();
  }
}
