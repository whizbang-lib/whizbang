// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branch coverage for <see cref="IntegrityCheckpointWorker"/>: the options guard, a host with no
/// service-instance provider, the checkpoint metric, owned domains handed to the outbox routing
/// strategy, and the topic routing strategy layered over the topic registry.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityCheckpointWorker.cs</code-under-test>
public class IntegrityCheckpointWorkerBranchCoverageTests {

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();

    var ex = await Assert.That(() => new IntegrityCheckpointWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      null!,
      NullLogger<IntegrityCheckpointWorker>.Instance)).Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();

    // An IOptions wrapper with no value must fail at construction, not on the first cycle.
    var ex = await Assert.That(() => new IntegrityCheckpointWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      new NullValueOptions(),
      NullLogger<IntegrityCheckpointWorker>.Instance)).Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task RunCheckpointOnce_NoServiceInstanceProvider_PublishesWithEmptyOriginNameAsync() {
    var coordinator = new CheckpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 3, ToCommitSequence = 4 }
    };
    var dispatcher = new CaptureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, _ => { });

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    var checkpoint = (IntegrityCheckpoint)dispatcher.Published.Single();
    await Assert.That(checkpoint.OriginServiceName).IsEqualTo(string.Empty)
      .Because("a host without a service-instance provider still checkpoints, with an empty name rather than a null");
    await Assert.That(checkpoint.OriginServiceId).IsEqualTo(coordinator.LocalServiceId);
  }

  [Test]
  public async Task RunCheckpointOnce_ProviderReportsNullServiceName_PublishesWithEmptyOriginNameAsync() {
    var coordinator = new CheckpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 3, ToCommitSequence = 4 }
    };
    var dispatcher = new CaptureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher,
      s => s.AddSingleton<IServiceInstanceProvider>(new InstanceProvider(null)));

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    var checkpoint = (IntegrityCheckpoint)dispatcher.Published.Single();
    await Assert.That(checkpoint.OriginServiceName).IsEqualTo(string.Empty);
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task RunCheckpointOnce_WithStreamIntegrityMetrics_CountsThePublishedCheckpointAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var coordinator = new CheckpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 1, ToCommitSequence = 2 }
    };
    var dispatcher = new CaptureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, s => {
      s.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("origin-svc"));
      s.AddSingleton(metrics);
    });

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(dispatcher.Published).Count().IsEqualTo(1);
    var published = helper.GetByName("whizbang.stream_integrity.checkpoints_published")
      .First(m => m.Tags.Count == 0).Value;
    await Assert.That(published).IsEqualTo(1)
      .Because("each published checkpoint is counted exactly once when the metrics are registered");
  }

  [Test]
  public async Task RunCheckpointOnce_RoutingOptionsRegistered_PassesOwnedDomainsCaseInsensitivelyToTheStrategyAsync() {
    var ordersType = typeof(CheckpointTopicProbes.Orders.OrdersProbeEvent);
    var coordinator = new CheckpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 1, ToCommitSequence = 2 },
      OwnAuditedEventTypes = [TypeNameFormatter.Format(ordersType)],
    };
    var dispatcher = new CaptureDispatcher();
    var transport = new CaptureTransport();
    var strategy = new CapturingOutboxStrategy();
    var routingOptions = new RoutingOptions().OwnDomains("app.orders");
    var worker = _buildWorker(coordinator, dispatcher, s => {
      _addTransportInfrastructure(s, transport, new Catalog(ordersType));
      s.AddSingleton<IOutboxRoutingStrategy>(strategy);
      s.AddSingleton<IOptions<RoutingOptions>>(Options.Create(routingOptions));
    });

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(strategy.OwnedDomainsSeen).IsNotNull();
    await Assert.That(strategy.OwnedDomainsSeen!.Contains("APP.ORDERS")).IsTrue()
      .Because("the configured owned domains reach the routing strategy, matched case-insensitively");
    await Assert.That(transport.Published.Select(p => p.Destination.Address)).IsEquivalentTo(["captured.topic"]);
    await Assert.That(dispatcher.Published).IsEmpty();
  }

  [Test]
  public async Task RunCheckpointOnce_TopicRoutingStrategyRegistered_RidesTheResolvedTopicAsync() {
    var ordersType = typeof(CheckpointTopicProbes.Orders.OrdersProbeEvent);
    var coordinator = new CheckpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 1, ToCommitSequence = 2 },
      OwnAuditedEventTypes = [TypeNameFormatter.Format(ordersType)],
    };
    var dispatcher = new CaptureDispatcher();
    var transport = new CaptureTransport();
    var worker = _buildWorker(coordinator, dispatcher, s => {
      _addTransportInfrastructure(s, transport, new Catalog(ordersType));
      s.AddSingleton<ITopicRegistry>(new TopicRegistry((ordersType, "app.orders")));
      s.AddSingleton<ITopicRoutingStrategy>(new SuffixTopicStrategy(".region-a"));
    });

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published.Select(p => p.Destination.Address)).IsEquivalentTo(["app.orders.region-a"])
      .Because("the topic routing strategy's resolved topic, not the registry's base topic, is what consumers subscribe to");
    await Assert.That(dispatcher.Published).IsEmpty();
  }

  [Test]
  public async Task RunCheckpointOnce_TopicRoutingStrategyReturnsNull_FallsBackToTheBaseTopicAsync() {
    var ordersType = typeof(CheckpointTopicProbes.Orders.OrdersProbeEvent);
    var coordinator = new CheckpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 1, ToCommitSequence = 2 },
      OwnAuditedEventTypes = [TypeNameFormatter.Format(ordersType)],
    };
    var dispatcher = new CaptureDispatcher();
    var transport = new CaptureTransport();
    var worker = _buildWorker(coordinator, dispatcher, s => {
      _addTransportInfrastructure(s, transport, new Catalog(ordersType));
      s.AddSingleton<ITopicRegistry>(new TopicRegistry((ordersType, "app.orders")));
      s.AddSingleton<ITopicRoutingStrategy>(new NullTopicStrategy());
    });

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published.Select(p => p.Destination.Address)).IsEquivalentTo(["app.orders"])
      .Because("a strategy that resolves nothing must not lose the topic; the registry's base topic is used");
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static IntegrityCheckpointWorker _buildWorker(
      CheckpointCoordinator coordinator, CaptureDispatcher dispatcher, Action<IServiceCollection> configure) {
    var services = new ServiceCollection();
    services.AddSingleton<ICheckpointMint>(new CheckpointMint(Options.Create(new ControlClassOptions())));
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IDispatcher>(dispatcher);
    configure(services);
    var sp = services.BuildServiceProvider();
    return new IntegrityCheckpointWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(new StreamIntegrityOptions()),
      NullLogger<IntegrityCheckpointWorker>.Instance);
  }

  private static void _addTransportInfrastructure(IServiceCollection services, CaptureTransport transport, IMessageTypeCatalog catalog) {
    services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("origin-svc"));
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton(catalog);
  }

  private sealed class NullValueOptions : IOptions<StreamIntegrityOptions> {
    public StreamIntegrityOptions Value => null!;
  }

  private sealed class CapturingOutboxStrategy : IOutboxRoutingStrategy {
    public IReadOnlySet<string>? OwnedDomainsSeen { get; private set; }

    public TransportDestination GetDestination(Type messageType, IReadOnlySet<string> ownedDomains, MessageKind kind) {
      OwnedDomainsSeen = ownedDomains;
      return new TransportDestination("captured.topic");
    }
  }

  private sealed class SuffixTopicStrategy(string suffix) : ITopicRoutingStrategy {
    public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) =>
      baseTopic + suffix;
  }

  private sealed class NullTopicStrategy : ITopicRoutingStrategy {
    public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) =>
      null!;
  }

  private sealed class CheckpointCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public IntegrityCheckpointWindow? Window { get; init; }
    public Guid LocalServiceId { get; } = TrackedGuid.New().Value;
    public List<string> OwnAuditedEventTypes { get; init; } = [];

    public Task<IReadOnlyList<string>> GetOwnAuditedEventTypesAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<string>>([.. OwnAuditedEventTypes]);

    public Task<IntegrityCheckpointWindow?> AdvanceIntegrityCheckpointAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(Window);

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(LocalServiceId);
  }

  private sealed class CaptureDispatcher : FakeDispatcher, IDispatcher {
    public List<object> Published { get; } = [];

    public new Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      Published.Add(eventData!);
      return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
    }
  }

  private sealed class InstanceProvider(string? serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New().Value;
    public string ServiceName => serviceName!;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class TopicRegistry(params (Type Type, string Topic)[] map) : ITopicRegistry {
    public string? GetBaseTopic(Type messageType) =>
      map.Where(m => m.Type == messageType).Select(m => m.Topic).FirstOrDefault();
  }

  private sealed class Catalog(params Type[] eventTypes) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() =>
      [.. eventTypes.Select(t => new MessageTypeCatalogEntry(t, TypeNameFormatter.Format(t), "event", null))];
  }

  private sealed class CaptureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) {
        Published.Add((envelope, destination, envelopeType));
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }
}
