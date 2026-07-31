using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Directed-message gate: an envelope whose <see cref="IMessageEnvelope.Target"/> names a
/// DIFFERENT service must be discarded at the transport receive seam — before deserialization,
/// inbox storage, or fan-out. A matching target and an absent target (broadcast, the default)
/// are accepted. Targeted traffic is point-to-point (repair / control-plane / response); every
/// non-target's copy is noise by definition, and the discard keeps the cost of a directed
/// repair or response O(1) for uninvolved services.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Observability/MessageEnvelope.cs</code-under-test>
[NotInParallel("DirectedTarget")]
public class TransportConsumerWorkerDirectedTargetTests {

  private const string THIS_SERVICE = "OrderService";
  private const string OTHER_SERVICE = "ReportingService";

  /// <summary>Wire-format envelope type name — the transport delivers
  /// <c>MessageEnvelope`1[[Inner, Asm]]</c> identities, which the inbox builder parses.</summary>
  private static readonly string _eventType =
    "Whizbang.Core.Observability.MessageEnvelope`1[[" + typeof(FakeDirectedEvent).FullName + ", "
    + typeof(FakeDirectedEvent).Assembly.GetName().Name + "]], Whizbang.Core";

  [Test]
  public async Task ForeignTarget_IsDiscardedBeforeInboxAsync() {
    using var worker = _createWorker(serviceName: THIS_SERVICE);
    await worker.StartAsync();

    var envelope = _createEnvelope();
    envelope.Target = OTHER_SERVICE;

    await worker.SimulateMessageAsync(envelope, _eventType);

    await worker.StopAsync();
    await Assert.That(worker.StoredInboxCount).IsEqualTo(0)
      .Because("a message directed at another service is point-to-point traffic this service " +
               "must discard at the receive seam — storing it costs every uninvolved service " +
               "deserialize + fan-out + conflict-skip work for traffic that was never for them.");
  }

  [Test]
  public async Task MatchingTarget_IsAcceptedAsync() {
    using var worker = _createWorker(serviceName: THIS_SERVICE);
    await worker.StartAsync();

    var envelope = _createEnvelope();
    envelope.Target = THIS_SERVICE;

    await worker.SimulateMessageAsync(envelope, _eventType);

    await worker.StopAsync();
    await Assert.That(worker.StoredInboxCount).IsEqualTo(1)
      .Because("a message directed at THIS service must flow through the normal inbox path.");
  }

  [Test]
  public async Task AbsentTarget_IsAcceptedAsync() {
    using var worker = _createWorker(serviceName: THIS_SERVICE);
    await worker.StartAsync();

    var envelope = _createEnvelope();

    await worker.SimulateMessageAsync(envelope, _eventType);

    await worker.StopAsync();
    await Assert.That(worker.StoredInboxCount).IsEqualTo(1)
      .Because("an absent target means broadcast — today's semantics, unchanged.");
  }

  [Test]
  public async Task ForeignTarget_WithoutServiceIdentity_IsAcceptedFailOpenAsync() {
    // No IServiceInstanceProvider wired → this service cannot know its own name → targeted
    // messages are ACCEPTED (fail-open). The event-id conflict skip downstream keeps acceptance
    // idempotent; silently discarding on unknown identity could starve a legitimate target.
    using var worker = _createWorker(serviceName: null);
    await worker.StartAsync();

    var envelope = _createEnvelope();
    envelope.Target = OTHER_SERVICE;

    await worker.SimulateMessageAsync(envelope, _eventType);

    await worker.StopAsync();
    await Assert.That(worker.StoredInboxCount).IsEqualTo(1)
      .Because("without a known service identity the gate must fail open — acceptance is " +
               "idempotent downstream; a wrong discard could starve the legitimate target.");
  }

  [Test]
  public async Task EnvelopeTarget_RoundTripsAsTgtAndOmitsWhenNullAsync() {
    var targeted = _createEnvelope();
    targeted.Target = OTHER_SERVICE;
    var json = JsonSerializer.Serialize(targeted);
    await Assert.That(json).Contains("\"tgt\":\"" + OTHER_SERVICE + "\"")
      .Because("the target rides the wire envelope under the compact key `tgt`.");
    var back = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(json);
    await Assert.That(back!.Target).IsEqualTo(OTHER_SERVICE);

    var broadcast = _createEnvelope();
    var broadcastJson = JsonSerializer.Serialize(broadcast);
    await Assert.That(broadcastJson.Contains("\"tgt\"")).IsFalse()
      .Because("undirected envelopes pay zero wire cost — the key is omitted when null.");
  }

  // ========================================
  // Test Infrastructure (mirrors TransportConsumerWorkerOwnedEventDiscardTests)
  // ========================================

  private static TestWorkerWrapper _createWorker(string? serviceName) {
    var transport = new StubTransport();
    var workStrategy = new StubWorkStrategy();
    var noOpCoordinator = new NoOpWorkCoordinator();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddSingleton<IEventTypeProvider>(new StubEventTypeProvider());
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    services.Configure<RoutingOptions>(_ => { });
    var sp = services.BuildServiceProvider();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(), new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      routingOptions: sp.GetRequiredService<IOptions<RoutingOptions>>(),
      serviceInstanceProvider: serviceName is null ? null : new StubServiceInstanceProvider(serviceName)
    );

    return new TestWorkerWrapper(worker, transport, noOpCoordinator);
  }

  /// <summary>Wire-realistic envelope: transports deliver <c>JsonElement</c> payloads, which the
  /// inbox builder uses directly (no serializer registration needed in this minimal harness) —
  /// so accepted messages genuinely reach storage and the assertions discriminate.</summary>
  private static MessageEnvelope<JsonElement> _createEnvelope() {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = JsonSerializer.SerializeToElement(new FakeDirectedEvent()),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = OTHER_SERVICE,
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 1234
          }
        }
      ],
      DispatchContext = new MessageDispatchContext {
        Mode = DispatchModes.Outbox,
        Source = MessageSource.Outbox
      }
    };
  }

  /// <summary>Fake event payload. Internal so <c>FullName</c> resolves.</summary>
  internal sealed class FakeDirectedEvent : IEvent;

  /// <summary>Empty on purpose: with no known event types the known-event post-filter is inert,
  /// so storage outcomes reflect ONLY the directed-target gate under test.</summary>
  private sealed class StubEventTypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class TestWorkerWrapper(
    TransportConsumerWorker worker,
    StubTransport transport,
    NoOpWorkCoordinator coordinator) : IDisposable {
    private CancellationTokenSource? _cts;

    public int StoredInboxCount => coordinator.StoredInboxCount;

    public async Task StartAsync() {
      _cts = new CancellationTokenSource();
      _ = worker.StartAsync(_cts.Token);
      await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));
    }

    public Task SimulateMessageAsync(IMessageEnvelope envelope, string envelopeType) =>
      transport.SimulateMessageReceivedAsync(envelope, envelopeType);

    public async Task StopAsync() {
      _cts?.Cancel();
      await Task.Yield();
    }

    public void Dispose() {
      _cts?.Dispose();
      transport.Dispose();
    }
  }

  private sealed class StubServiceInstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId => Guid.NewGuid();
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1234;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = serviceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class StubTransport : ITransport, IDisposable {
    private Func<IMessageEnvelope, string?, CancellationToken, Task>? _handler;
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;
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
      if (_batchHandler != null) {
        await _batchHandler([new TransportMessage(envelope, envelopeType)], CancellationToken.None);
      } else if (_handler != null) {
        await _handler(envelope, envelopeType, CancellationToken.None);
      }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
      string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination, CancellationToken cancellationToken = default) {
      _handler = handler;
      _subscribeSignal.Release();
      return Task.FromResult<ISubscription>(new StubSubscription());
    }
    public Task<ISubscription> SubscribeBatchAsync(
      Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
      TransportDestination destination,
      TransportBatchOptions batchOptions,
      CancellationToken cancellationToken = default) {
      _batchHandler = batchHandler;
      _subscribeSignal.Release();
      return Task.FromResult<ISubscription>(new StubSubscription());
    }
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope, TransportDestination destination,
      CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class ConsoleCaptureLogger : Microsoft.Extensions.Logging.ILogger<TransportConsumerWorker> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Console.WriteLine($"[TCW {logLevel}] {formatter(state, exception)} {exception}");
    }
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
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      return FlushAndGetBatchAsync(flags, ct);
    }

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }
  }
}
