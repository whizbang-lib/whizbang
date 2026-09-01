using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Issue #514: TransportDeadLetterDrainWorker resolves ITransportDeadLetterDrainer from DI —
/// and nothing ever registered one, so every drain pass iterated an empty list and broker
/// dead-letter queues grew unboundedly (observed: ~13k dead-lettered events per subscription
/// after a broker throttling storm, stranded for days). These tests pin the wiring: the ASB
/// hosting registration must contribute a drainer, and the fleet drainer must fan a drain
/// pass out across the transport's active subscriptions.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusFleetDeadLetterDrainer.cs</code-under-test>
public class AsbDeadLetterDrainerWiringTests {

  private sealed class _recordingDrainer((string TopicName, string SubscriptionName) key) : ITransportDeadLetterDrainer {
    public int Invocations;
    public int LastBudget;
    public int ReturnPerDrain { get; init; } = 1;
    public string TransportName => $"asb:{key.TopicName}/{key.SubscriptionName}";
    public Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default) {
      Interlocked.Increment(ref Invocations);
      LastBudget = maxCount;
      return Task.FromResult(Math.Min(ReturnPerDrain, maxCount));
    }
  }

  [Test]
  public async Task AddAzureServiceBusTransport_RegistersADeadLetterDrainerAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    // Pre-registering the client keeps the test hermetic — the extension skips its own
    // client factory when one exists, and ServiceBusClient construction never dials.
    services.AddSingleton(new ServiceBusClient(
      "Endpoint=sb://unit-test.example/;SharedAccessKeyName=unit;SharedAccessKey=dW5pdC10ZXN0LWtleQ=="));
    services.AddAzureServiceBusTransport(
      "Endpoint=sb://unit-test.example/;SharedAccessKeyName=unit;SharedAccessKey=dW5pdC10ZXN0LWtleQ==");

    await using var provider = services.BuildServiceProvider();
    var drainers = provider.GetServices<ITransportDeadLetterDrainer>().ToList();

    await Assert.That(drainers.Count).IsGreaterThanOrEqualTo(1)
      .Because("issue #514: the drain worker resolves ITransportDeadLetterDrainer from DI, and "
             + "with zero registrations every drain pass is a silent no-op — broker DLQs are "
             + "never recovered");
  }

  [Test]
  public async Task FleetDrainer_DrainsEveryActiveSubscription_AndSumsCountsAsync() {
    var made = new Dictionary<(string, string), _recordingDrainer>();
    var subs = new List<(string TopicName, string SubscriptionName)> {
      ("orders.contracts.job", "svc-orders.contracts.job"),
      ("inbox", "svc-inbox"),
    };
    var fleet = new AzureServiceBusFleetDeadLetterDrainer(
      () => subs,
      key => { var d = new _recordingDrainer(key) { ReturnPerDrain = 3 }; made[key] = d; return d; });

    var drained = await fleet.DrainDeadLetterQueueAsync(500);

    await Assert.That(made.Count).IsEqualTo(2)
      .Because("every active subscription's DLQ gets a drainer");
    await Assert.That(made.Values.Sum(d => d.Invocations)).IsEqualTo(2);
    await Assert.That(drained).IsEqualTo(6)
      .Because("the fleet reports the total drained across subscriptions");
  }

  [Test]
  public async Task FleetDrainer_BudgetIsATotalCap_NotPerSubscriptionAsync() {
    var subs = new List<(string TopicName, string SubscriptionName)> {
      ("t1", "s1"), ("t2", "s2"), ("t3", "s3"),
    };
    var made = new List<_recordingDrainer>();
    var fleet = new AzureServiceBusFleetDeadLetterDrainer(
      () => subs,
      key => { var d = new _recordingDrainer(key) { ReturnPerDrain = 4 }; made.Add(d); return d; });

    var drained = await fleet.DrainDeadLetterQueueAsync(10);

    await Assert.That(drained).IsLessThanOrEqualTo(10)
      .Because("MaxPerTick is the worker's pacing contract — a fleet that multiplies it by the "
             + "subscription count reintroduces exactly the broker ops-rate burst the pacing exists "
             + "to prevent");
    var totalBudgetSeen = made.Sum(d => d.LastBudget);
    await Assert.That(totalBudgetSeen).IsLessThanOrEqualTo(10 * made.Count);
  }

  [Test]
  public async Task FleetDrainer_NewSubscriptionAppearingLater_GetsDrainedAsync() {
    var subs = new List<(string TopicName, string SubscriptionName)> { ("t1", "s1") };
    var made = new Dictionary<(string, string), _recordingDrainer>();
    var fleet = new AzureServiceBusFleetDeadLetterDrainer(
      () => subs,
      key => { var d = new _recordingDrainer(key); made[key] = d; return d; });

    _ = await fleet.DrainDeadLetterQueueAsync(100);
    subs.Add(("t2", "s2"));
    _ = await fleet.DrainDeadLetterQueueAsync(100);

    await Assert.That(made.ContainsKey(("t2", "s2"))).IsTrue()
      .Because("the active-subscription snapshot is re-evaluated per pass — a subscription "
             + "established after startup still gets its DLQ drained");
    await Assert.That(made[("t1", "s1")].Invocations).IsEqualTo(2)
      .Because("per-subscription drainers are cached, not re-created each pass");
  }
}
