// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.CompositePublish.Bundles;
using Whizbang.Core.Component.Tests.CompositePublish.Owned;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Dispatch.Policy;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Publishing a composite the service owns, against the REAL generated dispatcher: every inner event is stored
/// (an event-store row, no destination) and handled here once, and the composite itself goes out once for the
/// other services that fan it out on receive.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/messaging/composite-events</docs>
public class OwnedCompositePublishComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.CompositePublish.Owned";
  private const string BUNDLE_DOMAIN = "Whizbang.Core.Component.Tests.CompositePublish.Bundles";

  private static readonly Type[] _innerTypes = [
    typeof(CpAssigned), typeof(CpStepAssigned), typeof(CpStepApproved),
    typeof(CpStageCompleted), typeof(CpStageStarted), typeof(CpInitialized)];

  [Test]
  public async Task PublishingAnOwnedComposite_StoresAndHandlesEveryInnerEvent_AndSendsTheCompositeAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var streamId = Guid.CreateVersion7();
    var composite = new CpWorkflowComposite {
      StreamId = streamId,
      Inner = [
        new CpAssigned(streamId), new CpStepAssigned(streamId), new CpStepApproved(streamId),
        new CpStageCompleted(streamId), new CpStageStarted(streamId), new CpInitialized(streamId)],
    };

    await dispatcher.PublishAsync(composite);

    foreach (var inner in _innerTypes) {
      await Assert.That(CompositeProbe.HandledCount(streamId, inner.Name)).IsEqualTo(1)
        .Because($"{inner.Name} is handled here once");
      await Assert.That(outbox.QueuedOf(inner).Count(m => m.Destination is null)).IsEqualTo(1)
        .Because($"{inner.Name} is stored once, as an event-store row");
      await Assert.That(outbox.QueuedOf(inner).Count(m => m.Destination is not null)).IsEqualTo(0)
        .Because($"{inner.Name} is never sent on its own; the composite carries it");
    }
    await Assert.That(outbox.QueuedOf(typeof(CpWorkflowComposite)).Count(m => m.Destination is not null)).IsEqualTo(1)
      .Because("the composite goes out once, for the services that fan it out on receive");
  }

  /// <summary>
  /// The composite carries events, so its copy goes to the event topic its inner events would go to, where the
  /// services subscribed to those events receive it. 0.2623.0 routed every message that is not an
  /// <see cref="IEvent"/> to its owner's command destination, so the composite (an <see cref="IMessage"/>) went
  /// only to the publishing service itself, which discards its own echo, and no subscriber ever received it.
  /// </summary>
  [Test]
  public async Task PublishingAnOwnedComposite_SendsItToTheEventTopic_NotTheOwnersCommandDestinationAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var streamId = Guid.CreateVersion7();

    // A plain event of the same domain, published with no composite, shows where its events go
    await dispatcher.PublishAsync(new CpAssigned(streamId));
    var eventTopic = outbox.QueuedOf(typeof(CpAssigned)).Single(m => m.Destination is not null).Destination;

    await dispatcher.PublishAsync(new CpWorkflowComposite { StreamId = streamId, Inner = [new CpStepAssigned(streamId)] });

    var compositeDestination = outbox.QueuedOf(typeof(CpWorkflowComposite)).Single(m => m.Destination is not null).Destination;
    await Assert.That(compositeDestination).IsEqualTo(eventTopic)
      .Because("a composite is a bundle of events and travels where its events do, to every subscriber");
  }

  /// <summary>
  /// A composite declared in another namespace than its events goes to its events' topic: the services that receive
  /// it subscribe to those events, not to the namespace the bundle type happens to live in.
  /// </summary>
  [Test]
  public async Task ACompositeDeclaredInAnotherNamespace_GoesToItsEventsTopicAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var streamId = Guid.CreateVersion7();
    await dispatcher.PublishAsync(new CpAssigned(streamId));
    var eventTopic = outbox.QueuedOf(typeof(CpAssigned)).Single(m => m.Destination is not null).Destination;

    await dispatcher.PublishAsync(new CpBundleComposite { StreamId = streamId, Inner = [new CpStepAssigned(streamId), new CpStageStarted(streamId)] });

    var destinations = outbox.QueuedOf(typeof(CpBundleComposite)).Select(m => m.Destination).ToList();
    await Assert.That(destinations).IsEquivalentTo([eventTopic])
      .Because("a bundle travels to the topic its events' subscribers listen on");
  }

  public enum CompositeEntry {
    Publish,
    PublishMany,
    ReturnedByAReceptor,
  }

  /// <summary>
  /// Every way a composite reaches the outbox sends it to its events' topic. 0.2623.0 sent each to the owner's command
  /// destination, and a composite returned by a receptor was refused as an owned command nothing handles.
  /// </summary>
  [Test]
  [MatrixDataSource]
  public async Task EveryWayAnOwnedCompositeIsWritten_SendsItToTheEventTopicAsync([Matrix] CompositeEntry entry) {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var streamId = Guid.CreateVersion7();
    await dispatcher.PublishAsync(new CpAssigned(streamId));
    var eventTopic = outbox.QueuedOf(typeof(CpAssigned)).Single(m => m.Destination is not null).Destination;

    switch (entry) {
      case CompositeEntry.Publish:
        await dispatcher.PublishAsync(new CpWorkflowComposite { StreamId = streamId, Inner = [new CpStepApproved(streamId)] });
        break;
      case CompositeEntry.PublishMany:
        await dispatcher.PublishManyAsync<CpWorkflowComposite>([new CpWorkflowComposite { StreamId = streamId, Inner = [new CpStepApproved(streamId)] }]);
        break;
      default:
        await dispatcher.LocalInvokeAsync(new CpStartWorkflow(streamId));
        break;
    }

    var compositeDestinations = outbox.QueuedOf(typeof(CpWorkflowComposite)).Select(m => m.Destination).ToList();
    await Assert.That(compositeDestinations).IsEquivalentTo([eventTopic])
      .Because($"a composite written by {entry} goes out once, to the topic its events go to");
  }

  /// <summary>
  /// Every way an owned composite is written expands it here: each inner event is stored once and handled once,
  /// as a single publish does. A publish-many and a composite returned by a receptor used to skip the local
  /// fan-out, so the publishing service never stored its own events (its echo is discarded on receive).
  /// </summary>
  [Test]
  [MatrixDataSource]
  public async Task EveryWayAnOwnedCompositeIsWritten_StoresAndHandlesItsInnerEventsHereOnceAsync([Matrix] CompositeEntry entry) {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var streamId = Guid.CreateVersion7();

    switch (entry) {
      case CompositeEntry.Publish:
        await dispatcher.PublishAsync(new CpWorkflowComposite { StreamId = streamId, Inner = [new CpStepApproved(streamId)] });
        break;
      case CompositeEntry.PublishMany:
        await dispatcher.PublishManyAsync<CpWorkflowComposite>([new CpWorkflowComposite { StreamId = streamId, Inner = [new CpStepApproved(streamId)] }]);
        break;
      default:
        await dispatcher.LocalInvokeAsync(new CpStartWorkflow(streamId));
        break;
    }

    await Assert.That(CompositeProbe.HandledCount(streamId, nameof(CpStepApproved))).IsEqualTo(1)
      .Because($"the inner event of a composite written by {entry} is handled here once");
    await Assert.That(outbox.QueuedOf(typeof(CpStepApproved)).Count(m => m.Destination is null)).IsEqualTo(1)
      .Because($"the inner event of a composite written by {entry} is stored here once");
  }

  /// <summary>
  /// A composite scheduled for later is expanded when it is delivered, not when it is published: its inner events
  /// are neither stored nor handled at publish. The local fan-out used to run at publish whatever the schedule.
  /// </summary>
  [Test]
  public async Task AScheduledOwnedComposite_IsNotExpandedBeforeItsTimeAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var streamId = Guid.CreateVersion7();

    await dispatcher.PublishAsync(new CpWorkflowComposite { StreamId = streamId, Inner = [new CpStepApproved(streamId)] },
      new DispatchOptions { ScheduledFor = DateTimeOffset.UtcNow.AddHours(1) });

    await Assert.That(CompositeProbe.HandledCount(streamId, nameof(CpStepApproved))).IsEqualTo(0)
      .Because("a scheduled composite's events are handled when it is delivered");
    await Assert.That(outbox.QueuedOf(typeof(CpStepApproved)).Count).IsEqualTo(0)
      .Because("a scheduled composite's events are stored when it is delivered");
    await Assert.That(outbox.QueuedOf(typeof(CpWorkflowComposite)).Count(m => m.Destination is not null)).IsEqualTo(1)
      .Because("the composite itself is written now, for delivery at its time");
  }

  /// <summary>
  /// Only an event is stored. A message that is not one, routed to the event store only, is refused: its row had no
  /// destination and was not an event, so the outbox published it to nowhere and the store skipped it, and it
  /// vanished without a word.
  /// </summary>
  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task ANonEventRoutedToTheEventStoreOnly_IsRefusedNotDroppedAsync(bool composite) {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    var publish = composite
      ? (Func<Task>)(async () => await dispatcher.PublishAsync(Route.EventStoreOnly(new CpWorkflowComposite { StreamId = id, Inner = [new CpStepApproved(id)] })))
      : async () => await dispatcher.PublishAsync(Route.EventStoreOnly(new CpNotice(id)));

    await Assert.That(publish).Throws<DispatchRejectedException>()
      .Because("only an event is stored; a store-only route for anything else must say so");
    await Assert.That(outbox.QueuedOf(composite ? typeof(CpWorkflowComposite) : typeof(CpNotice)).Count).IsEqualTo(0);
  }

  private static ServiceProvider _buildProvider(PolicyRecordingOutbox outbox) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton<IEnvelopeSerializer>(new PolicyContextKeepingEnvelopeSerializer());
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN, BUNDLE_DOMAIN));
    // Namespace routing, as a deployed service has: every event of a domain goes to that domain's topic
    services.AddSingleton<IOutboxRoutingStrategy>(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RoutingOptions>>().Value.OutboxStrategy);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
