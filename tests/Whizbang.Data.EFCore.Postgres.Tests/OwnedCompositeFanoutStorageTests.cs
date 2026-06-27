using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End-to-end (real Postgres) proof of producer-side composite fan-out: publishing a composite in the
/// service's OWN domain expands it into child inbox rows persisted to wh_inbox — one per inner event,
/// all on the composite's stream, each carrying composite lineage in its hops — so the publishing
/// service's own pipeline (claim → event store → perspectives) materializes them. Closes the gap where
/// a composite is IMessage-not-IEvent (no local append) and echo-suppressed on self-receive.
/// </summary>
public class OwnedCompositeFanoutStorageTests : EFCoreTestBase {

  public sealed class BulkJobComposite : CompositeEventBase {
    public BulkJobComposite() => Atomicity = FanoutAtomicity.Atomic;
  }

  public record BulkInnerEvent([property: StreamId] Guid StreamId, string Data) : IEvent;

  // The StreamIdGenerator now discovers [StreamId] on concrete ICompositeEvent types (inherited from
  // CompositeEventBase) and emits an object-typed resolver case (TRY_RESOLVE_OTHER_DISPATCH /
  // OTHER_EXTRACTORS), so producer-side fan-out routes the composite's inner events onto its declared
  // stream. This test is the green end-to-end proof; the producer-side fan-out + lineage are also
  // unit-proven (DispatcherOwnedCompositeFanoutTests, CompositeInboxFanoutTests).
  [Test]
  public async Task PublishOwnedComposite_FansOutInnerEventsToInbox_OnCompositeStream_WithLineageAsync() {
    await using var serviceProvider = (await _buildServicesAsync()).BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    var jobStreamId = (Guid)TrackedGuid.NewMedo();
    var composite = new BulkJobComposite {
      StreamId = jobStreamId,
      Inner = [
        new BulkInnerEvent(jobStreamId, "init"),
        new BulkInnerEvent(jobStreamId, "field-a"),
        new BulkInnerEvent(jobStreamId, "field-b"),
      ],
    };

    await dispatcher.PublishAsync(composite);

    await using var db = CreateDbContext();
    var children = await db.Inbox.Where(r => r.StreamId == jobStreamId).ToListAsync();

    // One child inbox row per inner event, all on the composite's stream.
    await Assert.That(children.Count).IsEqualTo(3)
      .Because("the owned composite fans out locally into one inbox row per inner event on the job stream.");
    await Assert.That(children.All(r => r.MessageType.Contains("BulkInnerEvent", StringComparison.Ordinal))).IsTrue();

    // The composite itself is never stored on its stream — only the inner events are.
    await Assert.That(children.Any(r => r.MessageType.Contains("BulkJobComposite", StringComparison.Ordinal))).IsFalse()
      .Because("the composite is wire-only — only its inner events are stored.");

    // Composite lineage: every child's creation hop traces back to the same composite.
    var causationIds = children.Select(r => r.Metadata.Hops[0].CausationId).Distinct().ToList();
    await Assert.That(causationIds.Count).IsEqualTo(1)
      .Because("all children of one composite share the same causation — the composite's MessageId.");
    await Assert.That(causationIds[0]).IsNotNull()
      .Because("the creation hop carries the composite's MessageId as CausationId.");
    await Assert.That(children.All(r => r.Metadata.Hops[0].CausationType == nameof(BulkJobComposite))).IsTrue()
      .Because("each child's creation hop records the composite type as its cause.");
  }

  private async Task<ServiceCollection> _buildServicesAsync() {
    await base.SetupAsync();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddScoped(_ => CreateDbContext());

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

    services.AddScoped<IWorkCoordinator>(sp =>
      new EFCoreWorkCoordinator<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddScoped<IWorkCoordinatorStrategy>(sp =>
      new ScopedWorkCoordinatorStrategy(
        sp.GetRequiredService<IWorkCoordinator>(),
        sp.GetRequiredService<IServiceInstanceProvider>(),
        workChannelWriter: null,
        new WorkCoordinatorOptions { LeaseSeconds = 30, AbandonStaleInstanceThresholdSeconds = 300, PartitionCount = 4 },
        sp.GetService<ILogger<ScopedWorkCoordinatorStrategy>>()));

    // Own this test namespace so the composite triggers producer-side local fan-out.
    services.Configure<RoutingOptions>(o => o.OwnDomains(typeof(BulkJobComposite).Namespace!));

    services.AddReceptors();
    services.AddWhizbangStreamIdExtractor();
    services.AddWhizbangDispatcher();
    return services;
  }
}
