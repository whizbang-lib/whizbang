using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The no-consumer gate in the batch handler: a message whose inner type nothing here consumes is
/// skipped, and the rest of the batch still lands.
/// </summary>
/// <remarks>
/// The other tests in this class build the worker with <see cref="PermissiveReceptorRegistryQuery"/>,
/// which answers yes for every type, so this gate never fires in them. It matters because the skip
/// is per message: the all-filtered early return elsewhere in the handler drops a whole batch, and
/// if the gate behaved that way a single unconsumed type would take every message beside it down.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
public partial class TransportConsumerWorkerKnownEventFilterTests {

  [Test]
  public async Task BatchHandler_OneMessageHasNoConsumer_IsSkippedWhileTheRestOfTheBatchStoresAsync() {
    using var meterFactory = new TestMeterFactory();
    var metrics = new TransportMetrics(new WhizbangMetrics(meterFactory));
    using var metricHelper = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);

    // Both types are event types from the start, so the known-event-type filter passes both and the
    // only thing that can drop one is the consumer gate under test.
    var provider = new MutableEventTypeProvider([typeof(FilterCoverageKnownEvent), typeof(FilterCoverageUnknownEvent)]);
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IEventTypeProvider>(provider);
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    await using var sp = services.BuildServiceProvider();

    var transport = new FilterTransport();
    var worker = _buildWorker(
      transport,
      sp,
      metrics,
      new ConsumesOnlyNamedTypes(nameof(FilterCoverageKnownEvent)));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    await transport.DeliverBatchAsync([
      new TransportMessage(_createJsonEnvelope(MessageId.New()), _envelopeTypeFor(typeof(FilterCoverageUnknownEvent))),
      new TransportMessage(_createJsonEnvelope(MessageId.New()), _envelopeTypeFor(typeof(FilterCoverageKnownEvent)))
    ]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("the consumed type has to reach the inbox even though the message before it in the "
        + "same batch was skipped — the gate drops one message, not the batch");
    var dedup = metricHelper.GetByName("whizbang.transport.inbox.messages_deduplicated");
    await Assert.That(dedup).Count().IsEqualTo(1)
      .Because("exactly the unconsumed message is counted as deduplicated");
    await Assert.That(dedup[0].Value).IsEqualTo(1d);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>Answers yes only for the message types it was told about.</summary>
  private sealed class ConsumesOnlyNamedTypes(params string[] consumedSimpleNames) : IReceptorRegistryQuery {
    private readonly string[] _consumed = consumedSimpleNames;

    public bool HasReceptors(LifecycleStage stage, string messageType) => HasAnyConsumer(messageType);

    public bool HasInboxHandler(string messageType) => HasAnyConsumer(messageType);

    public bool HasAnyConsumer(string messageType) =>
      Array.Exists(_consumed, name => messageType.Contains(name, StringComparison.Ordinal));

    public IReadOnlyList<HandledMessageInfo> GetHandledMessages() => [];
  }
}
