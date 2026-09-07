using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Tags;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The projection that reports each namespace's idle broker-operation rate by traffic class.
/// </summary>
/// <remarks>
/// Azure Service Bus bills per operation, so an idle consumer still costs money — one receive
/// poll per subscription per interval, multiplied by every namespace a service touches. This
/// source is what makes that visible before it appears on an invoice.
///
/// <para>
/// It has to walk a namespace-routing transport rather than just the default one, or a service
/// that publishes across several namespaces reports the cost of one of them. And it must
/// contribute nothing at all when the transport has no projection to offer, rather than reporting
/// a zero that reads as "idle and free".
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AsbTrafficClassOpsRateSource.cs</code-under-test>
public class AsbTrafficClassOpsRateSourceTests {

  /// <summary>A transport that is not an ASB one, so it offers no projection.</summary>
  private sealed class ForeignTransport : ITransport {
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public long? MaxMessageSizeBytes => null;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<BulkPublishItemResult>> PublishBatchAsync(
        IReadOnlyList<BulkPublishItem> items, TransportDestination destination,
        CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<BulkPublishItemResult>>([]);
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
    public Task<ISubscription> SubscribeAsync(
        Func<TransportMessage, CancellationToken, Task> handler, TransportDestination destination,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  [Test]
  public async Task TheSourceIdentifiesItselfAsTheServiceBusTransportAsync() {
    // The name is how the ops-rate surface attributes a cost line to a transport; a host with
    // both brokers wired would otherwise merge their rates.
    var source = new AsbTrafficClassOpsRateSource(new ForeignTransport());

    await Assert.That(source.TransportName).IsEqualTo("asb");
  }

  [Test]
  public async Task ATransportWithNoProjection_ContributesNothingAsync() {
    // Reporting a zero rate here would read as "idle and free" on the dashboard, which is the
    // opposite of "we have no measurement".
    var source = new AsbTrafficClassOpsRateSource(new ForeignTransport());

    var rates = source.Project();

    await Assert.That(rates).IsEmpty()
      .Because("an absent measurement must not present as a measured zero");
  }

  [Test]
  public async Task ARoutingTransport_IsWalkedPerNamespaceAsync() {
    // A service publishing across several namespaces pays per namespace. Projecting only the
    // default would report a fraction of the bill.
    var router = new NamespaceRoutingTransport(
      new ForeignTransport(),
      new Dictionary<string, ITransport>(StringComparer.Ordinal) {
        ["bulk"] = new ForeignTransport(),
        ["control"] = new ForeignTransport(),
      },
      activeConsumeNamespaceKeys: null);

    var source = new AsbTrafficClassOpsRateSource(router);
    var rates = source.Project();

    // None of these transports offers a projection, so the walk contributes nothing — but it
    // must have walked rather than thrown on a shape it did not expect.
    await Assert.That(rates).IsEmpty();
  }

  [Test]
  public async Task WithNoTagOptions_TheSourceStillProjectsAsync() {
    // Traffic-class bindings are optional; a host that never declared one still gets its rates,
    // classified as ordinary domain traffic.
    var source = new AsbTrafficClassOpsRateSource(new ForeignTransport(), tagOptions: null);

    await Assert.That(source.Project()).IsEmpty();
  }

  [Test]
  public async Task WithTagOptions_TheSourceStillProjectsAsync() {
    var tagOptions = new TagOptions();
    tagOptions.RouteNamespace("record-digest", "bulk");
    var source = new AsbTrafficClassOpsRateSource(new ForeignTransport(), tagOptions);

    await Assert.That(source.Project()).IsEmpty();
  }

  [Test]
  public async Task Constructor_RejectsANullTransportAsync() {
    await Assert.That(() => new AsbTrafficClassOpsRateSource(null!))
      .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// An ASB transport that has actually projected an idle ops rate: the projection only exists
  /// after a session subscription runs the self-check, which is why this subscribes rather than
  /// just constructing the transport. Non-adaptive with a one-second idle timeout, so the
  /// projection is exactly <paramref name="maxConcurrentSessions"/> ops/sec.
  /// </summary>
  private static async Task<AzureServiceBusTransport> _projectingTransportAsync(int maxConcurrentSessions) {
    var transport = new AzureServiceBusTransport(
      new RaisableServiceBusClient(),
      new JsonSerializerOptions(),
      new AzureServiceBusOptions {
        AutoProvisionInfrastructure = false,
        EnableSessions = true,
        EnableAdaptiveAcceptors = false,
        MaxConcurrentSessions = maxConcurrentSessions,
        SessionIdleTimeout = TimeSpan.FromSeconds(1),
      },
      new RecordingTransportLogger());

    await transport.InitializeAsync();
    await transport.SubscribeBatchAsync(
      (batch, ct) => Task.CompletedTask,
      new TransportDestination("inbox") { RoutingKey = "ops-rate-sub" },
      new TransportBatchOptions());

    return transport;
  }

  [Test]
  public async Task ARoutingTransport_ReportsEachNamespacesOwnRateUnderItsBoundTrafficClassAsync() {
    // Every namespace has its OWN request-credit pool, so the only actionable number is the
    // per-namespace one, stamped with the class routed there. Reporting the default's rate for
    // both, or a sum, names no pool an operator can raise a quota on — and the whole point of
    // this source is to make the pool that is actually being burned nameable before the invoice
    // is the first witness. The two namespaces are deliberately given different acceptor
    // budgets so an implementation that projected one of them twice cannot pass.
    var @default = await _projectingTransportAsync(maxConcurrentSessions: 4);
    var bulk = await _projectingTransportAsync(maxConcurrentSessions: 20);
    var router = new NamespaceRoutingTransport(
      @default,
      new Dictionary<string, ITransport>(StringComparer.Ordinal) { ["bulk"] = bulk });

    var tagOptions = new TagOptions();
    tagOptions.RouteNamespace("record-digest", "bulk");

    var rates = new AsbTrafficClassOpsRateSource(router, tagOptions).Project();

    await Assert.That(rates).Count().IsEqualTo(2)
      .Because("one entry per namespace the transport holds a client for — a namespace with no "
             + "entry has no visible idle spend at all");

    var defaultRate = rates.Single(r => r.TransportNamespace == TransportNamespaces.DefaultKey);
    var bulkRate = rates.Single(r => r.TransportNamespace == "bulk");

    await Assert.That(defaultRate.OpsPerSecond).IsEqualTo(4d)
      .Because("the default namespace's 4 acceptor slots over a one-second idle timeout are its "
             + "own spend, not the fleet's total");
    await Assert.That(bulkRate.OpsPerSecond).IsEqualTo(20d)
      .Because("the hot namespace's 20 slots must surface against ITS quota — averaging or "
             + "summing across namespaces hides the one that is actually exhausted");

    await Assert.That(bulkRate.TrafficClass).IsEqualTo("record-digest")
      .Because("the class routed to a namespace is the dimension an operator groups by when one "
             + "class of traffic starves another");
    await Assert.That(defaultRate.TrafficClass).IsEqualTo(TrafficClasses.DOMAIN)
      .Because("a namespace nothing is explicitly routed to carries ordinary application traffic");
  }

  [Test]
  public async Task WithNoTagOptions_TheRateIsStillClassifiedAsDomainTrafficAsync() {
    // Traffic-class routing is opt-in, so most hosts have no bindings at all. Their spend still
    // has to appear on the by-class breakdown: an empty or null class would drop the namespace
    // out of every grouped view, which is indistinguishable from spending nothing.
    var transport = await _projectingTransportAsync(maxConcurrentSessions: 6);

    var rates = new AsbTrafficClassOpsRateSource(transport, tagOptions: null).Project();

    await Assert.That(rates).Count().IsEqualTo(1);
    await Assert.That(rates[0].TransportNamespace).IsEqualTo(TransportNamespaces.DefaultKey);
    await Assert.That(rates[0].TrafficClass).IsEqualTo(TrafficClasses.DOMAIN)
      .Because("an unclassified namespace must land in the domain bucket, never in no bucket");
    await Assert.That(rates[0].OpsPerSecond).IsEqualTo(6d)
      .Because("the projection reported is the transport's own, so a configuration change that "
             + "raises idle churn shows up here");
  }
}
