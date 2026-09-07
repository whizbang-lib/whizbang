using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tags;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Coverage gaps left by <c>AsbBacklogPeekTests</c>: a non-ASB transport being skipped rather
/// than mis-cast, a namespace-routing transport actually being walked per namespace key, and a
/// namespace whose key matches a configured routing binding being tagged with THAT traffic
/// class rather than falling through to the unclassified default.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AsbBacklogPeek.cs</code-under-test>
public class AsbBacklogPeekCoverageTests {

  /// <summary>A transport that is not an ASB one, so the peek must skip it rather than cast it.</summary>
  private sealed class _foreignTransport : ITransport {
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(Whizbang.Core.Observability.IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<BulkPublishItemResult>> PublishBatchAsync(
        IReadOnlyList<BulkPublishItem> items, TransportDestination destination,
        CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<BulkPublishItemResult>>([]);
    public Task<Whizbang.Core.Observability.IMessageEnvelope> SendAsync<TRequest, TResponse>(
        Whizbang.Core.Observability.IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
    public Task<ISubscription> SubscribeAsync(
        Func<TransportMessage, CancellationToken, Task> handler, TransportDestination destination,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, Whizbang.Core.Workers.TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  /// <summary>
  /// Production risk if this regresses: a non-ASB transport plugged into a shared backlog-peek
  /// registration (a host mixing brokers, or a misconfiguration) would either throw on the cast
  /// or, worse, be silently mis-cast — instead of simply contributing nothing, which is what a
  /// transport with no depth/age concept must do.
  /// </summary>
  [Test]
  public async Task PeekAsync_TransportIsNotAzureServiceBus_ContributesNoSamplesAsync() {
    var samples = await new AsbBacklogPeek(new _foreignTransport()).PeekAsync(CancellationToken.None);

    await Assert.That(samples).IsEmpty();
  }

  /// <summary>
  /// Production risk if this regresses: on a multi-namespace host, a sample from a routed
  /// namespace would report as unclassified domain traffic instead of the traffic class actually
  /// backed up — the exact ambiguity a per-namespace-class dashboard exists to resolve.
  /// </summary>
  [Test]
  public async Task PeekAsync_NamespaceRoutingTransport_TagsAMatchedNamespaceWithItsRoutedTagAsync() {
    var admin = new RecordingProvisioningAdminClient { ActiveMessageCountResult = 5 };
    var bulkTransport = new AzureServiceBusTransport(
      new RaisableServiceBusClient(),
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { AutoProvisionInfrastructure = false, EnableSessions = false },
      NullLogger<AzureServiceBusTransport>.Instance,
      adminClient: admin);
    bulkTransport.LivenessWatchdog!.Track("bulk-topic", "bulk-svc-sub");
    bulkTransport.OldestEnqueuedTimeProbe = (_, _, _) => Task.FromResult<DateTimeOffset?>(null);

    var router = new NamespaceRoutingTransport(
      new _foreignTransport(),
      new Dictionary<string, ITransport>(StringComparer.Ordinal) {
        ["bulk"] = bulkTransport,
      },
      activeConsumeNamespaceKeys: null);

    var tagOptions = new TagOptions();
    tagOptions.RouteNamespace("bulk-import", "bulk");

    var sample = (await new AsbBacklogPeek(router, tagOptions).PeekAsync(CancellationToken.None)).Single();

    await Assert.That(sample.TransportNamespace).IsEqualTo("bulk");
    await Assert.That(sample.TrafficClass).IsEqualTo("bulk-import")
      .Because("the 'bulk' namespace key has a routing binding — the peek must report ITS tag, "
             + "not the unclassified default");
  }
}
