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
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

using BilledEvent = Whizbang.Core.Tests.Workers.BackfillProbes.Billing.BilledEvent;
using PlacedEvent = Whizbang.Core.Tests.Workers.BackfillProbes.Orders.PlacedEvent;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The subscription-growth backfill asks each known owner for its own events (#2032): a type whose domain owner is
/// known from the ownership map, with a request address, is queued for that owner. Only the types with no usable owner
/// fall back to a broadcast. Nothing is sent at once (#2092): the paced backfill walks each queued type in windows.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SubscriptionExpansionWorker.cs</code-under-test>
public class SubscriptionExpansionTargetingTests {
  private static readonly Guid _ordersOwner = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
  private static readonly Guid _billingOwner = Guid.Parse("0b000000-0000-0000-0000-00000000000b");
  private static readonly string _placedWire = TypeNameFormatter.Format(typeof(PlacedEvent));
  private static readonly string _billedWire = TypeNameFormatter.Format(typeof(BilledEvent));

  [Test]
  public async Task Backfill_TypesWithAKnownOwner_AreQueuedForThatOwner_AndNothingIsSentAtOnceAsync() {
    var coordinator = new ExpandingCoordinator {
      Claims = [
        _claim("Whizbang.Core.Tests.Workers.BackfillProbes.Orders", _ordersOwner, "orders-svc", "orders.requests"),
        _claim("whizbang.core.tests.workers.backfillprobes.billing", _billingOwner, "billing-svc", "billing.requests"),
      ],
    };
    var transport = new CaptureTransport();

    await _buildWorker(coordinator, transport).RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Seeds.OrderBy(s => s.OwnerServiceName, StringComparer.Ordinal)).IsEquivalentTo([
      new SubscriptionBackfillSeed(_billingOwner, "billing-svc", _billedWire),
      new SubscriptionBackfillSeed(_ordersOwner, "orders-svc", _placedWire),
    ]).Because("each type is queued for its owner, in the wire form the owner matches its store against");
    await Assert.That(transport.Published).IsEmpty()
      .Because("#2092: asking every owner for the full history of every type at once was the flood; the paced backfill sends windows");
    await Assert.That(coordinator.Registry.Where(kv => kv.Key != "Contracts.PriorType").All(kv => kv.Value == ConsumedTypeBackfillStatus.Requested))
      .IsTrue().Because("both expansions are queued, so both are marked requested");
  }

  [Test]
  public async Task Backfill_TypesWithNoUsableOwner_AreQueuedAsBroadcastsAsync() {
    var coordinator = new ExpandingCoordinator {
      Claims = [
        // Known owner, but it never announced a request address (an older origin): nowhere to direct the request.
        _claim("whizbang.core.tests.workers.backfillprobes.billing", _billingOwner, "billing-svc", requestTopic: null),
      ],
    };

    await _buildWorker(coordinator, new CaptureTransport()).RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Seeds).IsEquivalentTo([
      new SubscriptionBackfillSeed(Guid.Empty, string.Empty, _placedWire),
      new SubscriptionBackfillSeed(Guid.Empty, string.Empty, _billedWire),
    ]).Because("with no owner to ask, each window is broadcast, and the answering side ships only what it authored");
  }

  [Test]
  public async Task Backfill_MixedOwnership_QueuesTheOwnedTypeForItsOwnerAndTheRestAsBroadcastAsync() {
    var coordinator = new ExpandingCoordinator {
      Claims = [_claim("whizbang.core.tests.workers.backfillprobes.orders", _ordersOwner, "orders-svc", "orders.requests")],
    };

    await _buildWorker(coordinator, new CaptureTransport()).RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Seeds).IsEquivalentTo([
      new SubscriptionBackfillSeed(_ordersOwner, "orders-svc", _placedWire),
      new SubscriptionBackfillSeed(Guid.Empty, string.Empty, _billedWire),
    ]);
  }

  [Test]
  public async Task Backfill_AnOwnerThatIsThisService_IsNotAskedAsync() {
    var self = Guid.Parse("0c000000-0000-0000-0000-00000000000c");
    var coordinator = new ExpandingCoordinator {
      LocalServiceId = self,
      Claims = [_claim("whizbang.core.tests.workers.backfillprobes.orders", self, "expanding-svc", "inbox")],
    };

    await _buildWorker(coordinator, new CaptureTransport()).RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Seeds.All(s => s.OwnerServiceId == Guid.Empty)).IsTrue()
      .Because("a service never directs a request at itself; its own request would loop back to it");
  }

  [Test]
  public async Task Backfill_TypesOfOneOwner_AreQueuedOnePerTypeAsync() {
    var coordinator = new ExpandingCoordinator {
      Claims = [_claim("whizbang.core.tests.workers.backfillprobes", _ordersOwner, "probes-svc", "probes.requests")],
    };

    await _buildWorker(coordinator, new CaptureTransport()).RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Seeds).IsEquivalentTo([
      new SubscriptionBackfillSeed(_ordersOwner, "probes-svc", _placedWire),
      new SubscriptionBackfillSeed(_ordersOwner, "probes-svc", _billedWire),
    ]).Because("each type walks its own history in its own windows, with its own progress and its own finish");
  }

  /// <summary>
  /// The first pass after the upgrade to the consumer registry (#2064): the perspective's event was baselined before,
  /// and the event only a receptor consumes is new to the registry, so it is an expansion. Its history was never stored
  /// here, and its owner is known, so it is queued for that owner alone.
  /// </summary>
  [Test]
  public async Task Backfill_FirstPassAfterUpgrade_QueuesTheReceptorOnlyEventForItsOwnerAsync() {
    var coordinator = new ExpandingCoordinator {
      Claims = [_claim("whizbang.core.tests.workers.backfillprobes.billing", _billingOwner, "billing-svc", "billing.requests")],
    };
    coordinator.Registry[TypeNameFormatter.FormatClrTypeName(typeof(PlacedEvent))] = ConsumedTypeBackfillStatus.Baseline;

    await _buildWorker(coordinator, new CaptureTransport()).RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Seeds).IsEquivalentTo([new SubscriptionBackfillSeed(_billingOwner, "billing-svc", _billedWire)])
      .Because("only the newly consumed type is an expansion; the baselined perspective type is not asked again");
  }

  private static IntegrityDomainClaim _claim(string domain, Guid owner, string name, string? requestTopic) =>
    new() { Domain = domain, OwnerServiceId = owner, OwnerServiceName = name, RequestTopic = requestTopic };

  private static SubscriptionExpansionWorker _buildWorker(
      ExpandingCoordinator coordinator, CaptureTransport transport) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IConsumerRegistry>(new ConsumesBoth());
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("expanding-svc"));
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    return new SubscriptionExpansionWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped }),
      NullLogger<SubscriptionExpansionWorker>.Instance);
  }

  /// <summary>The consumer registry (#2058): this service consumes both probe events.</summary>
  private sealed class ConsumesBoth : IConsumerRegistry {
    public bool HasAnyConsumer(string messageType) => messageType == _placedWire || messageType == _billedWire;
    public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() =>
      [new ConsumedMessageInfo(_placedWire, IsEvent: true), new ConsumedMessageInfo(_billedWire, IsEvent: true)];
  }

  private sealed class InstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.Parse("0d000000-0000-0000-0000-00000000000d");
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  /// <summary>A registry from a prior boot (one baseline type), so both catalog types are expansions.</summary>
  private sealed class ExpandingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public Dictionary<string, ConsumedTypeBackfillStatus> Registry { get; } = new() {
      ["Contracts.PriorType"] = ConsumedTypeBackfillStatus.Baseline,
    };
    public IReadOnlyList<IntegrityDomainClaim> Claims { get; init; } = [];
    public Guid LocalServiceId { get; init; } = Guid.Parse("0e000000-0000-0000-0000-00000000000e");
    public List<SubscriptionBackfillSeed> Seeds { get; } = [];

    public Task EnqueueSubscriptionBackfillsAsync(IReadOnlyList<SubscriptionBackfillSeed> seeds, CancellationToken cancellationToken = default) {
      Seeds.AddRange(seeds);
      return Task.CompletedTask;
    }

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(LocalServiceId);

    public Task<IReadOnlyList<IntegrityDomainClaim>> GetIntegrityDomainClaimsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(Claims);

    public Task<IReadOnlyList<ConsumedTypeRegistration>> GetConsumedTypeRegistrationsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<ConsumedTypeRegistration>>(
        [.. Registry.Select(kv => new ConsumedTypeRegistration { EventType = kv.Key, Status = kv.Value })]);

    public Task RegisterConsumedTypesAsync(IReadOnlyList<string> eventTypes, bool asBaseline, CancellationToken cancellationToken = default) {
      foreach (var type in eventTypes) {
        Registry.TryAdd(type, asBaseline ? ConsumedTypeBackfillStatus.Baseline : ConsumedTypeBackfillStatus.Pending);
      }
      return Task.CompletedTask;
    }

    public Task MarkConsumedTypeBackfillRequestedAsync(IReadOnlyList<string> eventTypes, CancellationToken cancellationToken = default) {
      foreach (var type in eventTypes) {
        if (Registry.TryGetValue(type, out var status) && status == ConsumedTypeBackfillStatus.Pending) {
          Registry[type] = ConsumedTypeBackfillStatus.Requested;
        }
      }
      return Task.CompletedTask;
    }
  }

  private sealed class CaptureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      Published.Add((envelope, destination, envelopeType));
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }
}
