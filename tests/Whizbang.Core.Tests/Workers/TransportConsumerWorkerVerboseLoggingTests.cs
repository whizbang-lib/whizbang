using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Exercises the Information/Debug-gated logging blocks in
/// <see cref="TransportConsumerWorker"/> that a NullLogger never enters:
/// startup destination listing (including the "#" routing-key fallback),
/// owned-domain provisioning count, per-destination subscribe details, the
/// healthy/failed startup summary, and the batch receive/insert logs.
/// Every test asserts the observable log output AND the functional outcome.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[Category("Workers")]
public class TransportConsumerWorkerVerboseLoggingTests {

  [Test]
  public async Task ExecuteAsync_VerboseLogger_LogsStartupProvisioningAndSubscriptionDetailsAsync() {
    // The "started with" summary is logged AFTER SubscriptionsReady resolves, so the
    // logger itself provides the completion signal for that final message.
    var logger = new CapturingLogger("TransportConsumerWorker started with");
    var routing = new RoutingOptions();
    routing.OwnDomains("TestApp.Owned");

    var services = new ServiceCollection();
    services.AddSingleton<IInfrastructureProvisioner>(new NoOpProvisioner());
    services.AddSingleton<IOptions<RoutingOptions>>(Options.Create(routing));
    await using var sp = services.BuildServiceProvider();

    var transport = new LoggingTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("logging-topic-plain"));
    options.Destinations.Add(new TransportDestination("logging-topic-routed", "orders.created"));

    // RoutingOptions is deliberately registered ONLY in DI (for the provisioning block),
    // not passed to the constructor — keeps owned-domain echo discard out of the picture.
    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: logger,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));
    await logger.SignalLogged.Task.WaitAsync(TimeSpan.FromSeconds(10));

    var log = string.Join("\n", logger.Messages);
    await Assert.That(log).Contains("TransportConsumerWorker starting with 2 destinations")
      .Because("The startup Information block must report the configured destination count.");
    await Assert.That(log).Contains("Destination: logging-topic-plain (routing key: #)")
      .Because("A null routing key must be rendered with the '#' wildcard fallback.");
    await Assert.That(log).Contains("Destination: logging-topic-routed (routing key: orders.created)")
      .Because("Explicit routing keys must be logged verbatim.");
    await Assert.That(log).Contains("Provisioning infrastructure for 1 owned domains")
      .Because("The provisioning Debug block must report how many owned domains are being provisioned.");
    await Assert.That(log).Contains("Creating subscription for destination: logging-topic-routed")
      .Because("Each destination's subscribe attempt must be logged at Debug.");
    await Assert.That(log).Contains("started with 2 healthy, 0 failed subscriptions")
      .Because("The startup summary must report per-status subscription counts.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task BatchHandler_VerboseLogger_LogsBatchReceiveAndInsertAsync() {
    var logger = new CapturingLogger();
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    await using var sp = services.BuildServiceProvider();

    var transport = new LoggingTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("logging-batch-topic"));
    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: logger,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    const string envelopeType =
      "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestMessage, TestApp]], Whizbang.Core";
    // The batch handler runs synchronously inside DeliverBatchAsync — once it returns,
    // both the receive log and the post-insert ACK log have been written.
    await transport.DeliverBatchAsync([
      new TransportMessage(_createJsonEnvelope(MessageId.New()), envelopeType),
      new TransportMessage(_createJsonEnvelope(MessageId.New()), envelopeType)
    ]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(2)
      .Because("Both batch messages must be inserted into the inbox.");
    var log = string.Join("\n", logger.Messages);
    await Assert.That(log).Contains("Processing batch of 2 messages from transport")
      .Because("The Information block must report the received batch size.");
    await Assert.That(log).Contains("Batch of 2 messages inserted into inbox")
      .Because("The Debug block must confirm the insert count before the transport ACK.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static MessageEnvelope<JsonElement> _createJsonEnvelope(MessageId messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  // ============================================================
  // Test doubles
  // ============================================================

  /// <summary>Logger with ALL levels enabled that captures formatted messages and optionally
  /// signals when a message containing the given fragment is logged.</summary>
  private sealed class CapturingLogger(string? signalFragment = null) : ILogger<TransportConsumerWorker> {
    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];

    public TaskCompletionSource SignalLogged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<string> Messages {
      get {
        lock (_lock) {
          return [.. _messages];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      lock (_lock) {
        _messages.Add(message);
      }
      if (signalFragment is not null && message.Contains(signalFragment, StringComparison.Ordinal)) {
        SignalLogged.TrySetResult();
      }
    }
  }

  private sealed class NoOpProvisioner : IInfrastructureProvisioner {
    public Task ProvisionOwnedDomainsAsync(
        IReadOnlySet<string> ownedDomains, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  /// <summary>Transport that captures the batch handler and delivers batches on demand.</summary>
  private sealed class LoggingTransport : ITransport {
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;

    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      _batchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new LoggingSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotSupportedException();

    public Task DeliverBatchAsync(IReadOnlyList<TransportMessage> messages)
      => _batchHandler is null
        ? throw new InvalidOperationException("No batch handler subscribed yet")
        : _batchHandler(messages, CancellationToken.None);
  }

  private sealed class LoggingSubscription : ISubscription {
    public bool IsActive => true;

    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() {
      // Nothing to release — test double.
    }
  }
}
