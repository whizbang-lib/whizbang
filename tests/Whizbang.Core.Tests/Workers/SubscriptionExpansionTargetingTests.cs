// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
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
/// known from the ownership map, with a request address, goes to that owner as a directed request on its request
/// topic. Only the types with no usable owner fall back to the broadcast every holder used to answer.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SubscriptionExpansionWorker.cs</code-under-test>
public class SubscriptionExpansionTargetingTests {
  private static readonly Guid _ordersOwner = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
  private static readonly Guid _billingOwner = Guid.Parse("0b000000-0000-0000-0000-00000000000b");
  private static readonly string _placedWire = TypeNameFormatter.Format(typeof(PlacedEvent));
  private static readonly string _billedWire = TypeNameFormatter.Format(typeof(BilledEvent));

  [Test]
  public async Task Backfill_TypesWithAKnownOwner_AreAskedOfThatOwnerOnlyAsync() {
    var coordinator = new ExpandingCoordinator {
      Claims = [
        _claim("Whizbang.Core.Tests.Workers.BackfillProbes.Orders", _ordersOwner, "orders-svc", "orders.requests"),
        _claim("whizbang.core.tests.workers.backfillprobes.billing", _billingOwner, "billing-svc", "billing.requests"),
      ],
    };
    var transport = new CaptureTransport();

    await _buildWorker(coordinator, transport).RunOnceAsync(CancellationToken.None);

    var sent = transport.Published.Select(p => (p.Envelope.Target, p.Destination.Address, Command: _command(p.Envelope)))
      .OrderBy(p => p.Target).ToList();
    await Assert.That(sent.Count).IsEqualTo(2)
      .Because("each owner gets one directed request; no broadcast is needed when every type has an owner");
    await Assert.That(sent[0].Target).IsEqualTo("billing-svc");
    await Assert.That(sent[0].Address).IsEqualTo("billing.requests");
    await Assert.That(sent[0].Command.EventTypes).IsEquivalentTo([_billedWire]);
    await Assert.That(sent[1].Target).IsEqualTo("orders-svc");
    await Assert.That(sent[1].Address).IsEqualTo("orders.requests")
      .Because("the owner's own request address, the one its checkpoint announced");
    await Assert.That(sent[1].Command.EventTypes).IsEquivalentTo([_placedWire]);
    await Assert.That(sent.All(s => s.Command.StateOnly && s.Command.Topic == "inbox" && s.Command.RequesterService == "expanding-svc"))
      .IsTrue().Because("the answer still comes back state-only to this service's reply topic");
    await Assert.That(coordinator.Registry.Where(kv => kv.Key != "Contracts.PriorType").All(kv => kv.Value == ConsumedTypeBackfillStatus.Requested))
      .IsTrue().Because("both expansions were asked of their owners, so both are marked requested");
  }

  [Test]
  public async Task Backfill_TypesWithNoUsableOwner_FallBackToOneBroadcastAsync() {
    var coordinator = new ExpandingCoordinator {
      Claims = [
        // Known owner, but it never announced a request address (an older origin): nowhere to direct the request.
        _claim("whizbang.core.tests.workers.backfillprobes.billing", _billingOwner, "billing-svc", requestTopic: null),
      ],
    };
    var transport = new CaptureTransport();

    await _buildWorker(coordinator, transport).RunOnceAsync(CancellationToken.None);

    var (envelope, destination, _) = transport.Published.Single();
    await Assert.That(envelope.Target).IsNull();
    await Assert.That(destination.Address).IsEqualTo("inbox");
    await Assert.That(_command(envelope).EventTypes).IsEquivalentTo([_placedWire, _billedWire])
      .Because("with no owner to ask, the legacy broadcast stands, and the answering side now ships only what it authored");
  }

  [Test]
  public async Task Backfill_MixedOwnership_DirectsTheOwnedTypesAndBroadcastsTheRestAsync() {
    var coordinator = new ExpandingCoordinator {
      Claims = [_claim("whizbang.core.tests.workers.backfillprobes.orders", _ordersOwner, "orders-svc", "orders.requests")],
    };
    var transport = new CaptureTransport();

    await _buildWorker(coordinator, transport).RunOnceAsync(CancellationToken.None);

    var (directed, _, _) = transport.Published.Single(p => p.Envelope.Target == "orders-svc");
    var (broadcast, _, _) = transport.Published.Single(p => p.Envelope.Target is null);
    await Assert.That(_command(directed).EventTypes).IsEquivalentTo([_placedWire]);
    await Assert.That(_command(broadcast).EventTypes).IsEquivalentTo([_billedWire]);
  }

  [Test]
  public async Task Backfill_AnOwnerThatIsThisService_IsNotAskedAsync() {
    var self = Guid.Parse("0c000000-0000-0000-0000-00000000000c");
    var coordinator = new ExpandingCoordinator {
      LocalServiceId = self,
      Claims = [_claim("whizbang.core.tests.workers.backfillprobes.orders", self, "expanding-svc", "inbox")],
    };
    var transport = new CaptureTransport();

    await _buildWorker(coordinator, transport).RunOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published.All(p => p.Envelope.Target is null)).IsTrue()
      .Because("a service never directs a request at itself; its own request would loop back to it");
    await Assert.That(_command(transport.Published.Single().Envelope).EventTypes).IsEquivalentTo([_placedWire, _billedWire]);
  }

  [Test]
  public async Task Backfill_TypesOfOneOwner_GoOutAsOneRequestAsync() {
    var coordinator = new ExpandingCoordinator {
      Claims = [_claim("whizbang.core.tests.workers.backfillprobes", _ordersOwner, "probes-svc", "probes.requests")],
    };
    var transport = new CaptureTransport();

    await _buildWorker(coordinator, transport).RunOnceAsync(CancellationToken.None);

    var (envelope, destination, _) = transport.Published.Single();
    await Assert.That(envelope.Target).IsEqualTo("probes-svc");
    await Assert.That(destination.Address).IsEqualTo("probes.requests");
    await Assert.That(_command(envelope).EventTypes).IsEquivalentTo([_placedWire, _billedWire])
      .Because("one owner's types share one request");
  }

  private static IntegrityDomainClaim _claim(string domain, Guid owner, string name, string? requestTopic) =>
    new() { Domain = domain, OwnerServiceId = owner, OwnerServiceName = name, RequestTopic = requestTopic };

  private static RequestRedeliveryCommand _command(IMessageEnvelope envelope) =>
    (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(RequestRedeliveryCommand)))!;

  private static SubscriptionExpansionWorker _buildWorker(ExpandingCoordinator coordinator, CaptureTransport transport) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IEventTypeProvider>(new TypeProvider());
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

  private sealed class TypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(PlacedEvent), typeof(BilledEvent)];
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
