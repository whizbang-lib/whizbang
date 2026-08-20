using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tags;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The Service Bus half of the backlog-age duty (topology arc phase 10). Depth comes from the
/// management plane; AGE comes from a single head peek, and age is the field that matters — the
/// motivating incident's 16,642-message subscription was HOSTAGE, not poison, and drained to zero
/// untouched once the churn stopped. A depth alarm cannot tell that from a stuck consumer.
/// </summary>
[Timeout(10_000)]
[Category("Transports")]
public class AsbBacklogPeekTests {
  [Test]
  public async Task PeekAsync_ReportsDepthAndAgePerTrackedSubscriptionAsync() {
    var admin = new RecordingProvisioningAdminClient { ActiveMessageCountResult = 42 };
    var transport = _transport(admin);
    transport.LivenessWatchdog!.Track("orders-topic", "orders-svc-sub");
    transport.OldestEnqueuedTimeProbe = (_, _, _) =>
      Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow - TimeSpan.FromHours(2));

    var samples = await new AsbBacklogPeek(transport).PeekAsync(CancellationToken.None);

    var sample = samples.Single();
    await Assert.That(sample.Entity).IsEqualTo("orders-topic/orders-svc-sub");
    await Assert.That(sample.Depth).IsEqualTo(42L);
    await Assert.That(sample.OldestAge!.Value).IsGreaterThan(TimeSpan.FromMinutes(100));
    await Assert.That(sample.Transport).IsEqualTo("asb");
  }

  [Test]
  public async Task PeekAsync_NothingSubscribed_ReportsNoSamplesAsync() {
    var samples = await new AsbBacklogPeek(_transport(new RecordingProvisioningAdminClient()))
      .PeekAsync(CancellationToken.None);

    await Assert.That(samples).IsEmpty();
  }

  [Test]
  public async Task PeekAsync_EmptyEntity_ReportsNoAgeRatherThanZeroAsync() {
    // An empty entity has no head message. Reporting "age 0" would read as a perfectly fresh
    // backlog; reporting NO age is the truth, and the duty treats it as such.
    var admin = new RecordingProvisioningAdminClient { ActiveMessageCountResult = 0 };
    var transport = _transport(admin);
    transport.LivenessWatchdog!.Track("orders-topic", "orders-svc-sub");
    transport.OldestEnqueuedTimeProbe = (_, _, _) => Task.FromResult<DateTimeOffset?>(null);

    var sample = (await new AsbBacklogPeek(transport).PeekAsync(CancellationToken.None)).Single();

    await Assert.That(sample.Depth).IsEqualTo(0L);
    await Assert.That(sample.OldestAge).IsNull();
  }

  [Test]
  public async Task PeekAsync_HeadPeekFails_StillReportsDepthAsync() {
    // Degrade the sample, never the pass: depth without age is still worth having, and the duty
    // surfaces the missing age as a capability gap.
    var admin = new RecordingProvisioningAdminClient { ActiveMessageCountResult = 7 };
    var transport = _transport(admin);
    transport.LivenessWatchdog!.Track("orders-topic", "orders-svc-sub");
    transport.OldestEnqueuedTimeProbe = (_, _, _) =>
      Task.FromException<DateTimeOffset?>(new InvalidOperationException("peek refused"));

    var sample = (await new AsbBacklogPeek(transport).PeekAsync(CancellationToken.None)).Single();

    await Assert.That(sample.Depth).IsEqualTo(7L);
    await Assert.That(sample.OldestAge).IsNull();
  }

  [Test]
  public async Task PeekAsync_DepthProbeFails_SkipsThatEntityWithoutFaultingAsync() {
    var admin = new RecordingProvisioningAdminClient {
      ActiveMessageCountException = new InvalidOperationException("management plane down"),
    };
    var transport = _transport(admin);
    transport.LivenessWatchdog!.Track("orders-topic", "orders-svc-sub");

    var samples = await new AsbBacklogPeek(transport).PeekAsync(CancellationToken.None);

    await Assert.That(samples).IsEmpty();
  }

  [Test]
  public async Task PeekAsync_TagsTheTrafficClassRoutedToTheNamespaceAsync() {
    // The comparative dimension: "which class is backed up" is the question, and on a
    // single-namespace host every entity is honestly unclassified rather than mislabelled.
    var admin = new RecordingProvisioningAdminClient { ActiveMessageCountResult = 1 };
    var transport = _transport(admin);
    transport.LivenessWatchdog!.Track("orders-topic", "orders-svc-sub");
    transport.OldestEnqueuedTimeProbe = (_, _, _) => Task.FromResult<DateTimeOffset?>(null);
    var tagOptions = new TagOptions();
    tagOptions.RouteNamespace(SystemTags.CONTROL, "control");

    var sample = (await new AsbBacklogPeek(transport, tagOptions).PeekAsync(CancellationToken.None)).Single();

    await Assert.That(sample.TransportNamespace).IsEqualTo(TransportNamespaces.DefaultKey);
    await Assert.That(sample.TrafficClass).IsEqualTo(TrafficClasses.DOMAIN);
  }

  private static AzureServiceBusTransport _transport(RecordingProvisioningAdminClient admin) =>
    new(new RaisableServiceBusClient(),
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { AutoProvisionInfrastructure = false, EnableSessions = false },
      NullLogger<AzureServiceBusTransport>.Instance,
      adminClient: admin);
}
