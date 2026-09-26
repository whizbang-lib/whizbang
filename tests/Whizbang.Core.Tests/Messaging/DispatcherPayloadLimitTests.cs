using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The dispatcher refuses a message whose serialized payload is over the limit before it reaches the
/// outbox, so an oversized message is never stored or sent, and the caller gets the exception.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Unit")]
public class DispatcherPayloadLimitTests {
  public record SizedCommand(int Size) : ICommand;
  public record SizedEvent([property: StreamId] Guid StreamId, int Size) : IEvent;
  public record BlobEvent([property: StreamId] Guid StreamId, string Blob) : IEvent;

  private const long LIMIT = 1000;

  private sealed class RecordingStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutbox { get; } = [];
    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutbox.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
  }

  /// <summary>Serializes a sized message to a payload of exactly its declared size.</summary>
  private sealed class SizedEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var size = envelope.Payload switch {
        SizedCommand c => c.Size,
        SizedEvent e => e.Size,
        _ => 10,
      };
      using var doc = JsonDocument.Parse($"{{\"v\":\"{new string('x', size - 8)}\"}}");
      var jsonEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = doc.RootElement.Clone(),
        Hops = envelope.Hops?.ToList() ?? [],
        DispatchContext = envelope.DispatchContext,
      };
      var messageType = typeof(TMessage).AssemblyQualifiedName!;
      return new SerializedEnvelope(jsonEnvelope, $"Whizbang.Core.Observability.MessageEnvelope`1[[{messageType}]], Whizbang.Core", messageType);
    }
    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => new();
  }

  private sealed class TestScopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public IServiceScope CreateScope() => new TestScope(provider);
    private sealed class TestScope(IServiceProvider provider) : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = provider;
      public void Dispose() { }
    }
  }

  /// <summary>No local receptors, so every message routes to the outbox; exposes the cascade path.</summary>
  private sealed class OutboxOnlyDispatcher(IServiceProvider sp) : Core.Dispatcher(
      sp, new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()), envelopeSerializer: new SizedEnvelopeSerializer()) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;

    public Task CascadeAsync(IMessage message) =>
      PublishToOutboxDynamicAsync(message, message.GetType(), Whizbang.Core.ValueObjects.MessageId.New());
  }

  private static (OutboxOnlyDispatcher Dispatcher, RecordingStrategy Strategy) _dispatcher(long? limit = LIMIT) {
    var strategy = new RecordingStrategy();
    var services = new ServiceCollection();
    services.AddSingleton(new WhizbangCoreOptions { MaxMessagePayloadBytes = limit });
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceScopeFactory>(sp => new TestScopeFactory(sp));
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    return (new OutboxOnlyDispatcher(services.BuildServiceProvider()), strategy);
  }

  [Test]
  public async Task Send_UnderTheLimit_ReachesTheOutboxAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.SendAsync(new SizedCommand(500));

    await Assert.That(strategy.QueuedOutbox).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Send_OverTheLimit_ThrowsAndNeverReachesTheOutboxAsync() {
    var (dispatcher, strategy) = _dispatcher();

    var ex = await Assert.ThrowsAsync<MessagePayloadTooLargeException>(() => dispatcher.SendAsync(new SizedCommand(1500)));

    await Assert.That(ex!.PayloadBytes).IsEqualTo(1500);
    await Assert.That(ex.LimitBytes).IsEqualTo(LIMIT);
    await Assert.That(strategy.QueuedOutbox).IsEmpty()
      .Because("an oversized message must never be stored, or every consumer that receives it pays for it");
  }

  [Test]
  public async Task Send_Untyped_OverTheLimit_ThrowsAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await Assert.ThrowsAsync<MessagePayloadTooLargeException>(() => dispatcher.SendAsync((object)new SizedCommand(1500)));
    await Assert.That(strategy.QueuedOutbox).IsEmpty();
  }

  [Test]
  public async Task Send_WithACallLimit_OverridesTheDefaultBothWaysAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.SendAsync(new SizedCommand(1500), new DispatchOptions().WithMaxPayloadBytes(2000));
    await dispatcher.SendAsync((object)new SizedCommand(1500), new DispatchOptions().WithMaxPayloadBytes(2000));
    var ex = await Assert.ThrowsAsync<MessagePayloadTooLargeException>(
      () => dispatcher.SendAsync(new SizedCommand(500), new DispatchOptions().WithMaxPayloadBytes(100)));
    await Assert.ThrowsAsync<MessagePayloadTooLargeException>(
      () => dispatcher.SendAsync((object)new SizedCommand(500), new DispatchOptions().WithMaxPayloadBytes(100)));

    await Assert.That(strategy.QueuedOutbox).Count().IsEqualTo(2);
    await Assert.That(ex!.LimitSource).IsEqualTo(PayloadLimitSource.Call);
  }

  [Test]
  public async Task Publish_OverTheLimit_ThrowsAndNeverReachesTheOutboxAsync() {
    var (dispatcher, strategy) = _dispatcher();

    var ex = await Assert.ThrowsAsync<MessagePayloadTooLargeException>(
      () => dispatcher.PublishAsync(new SizedEvent(Guid.CreateVersion7(), 1500)));

    await Assert.That(ex!.StreamId).IsNotNull();
    await Assert.That(strategy.QueuedOutbox).IsEmpty();
  }

  [Test]
  public async Task Publish_WithACallLimit_OverridesTheDefaultAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.PublishAsync(new SizedEvent(Guid.CreateVersion7(), 1500), new DispatchOptions().WithMaxPayloadBytes(2000));
    await Assert.ThrowsAsync<MessagePayloadTooLargeException>(
      () => dispatcher.PublishAsync(new SizedEvent(Guid.CreateVersion7(), 500), new DispatchOptions().WithMaxPayloadBytes(100)));

    await Assert.That(strategy.QueuedOutbox).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Cascade_OverTheLimit_ThrowsAndNeverReachesTheOutboxAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.CascadeAsync(new BlobEvent(Guid.CreateVersion7(), "small"));
    var ex = await Assert.ThrowsAsync<MessagePayloadTooLargeException>(
      () => dispatcher.CascadeAsync(new BlobEvent(Guid.CreateVersion7(), new string('x', 2000))));

    await Assert.That(ex!.MessageType).Contains(nameof(BlobEvent));
    await Assert.That(strategy.QueuedOutbox).Count().IsEqualTo(1)
      .Because("an event a receptor returns is measured the same way as one it publishes");
  }

  [Test]
  public async Task Send_LimitOff_AcceptsAnySizeAsync() {
    var (dispatcher, strategy) = _dispatcher(limit: null);

    await dispatcher.SendAsync(new SizedCommand(50_000));

    await Assert.That(strategy.QueuedOutbox).Count().IsEqualTo(1);
  }
}
