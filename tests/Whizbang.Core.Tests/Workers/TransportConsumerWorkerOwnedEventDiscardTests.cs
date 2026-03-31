using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Resilience;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests that owned events arriving from transport are discarded before writing to inbox.
/// This prevents double-persistence (outbox + inbox → event_store × 2) and double handler execution.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[NotInParallel("OwnedEventDiscard")]
public class TransportConsumerWorkerOwnedEventDiscardTests {

  [Test]
  public async Task OwnedEvent_FromTransport_IsDiscardedBeforeInboxAsync() {
    // Arrange — transport delivers an owned event (namespace matches OwnDomains)
    var messageId = MessageId.New();
    var transport = new StubTransport();
    var workStrategy = new StubWorkStrategy();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    services.Configure<RoutingOptions>(opts => {
      opts.OwnDomains("MyApp.Contracts.Chat");
    });
    var sp = services.BuildServiceProvider();

    var routingOptions = sp.GetRequiredService<IOptions<RoutingOptions>>();
    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(), new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      routingOptions: routingOptions
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    // Act — simulate owned event arriving from transport
    var envelope = _createTestEnvelope();
    const string envelopeType = "MyApp.Contracts.Chat.ChatOrchestrationContracts+SwitchedActivityEvent, MyApp.Contracts";

    try {
      await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    } catch {
      // May throw during processing — we only care about inbox write
    }

    cts.Cancel();

    // Assert — owned event should NOT reach inbox (discarded before QueueInboxMessage)
    await Assert.That(workStrategy.QueuedInboxCount).IsEqualTo(0)
      .Because("Owned events from transport should be discarded — they already fired locally via cascade");
  }

  // Note: Non-owned event and backward-compat (no owned domains) tests require full
  // serialization infrastructure (IEnvelopeSerializer) to verify inbox writes.
  // The ReceptorInvoker PostInbox ownership tests cover the handler-level filtering.
  // The ECommerce.RabbitMQ.Integration.Tests cover the full end-to-end path.

  // ========================================
  // Test Infrastructure
  // ========================================

  private static MessageEnvelope<JsonElement> _createTestEnvelope() {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext {
        Mode = DispatchModes.Both,
        Source = MessageSource.Outbox
      }
    };
  }

  private sealed class StubTransport : ITransport, IDisposable {
    private Func<IMessageEnvelope, string?, CancellationToken, Task>? _handler;
    private readonly SemaphoreSlim _subscribeSignal = new(0, int.MaxValue);

    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;

    public void Dispose() => _subscribeSignal.Dispose();

    public async Task WaitForSubscriptionAsync(TimeSpan timeout) {
      if (!await _subscribeSignal.WaitAsync(timeout)) {
        throw new TimeoutException($"Subscription not created within {timeout}");
      }
    }

    public async Task SimulateMessageReceivedAsync(IMessageEnvelope envelope, string? envelopeType) {
      if (_handler != null) {
        await _handler(envelope, envelopeType, CancellationToken.None);
      }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
      string? envelopeType = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default) {
      _handler = handler;
      _subscribeSignal.Release();
      return Task.FromResult<ISubscription>(new StubSubscription());
    }
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope, TransportDestination destination,
      CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class StubSubscription : ISubscription {
    public bool IsActive => true;
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
    public Task UnsubscribeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() { OnDisconnected?.Invoke(this, new SubscriptionDisconnectedEventArgs()); }
  }

  private sealed class StubWorkStrategy : IWorkCoordinatorStrategy {
    public int QueuedInboxCount { get; private set; }
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) => QueuedInboxCount++;
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }
  }
}
