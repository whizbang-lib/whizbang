using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// END-TO-END DI wiring lock for receive-path flag derivation. The per-worker unit tests construct
/// <see cref="TransportConsumerWorker"/> by hand and pass the resolvers explicitly — they can stay
/// green while a production host wires the worker differently. This suite mirrors the REAL
/// registration path instead: per-assembly module initializers assign
/// <see cref="ServiceRegistrationCallbacks.MessageTypeCatalog"/>, <c>InvokeAll</c> registers the
/// union catalog + resolvers, the worker is registered via <c>AddHostedService</c> (type-activated,
/// optional ctor parameters filled by the container), and the assertion runs against the worker
/// instance RESOLVED FROM THE CONTAINER. If any link — accumulation, union, resolver registration,
/// or optional-parameter injection — breaks, a collective event received over the wire loses its
/// flags and this test goes red.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/ServiceRegistrationCallbacks.cs</code-under-test>
[Category("Workers")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class TransportConsumerWorkerDiWiringTests {

  private sealed class DiHostEvent;
  private sealed class DiContractsCollectiveEvent;

  private sealed class HostAssemblyCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(DiHostEvent), TypeNameFormatter.FormatClrTypeName(typeof(DiHostEvent)), "event", null),
    ];
  }

  private sealed class ContractsAssemblyCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(DiContractsCollectiveEvent), TypeNameFormatter.FormatClrTypeName(typeof(DiContractsCollectiveEvent)), "event", null) { IsCollective = true },
    ];
  }

  [Test]
  public async Task ContainerWiredWorker_JsonElementCollectivePayload_StampsCollectiveFlagAsync() {
    var saved = ServiceRegistrationCallbacks.SnapshotMessageTypeCatalogRegistrations();
    try {
      ServiceRegistrationCallbacks.MessageTypeCatalog = null;
      // Two module initializers from two assemblies — the shipped generated pattern verbatim,
      // host assembly last (the historical last-wins displacement order).
      ServiceRegistrationCallbacks.MessageTypeCatalog = static s =>
        s.AddSingleton<IMessageTypeCatalog, ContractsAssemblyCatalog>();
      ServiceRegistrationCallbacks.MessageTypeCatalog = static s =>
        s.AddSingleton<IMessageTypeCatalog, HostAssemblyCatalog>();

      var coordinator = new NoOpWorkCoordinator();
      var transport = new DiFlagTransport();
      var services = new ServiceCollection();
      ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());
      services.AddScoped<IWorkCoordinator>(_ => coordinator);
      services.AddSingleton<ITransport>(transport);
      var options = new TransportConsumerOptions();
      options.Destinations.Add(new TransportDestination("di-flags-topic"));
      services.AddSingleton(options);
      services.AddSingleton(new SubscriptionResilienceOptions());
      services.AddSingleton(new JsonSerializerOptions());
      services.AddSingleton(new OrderedStreamProcessor(parallelizeStreams: false, logger: null));
      services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
      services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
      // Production registers both of these in AddWhizbang (ServiceCollectionExtensions) — they are
      // required-but-nullable ctor params, so the container refuses type-activation without them.
      services.AddSingleton<ILifecycleMessageDeserializer>(new DiNoOpLifecycleDeserializer());
      services.AddSingleton(new WhizbangMetrics());
      services.AddSingleton<TransportMetrics>();
      services.AddHostedService<TransportConsumerWorker>();

      await using var sp = services.BuildServiceProvider();
      var worker = sp.GetServices<IHostedService>().OfType<TransportConsumerWorker>().Single();

      using var cts = new CancellationTokenSource();
      await worker.StartAsync(cts.Token);
      await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));
      try {
        var payloadType = typeof(DiContractsCollectiveEvent);
        var envelope = new MessageEnvelope<JsonElement> {
          MessageId = new MessageId(TrackedGuid.NewMedo()),
          Payload = JsonDocument.Parse("{\"x\":1}").RootElement,
          Hops = [
            new MessageHop {
              Type = HopType.Current,
              Timestamp = DateTimeOffset.UtcNow,
              ServiceInstance = ServiceInstanceInfo.Unknown
            }
          ],
          DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
        };
        var envelopeType = $"Whizbang.Core.Messaging.MessageEnvelope`1[[{payloadType.AssemblyQualifiedName}]], Whizbang.Core";
        await transport.DeliverBatchAsync([new TransportMessage(envelope, envelopeType)]);

        await Assert.That(coordinator.StoredMessages.Count).IsEqualTo(1);
        await Assert.That(coordinator.StoredMessages[0].Flags & EventFlags.Collective).IsEqualTo(EventFlags.Collective)
          .Because("The container-wired worker must derive Collective from the UNION catalog for a " +
                   "contracts-assembly event received as JsonElement — this locks the full production " +
                   "DI chain, not just the hand-constructed worker.");
      } finally {
        await worker.StopAsync(CancellationToken.None);
      }
    } finally {
      ServiceRegistrationCallbacks.RestoreMessageTypeCatalogRegistrations(saved);
    }
  }

  // ============================================================
  // Test doubles
  // ============================================================

  private sealed class DiNoOpLifecycleDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => jsonBytes;
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => jsonElement;
  }

  private sealed class DiFlagTransport : ITransport {
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
      return Task.FromResult<ISubscription>(new DiFlagSubscription());
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

  private sealed class DiFlagSubscription : ISubscription {
    public bool IsActive => true;

    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() {
      // Nothing to release — test double.
    }
  }
}
