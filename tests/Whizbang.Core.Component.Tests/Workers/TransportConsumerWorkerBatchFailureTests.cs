// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #921, at the consumer: a batch that fails (here, the inbox store times out) must reach the
/// transport as a failure, so the transport abandons it instead of completing it, and the worker must
/// keep running and store the next batch. The consumer used to swallow the failure, and every
/// transport completes a message whose handler returned, so the "abandoned" batch was lost.
/// </summary>
[Category("Workers")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class TransportConsumerWorkerBatchFailureTests {

  /// <summary>An ordinary event; public so the generator registers its metadata.</summary>
  public sealed record BatchFailureProbeEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; }
    public string Marker { get; init; } = string.Empty;
  }

  private static OperationCanceledException _statementTimeout()
    => new("Query was canceled", new PostgresException(
        messageText: "canceling statement due to user request",
        severity: "ERROR", invariantSeverity: "ERROR", sqlState: "57014"));

  [Test]
  public async Task InboxStoreTimesOut_TransportToldBatchFailed_WorkerStaysUpAndStoresTheRedeliveryAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var coordinator = new NoOpWorkCoordinator { FailNextInboxStore = _statementTimeout() };
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton(options);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(options));
    await using var sp = services.BuildServiceProvider();
    var transport = new BatchTransport();
    var worker = _worker(transport, sp);
    using var host = new CancellationTokenSource();
    await worker.StartAsync(host.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));

    var message = _envelope();
    var type = EnvelopeTypeNameHelper.Format(TypeNameFormatter.Format(typeof(BatchFailureProbeEvent)));

    Exception? reported = null;
    try {
      await transport.DeliverBatchAsync([new TransportMessage(message, type)]);
    } catch (Exception ex) {
      reported = ex;
    }

    await Assert.That(reported).IsTypeOf<TransportBatchFailedException>()
      .Because("the transport settles on whether the handler threw; a swallowed failure is completed and lost");
    await Assert.That(reported is OperationCanceledException).IsFalse()
      .Because("the statement timeout is a cancellation, and must not reach the transport looking like a shutdown");
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("one failed batch must never stop the worker, let alone the host");

    // The broker redelivers the batch it was not allowed to settle; the live worker stores it.
    await transport.DeliverBatchAsync([new TransportMessage(message, type)]);

    await Assert.That(coordinator.StoredMessages.Select(m => m.MessageId)).IsEquivalentTo([message.MessageId.Value]);

    await host.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  private static MessageEnvelope<BatchFailureProbeEvent> _envelope() {
    var streamId = Guid.CreateVersion7();
    return new MessageEnvelope<BatchFailureProbeEvent> {
      MessageId = MessageId.New(),
      Payload = new BatchFailureProbeEvent { StreamId = streamId, Marker = "redelivered" },
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Metadata = new Dictionary<string, JsonElement> {
          ["AggregateId"] = JsonSerializer.SerializeToElement(streamId.ToString()),
        },
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
  }

  private static TransportConsumerWorker _worker(ITransport transport, IServiceProvider sp) {
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("failure-topic", "failure-sub"));
    return new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance, parallelizeStreams: false),
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      routingOptions: Options.Create(new RoutingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      claimWorkerOptions: Options.Create(new ClaimWorkerOptions()),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      controlClass: Options.Create(new ControlClassOptions()));
  }

  private sealed class BatchTransport : ITransport {
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) {
      _batchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new Subscription());
    }
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull
      => throw new NotSupportedException();
    public Task DeliverBatchAsync(IReadOnlyList<TransportMessage> messages) =>
      _batchHandler is null ? throw new InvalidOperationException("No batch handler subscribed yet") : _batchHandler(messages, CancellationToken.None);
  }

  private sealed class Subscription : ISubscription {
    public bool IsActive => true;
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() { }
  }
}
