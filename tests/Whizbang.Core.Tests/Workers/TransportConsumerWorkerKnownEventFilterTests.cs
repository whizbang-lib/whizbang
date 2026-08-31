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
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Batch-handler filtering branches in <see cref="TransportConsumerWorker"/>:
/// <list type="bullet">
/// <item><description>Known-event-type filter dropping an ENTIRE batch before the inbox insert
/// (the all-filtered early return) and recording the deduplicated metric</description></item>
/// <item><description>Owned-domain echo suppression falling through for payload types WITHOUT
/// a namespace (the null-namespace guard in the owned-namespace check)</description></item>
/// </list>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[Category("Workers")]
public class TransportConsumerWorkerKnownEventFilterTests {

  [Test]
  public async Task BatchHandler_KnownEventStored_UnknownEventFilteredBeforeInsertAsync() {
    using var meterFactory = new TestMeterFactory();
    var metrics = new TransportMetrics(new WhizbangMetrics(meterFactory));
    using var metricHelper = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var provider = new MutableEventTypeProvider([typeof(FilterCoverageKnownEvent)]);
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IEventTypeProvider>(provider);
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    await using var sp = services.BuildServiceProvider();

    var transport = new FilterTransport();
    var worker = _buildWorker(transport, sp, metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    // Phase 1 — a known event type flows through AND initializes the worker's cached
    // known-event-type set from the provider's current list.
    await transport.DeliverBatchAsync([
      new TransportMessage(_createJsonEnvelope(MessageId.New()), _envelopeTypeFor(typeof(FilterCoverageKnownEvent)))
    ]);
    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("Events in the known-type set must reach the inbox.");

    // Phase 2 — the provider now also claims the second event type, so the message
    // classifies as an event, but the known set was cached in phase 1 and does NOT
    // contain it. The defense-in-depth filter must drop the whole batch before the
    // inbox insert and count the drop as deduplicated.
    provider.EventTypes.Add(typeof(FilterCoverageUnknownEvent));
    await transport.DeliverBatchAsync([
      new TransportMessage(_createJsonEnvelope(MessageId.New()), _envelopeTypeFor(typeof(FilterCoverageUnknownEvent)))
    ]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("An all-filtered batch must produce NO additional inbox rows — the handler returns before the insert.");
    var dedup = metricHelper.GetByName("whizbang.transport.inbox.messages_deduplicated");
    await Assert.That(dedup).Count().IsEqualTo(1)
      .Because("The filter must record exactly one deduplicated-message measurement for the dropped event.");
    await Assert.That(dedup[0].Value).IsEqualTo(1d);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task BatchHandler_OwnedDomains_PayloadWithoutNamespace_IsNotDiscardedAsync() {
    var routing = new RoutingOptions();
    routing.OwnDomains("TestOwned");
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    await using var sp = services.BuildServiceProvider();

    var transport = new FilterTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("filter-owned-topic"));
    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      routingOptions: Options.Create(routing),
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    // The inner payload type has NO dot in its full name → no namespace can be
    // extracted → the owned-namespace check must fall through (guard branch), never
    // classify it as owned, and the message must be stored normally.
    const string envelopeType =
      "Whizbang.Core.Observability.MessageEnvelope`1[[NakedPayload, TestApp]], Whizbang.Core";
    var messageId = MessageId.New();
    await transport.DeliverBatchAsync([new TransportMessage(_createJsonEnvelope(messageId), envelopeType)]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("A payload type without a namespace can never match an owned domain — the guard must fall through to normal processing, not discard the message.");
    await Assert.That(coordinator.StoredMessages[0].MessageId).IsEqualTo(messageId.Value);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static TransportConsumerWorker _buildWorker(
      ITransport transport, IServiceProvider serviceProvider, TransportMetrics? metrics = null) {
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("filter-topic"));
    return new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: metrics,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());
  }

  private static string _envelopeTypeFor(Type payloadType) =>
    $"Whizbang.Core.Observability.MessageEnvelope`1[[{TypeNameFormatter.Format(payloadType)}]], Whizbang.Core";

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

  /// <summary>Event-type provider whose list can grow mid-test — lets phase 2 present an
  /// event type the worker's phase-1-cached known set has never seen.</summary>
  private sealed class MutableEventTypeProvider(List<Type> eventTypes) : IEventTypeProvider {
    public List<Type> EventTypes { get; } = eventTypes;
    public IReadOnlyList<Type> GetEventTypes() => [.. EventTypes];
  }

  /// <summary>Transport that captures the batch handler and delivers batches on demand.</summary>
  private sealed class FilterTransport : ITransport {
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
      return Task.FromResult<ISubscription>(new FilterSubscription());
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

  private sealed class FilterSubscription : ISubscription {
    public bool IsActive => true;

    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() {
      // Nothing to release — test double.
    }
  }
}

/// <summary>Event type present in the worker's cached known-event set (phase 1).</summary>
internal sealed record FilterCoverageKnownEvent(string Name);

/// <summary>Event type the provider claims only AFTER the known set was cached (phase 2).</summary>
internal sealed record FilterCoverageUnknownEvent(string Name);
