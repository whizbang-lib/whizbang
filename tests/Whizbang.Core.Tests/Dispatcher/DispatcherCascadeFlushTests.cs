using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Pins the cascade-to-outbox flush semantics so a future regression cannot silently
/// re-introduce per-event synchronous flushing against the Interval strategy
/// (as happened in commit 8b393c1e when FlushMode was introduced but the cascade path
/// kept the Required default — the bug fixed by splitting the API into FlushAsync
/// vs FlushAndGetBatchAsync).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[NotInParallel("CascadeFlushMethod")]
public class DispatcherCascadeFlushTests {

  public record CascadeFlushCommand(Guid EntityId);
  public record CascadeFlushEvent([property: StreamId] Guid EntityId) : IEvent;

  public class CascadeFlushCommandHandler : IReceptor<CascadeFlushCommand, CascadeFlushEvent> {
    public ValueTask<CascadeFlushEvent> HandleAsync(CascadeFlushCommand message, CancellationToken cancellationToken) {
      return ValueTask.FromResult(new CascadeFlushEvent(message.EntityId));
    }
  }

  private sealed class MethodRecordingStrategy : IWorkCoordinatorStrategy {
    public int FlushAsyncCalls { get; private set; }
    public int FlushAndGetBatchAsyncCalls { get; private set; }
    public List<OutboxMessage> QueuedOutboxMessages { get; } = [];

    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutboxMessages.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      FlushAsyncCalls++;
      return Task.CompletedTask;
    }

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      FlushAndGetBatchAsyncCalls++;
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonElement = System.Text.Json.JsonSerializer.SerializeToElement(new { });
      var jsonEnvelope = new MessageEnvelope<System.Text.Json.JsonElement> {
        MessageId = envelope.MessageId,
        Payload = jsonElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      return new SerializedEnvelope(
        jsonEnvelope,
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!
      );
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) {
      throw new NotImplementedException();
    }
  }

  [Test]
  public async Task CascadeToOutbox_UsesFireAndForgetFlushAsync_NotFlushAndGetBatchAsyncAsync() {
    var strategy = new MethodRecordingStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var sp = services.BuildServiceProvider();
    var dispatcher = sp.GetRequiredService<IDispatcher>();

    await dispatcher.LocalInvokeAsync<CascadeFlushEvent>(new CascadeFlushCommand(Guid.NewGuid()));

    await Assert.That(strategy.QueuedOutboxMessages.Count).IsGreaterThanOrEqualTo(1)
      .Because("Cascaded event should reach the outbox queue");
    await Assert.That(strategy.FlushAsyncCalls).IsGreaterThanOrEqualTo(1)
      .Because("Cascade should signal the strategy via fire-and-forget FlushAsync at least once");
    await Assert.That(strategy.FlushAndGetBatchAsyncCalls).IsEqualTo(0)
      .Because("Cascade must never force a synchronous batch-returning flush — that defeats Interval/Batch batching");
  }
}
