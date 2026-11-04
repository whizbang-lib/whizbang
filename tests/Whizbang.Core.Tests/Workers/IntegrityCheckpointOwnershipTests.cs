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
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The checkpoint follows registered ownership (#2008, #2031): it announces the origin's owned domains, counts only
/// the event types the origin owns, and rides only the topics of owned types, so a consumer subscribed to other
/// services' topics no longer receives a copy per topic. A service with no owned domains checkpoints as before.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityCheckpointWorker.cs</code-under-test>
public class IntegrityCheckpointOwnershipTests {
  private const string ORDERS_NAMESPACE = "Whizbang.Core.Tests.Workers.CheckpointTopicProbes.Orders";
  private static readonly Type _ordersType = typeof(CheckpointTopicProbes.Orders.OrdersProbeEvent);
  private static readonly Type _usersType = typeof(CheckpointTopicProbes.Users.UsersProbeEvent);

  [Test]
  public async Task RunCheckpointOnce_OwnedDomainsRegistered_AnnouncesThemOnTheCheckpointAsync() {
    var coordinator = new CheckpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 1, ToCommitSequence = 2 }
    };
    var dispatcher = new CaptureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, s =>
      s.AddSingleton<IOptions<RoutingOptions>>(Options.Create(new RoutingOptions().OwnDomains(ORDERS_NAMESPACE, "App.Contracts.Shipping."))));

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    var checkpoint = (IntegrityCheckpoint)dispatcher.Published.Single();
    await Assert.That(checkpoint.OwnedDomains).IsEquivalentTo([ORDERS_NAMESPACE.ToLowerInvariant(), "app.contracts.shipping"])
      .Because("consumers learn each domain's author from the checkpoint, as routing stores them (lower case) without the trailing dot");
  }

  [Test]
  public async Task RunCheckpointOnce_NoOwnedDomains_AnnouncesNoneAndKeepsEveryBucketAsync() {
    var coordinator = new CheckpointCoordinator {
      Window = new IntegrityCheckpointWindow {
        FromCommitSequence = 1,
        ToCommitSequence = 2,
        Buckets = [
          new CheckpointBucket { EventType = TypeNameFormatter.Format(_ordersType), Count = 2 },
          new CheckpointBucket { EventType = "Other.Contracts.Shipped, Other", Count = 1 },
        ]
      }
    };
    var dispatcher = new CaptureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, _ => { });

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    var checkpoint = (IntegrityCheckpoint)dispatcher.Published.Single();
    await Assert.That(checkpoint.OwnedDomains).IsEmpty();
    await Assert.That(checkpoint.Buckets.Count).IsEqualTo(2)
      .Because("with no owned domains registered every event without an origin is still this service's own");
  }

  [Test]
  public async Task RunCheckpointOnce_OwnedDomainsRegistered_CountsOnlyOwnedTypesAsync() {
    var coordinator = new CheckpointCoordinator {
      Window = new IntegrityCheckpointWindow {
        FromCommitSequence = 1,
        ToCommitSequence = 2,
        Buckets = [
          new CheckpointBucket { EventType = TypeNameFormatter.Format(_ordersType), Count = 2 },
          new CheckpointBucket { EventType = TypeNameFormatter.Format(_usersType), Count = 5 },
        ]
      }
    };
    var dispatcher = new CaptureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, s =>
      s.AddSingleton<IOptions<RoutingOptions>>(Options.Create(new RoutingOptions().OwnDomains(ORDERS_NAMESPACE))));

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    var checkpoint = (IntegrityCheckpoint)dispatcher.Published.Single();
    await Assert.That(checkpoint.Buckets.Select(b => b.EventType)).IsEquivalentTo([TypeNameFormatter.Format(_ordersType)])
      .Because("events of a type this service does not own were authored elsewhere; counting them invites consumers "
             + "to ask this service for history it never wrote");
  }

  [Test]
  public async Task RunCheckpointOnce_OwnedDomainsRegistered_RidesOnlyTheOwnedTypesTopicsAsync() {
    var coordinator = new CheckpointCoordinator {
      Window = new IntegrityCheckpointWindow {
        FromCommitSequence = 1,
        ToCommitSequence = 2,
        Buckets = [new CheckpointBucket { EventType = TypeNameFormatter.Format(_usersType), Count = 5 }],
      },
      OwnAuditedEventTypes = [TypeNameFormatter.Format(_usersType)],
    };
    var dispatcher = new CaptureDispatcher();
    var transport = new CaptureTransport();
    var worker = _buildWorker(coordinator, dispatcher, s => {
      _addTransportInfrastructure(s, transport, new Catalog(_ordersType, _usersType));
      s.AddSingleton<IOutboxRoutingStrategy>(new DomainTopicOutboxStrategy());
      s.AddSingleton<IOptions<RoutingOptions>>(Options.Create(new RoutingOptions().OwnDomains(ORDERS_NAMESPACE)));
    });

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published.Select(p => p.Destination.Address))
      .IsEquivalentTo(["whizbang.core.tests.workers.checkpointtopicprobes.orders"])
      .Because("one copy per owned topic: other services' topics (from the window or the history this store holds) "
             + "would each deliver another copy to every consumer subscribed to them (#2031)");
    await Assert.That(coordinator.OwnAuditedTypesReads).IsEqualTo(0)
      .Because("the owned types come from the catalog, not from a scan of the store's digest lane");
    await Assert.That(dispatcher.Published).IsEmpty();
  }

  [Test]
  public async Task RunCheckpointOnce_OwnedDomainsWithNoCatalogType_FallsBackToTheDispatcherAsync() {
    var coordinator = new CheckpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 1, ToCommitSequence = 2 },
    };
    var dispatcher = new CaptureDispatcher();
    var transport = new CaptureTransport();
    var worker = _buildWorker(coordinator, dispatcher, s => {
      _addTransportInfrastructure(s, transport, new Catalog(_usersType));
      s.AddSingleton<IOutboxRoutingStrategy>(new DomainTopicOutboxStrategy());
      s.AddSingleton<IOptions<RoutingOptions>>(Options.Create(new RoutingOptions().OwnDomains(ORDERS_NAMESPACE)));
    });

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty();
    await Assert.That(dispatcher.Published).Count().IsEqualTo(1)
      .Because("the liveness beat is never dropped: with no owned topic to ride it goes through the dispatcher");
  }

  [Test]
  public async Task RunCheckpointOnce_NoOwnedDomains_AHistoricalTypeOutsideTheCatalog_RidesNoTopicAsync() {
    var coordinator = new CheckpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 1, ToCommitSequence = 2 },
      OwnAuditedEventTypes = ["Retired.Contracts.Gone, Retired"],
    };
    var dispatcher = new CaptureDispatcher();
    var transport = new CaptureTransport();
    var worker = _buildWorker(coordinator, dispatcher, s => {
      _addTransportInfrastructure(s, transport, new Catalog(_ordersType));
      s.AddSingleton<IOutboxRoutingStrategy>(new DomainTopicOutboxStrategy());
    });

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty().Because("a stored type no catalog entry resolves has no topic to ride");
    await Assert.That(dispatcher.Published).Count().IsEqualTo(1);
  }

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
      Options.Create(new StreamIntegrityOptions()),
      NullLogger<IntegrityCheckpointWorker>.Instance);
  }

  private static void _addTransportInfrastructure(IServiceCollection services, CaptureTransport transport, IMessageTypeCatalog catalog) {
    services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("origin-svc"));
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton(catalog);
  }

  private sealed class CheckpointCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public IntegrityCheckpointWindow? Window { get; init; }
    public Guid LocalServiceId { get; } = TrackedGuid.New().Value;
    public List<string> OwnAuditedEventTypes { get; init; } = [];

    public int OwnAuditedTypesReads { get; private set; }

    public Task<IReadOnlyList<string>> GetOwnAuditedEventTypesAsync(CancellationToken cancellationToken = default) {
      OwnAuditedTypesReads++;
      return Task.FromResult<IReadOnlyList<string>>([.. OwnAuditedEventTypes]);
    }

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
