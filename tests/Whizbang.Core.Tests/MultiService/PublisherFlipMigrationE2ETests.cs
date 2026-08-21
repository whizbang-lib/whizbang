using System.Text.Json.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.MultiService;

namespace WbFlip.Orders.Commands {
  /// <summary>Flip-migration probe command. Deliberately OUTSIDE the Whizbang.Core.*
  /// namespace subtree — the framework-reserved subtree never gets per-namespace inboxes.</summary>
  public sealed record FlipProbe(string Marker);
}

namespace Whizbang.Core.Tests.MultiService {
  /// <summary>
  /// THE FLIP-MIGRATION LOCKS (topology arc phase 6, store tier): a consumer on the
  /// namespace-inbox topology subscribes to its per-namespace inbox AND (transitionally)
  /// the shared inbox, so during the migration window a publisher may route a namespace's
  /// commands via EITHER path — flip-in-flight and rollback lose nothing, and the
  /// dual-delivery overlap yields identity-equal rows the store's message-id dedup absorbs
  /// (the atomic dedup itself is locked by the work-coordinator suites; the transport
  /// receive seam stores what arrives).
  /// </summary>
  /// <code-under-test>src/Whizbang.Core/Routing/NamespaceInboxStrategy.cs</code-under-test>
  /// <code-under-test>src/Whizbang.Core/Routing/NamespaceOutboxStrategy.cs</code-under-test>
  /// <code-under-test>src/Whizbang.Testing/MultiService/MultiServiceHarness.cs</code-under-test>
  [Category("MultiService")]
  [NotInParallel("WhizbangBackgroundServiceTests")]
  public class PublisherFlipMigrationE2ETests {
    private const string SERVICE = "flip-svc";
    private const string SHARED_INBOX = "inbox";
    private const string NAMESPACE_INBOX = "inbox.wbflip.orders.commands";

    public sealed class FlipProbeCatalog : IMessageTypeCatalog {
      public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
        new(typeof(WbFlip.Orders.Commands.FlipProbe),
            TypeNameFormatter.FormatClrTypeName(typeof(WbFlip.Orders.Commands.FlipProbe)), "command", null),
      ];
    }

    /// <summary>Registry reporting this service HANDLES the probe's command namespace — the
    /// input the namespace-inbox strategy derives its owned inbox subscription from.</summary>
    private sealed class HandlesFlipProbeRegistry : IReceptorRegistryQuery {
      public bool HasReceptors(LifecycleStage stage, string messageType) => true;
      public bool HasInboxHandler(string messageType) => true;
      public bool HasAnyConsumer(string messageType) => true;
      public IReadOnlyList<HandledMessageInfo> GetHandledMessages() => [
        new("WbFlip.Orders.Commands.FlipProbe", "wbflip.orders.commands", MessageKind.Command)
      ];
    }

    private static bool _contextRegistered;
    private static readonly Lock _registerLock = new();

    private static void _ensureJsonContext() {
      lock (_registerLock) {
        if (!_contextRegistered) {
          JsonContextRegistry.RegisterContext(PublisherFlipMigrationE2EJsonContext.Default);
          _contextRegistered = true;
        }
      }
    }

    private static async Task<MultiServiceHarness> _startNamespaceInboxServiceAsync() {
      return await MultiServiceHarness.Create()
        .AddService(SERVICE, svc => svc
          .WithCatalog<FlipProbeCatalog>()
          // Last-wins over the harness's claim-all default: this registry also ENUMERATES
          // the handled command namespace, which is what the namespace-inbox strategy
          // derives the owned per-namespace subscription from.
          .Configure(services => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton<IReceptorRegistryQuery>(services, new HandlesFlipProbeRegistry()))
          .WithRouting(r => {
            r.Inbox.UseNamespaceInboxes();
            r.Outbox.UseNamespaceRouting();
            r.RouteCommandNamespaceToInbox("WbFlip.Orders.Commands");
          }))
        .StartAsync();
    }

    private static async Task _publishProbeAsync(
        MultiServiceHarness harness, string topic, string marker, Guid? messageId = null) {
      var envelope = new MessageEnvelope<WbFlip.Orders.Commands.FlipProbe> {
        MessageId = messageId is { } id ? MessageId.From(id) : new MessageId(TrackedGuid.NewMedo()),
        Payload = new WbFlip.Orders.Commands.FlipProbe(marker),
        Hops = [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = DateTimeOffset.UtcNow,
            ServiceInstance = ServiceInstanceInfo.Unknown
          }
        ],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      var envelopeType =
        $"Whizbang.Core.Messaging.MessageEnvelope`1[[{typeof(WbFlip.Orders.Commands.FlipProbe).AssemblyQualifiedName}]], Whizbang.Core";
      await harness.Wire.PublishAsync(envelope, new TransportDestination(topic), envelopeType);
    }

    [Test]
    public async Task FlipInFlight_TrafficStraddlesTheBoundary_NothingLostAsync() {
      _ensureJsonContext();
      await using var harness = await _startNamespaceInboxServiceAsync();

      // Traffic in flight DURING the flip: the first half was published before the
      // publisher flipped (shared inbox), the second half after (per-namespace inbox).
      // The namespace-inbox consumer holds BOTH subscriptions, so nothing is lost.
      for (var i = 0; i < 3; i++) {
        await _publishProbeAsync(harness, SHARED_INBOX, $"pre-flip-{i}");
      }
      for (var i = 0; i < 3; i++) {
        await _publishProbeAsync(harness, NAMESPACE_INBOX, $"post-flip-{i}");
      }

      var stored = await harness.GetService(SERVICE).Inbox.WaitForInboxAsync(6, TimeSpan.FromSeconds(10));
      await Assert.That(stored.Count).IsEqualTo(6)
        .Because("every command published across the flip boundary lands — no loss, whichever path carried it");
      await Assert.That(stored.Select(m => m.MessageId).Distinct().Count()).IsEqualTo(6)
        .Because("six distinct commands, six distinct identities — no duplication either");
    }

    [Test]
    public async Task Rollback_MidMigration_TrafficReturnsToTheSharedInbox_NothingLostAsync() {
      _ensureJsonContext();
      await using var harness = await _startNamespaceInboxServiceAsync();

      // flip … and roll back: old path → new path → old path again. Rollback is a
      // publisher-side config change; the consumer's transitional shared subscription
      // (retires only in phase 7) keeps catching everything.
      await _publishProbeAsync(harness, SHARED_INBOX, "before-flip");
      await _publishProbeAsync(harness, NAMESPACE_INBOX, "flipped");
      await _publishProbeAsync(harness, SHARED_INBOX, "rolled-back");

      var stored = await harness.GetService(SERVICE).Inbox.WaitForInboxAsync(3, TimeSpan.FromSeconds(10));
      await Assert.That(stored.Count).IsEqualTo(3)
        .Because("rollback mid-migration loses nothing — the shared path is still subscribed");
    }

    [Test]
    public async Task DualDeliveryOverlap_SameMessageId_BothCopiesCarryTheSameIdentityAsync() {
      _ensureJsonContext();
      await using var harness = await _startNamespaceInboxServiceAsync();

      // The migration overlap: the SAME command (same message id) arrives via the old AND
      // the new path (e.g. an outbox retry crossing the flip boundary). Both copies reach
      // the receive seam carrying the SAME identity — the store's atomic message-id dedup
      // (locked by the work-coordinator suites) absorbs the second write. Convergence is
      // idempotent by identity, not by coincidence.
      var messageId = Guid.CreateVersion7();
      await _publishProbeAsync(harness, SHARED_INBOX, "overlap", messageId);
      await _publishProbeAsync(harness, NAMESPACE_INBOX, "overlap", messageId);

      var stored = await harness.GetService(SERVICE).Inbox.WaitForInboxAsync(2, TimeSpan.FromSeconds(10));
      await Assert.That(stored.Count).IsEqualTo(2);
      await Assert.That(stored.Select(m => m.MessageId).Distinct().Count()).IsEqualTo(1)
        .Because("both paths deliver the SAME message id — the store's unique(message_id) key dedups, processed once");
      await Assert.That(stored[0].MessageId).IsEqualTo(messageId);
    }

    [Test]
    public async Task Retirement_SharedInboxDropped_NamespaceAndBroadcastCarryEverythingAsync() {
      // THE PHASE-7 RETIREMENT SHAPE on the harness: full flip + RetireSharedInbox — the
      // service subscribes to its per-namespace inbox and the system broadcast inbox ONLY.
      // A probe published to the legacy shared topic is delivered to NOBODY (the wire has no
      // subscriber there), while the namespace-inbox and broadcast probes land. Wire delivery
      // is synchronous with publish, so "not delivered" is deterministic — no timing.
      _ensureJsonContext();
      await using var harness = await MultiServiceHarness.Create()
        .AddService(SERVICE, svc => svc
          .WithCatalog<FlipProbeCatalog>()
          .Configure(services => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton<IReceptorRegistryQuery>(services, new HandlesFlipProbeRegistry()))
          .WithRouting(r => {
            r.Inbox.UseNamespaceInboxes();
            r.Outbox.UseNamespaceRouting();
            r.RouteAllCommandNamespacesToInbox();
            r.RetireSharedInbox();
          }))
        .StartAsync();

      var sharedProbeId = Guid.CreateVersion7();
      await _publishProbeAsync(harness, SHARED_INBOX, "via-shared-must-drop", sharedProbeId);
      await _publishProbeAsync(harness, NAMESPACE_INBOX, "via-owned-inbox");
      await _publishProbeAsync(harness, CommandInboxNaming.SystemBroadcastTopic, "via-broadcast-inbox");

      var stored = await harness.GetService(SERVICE).Inbox.WaitForInboxAsync(2, TimeSpan.FromSeconds(10));
      await Assert.That(stored.Count).IsEqualTo(2)
        .Because("exactly the namespace-inbox and broadcast probes land — no catch-all remnant");
      await Assert.That(stored.Select(m => m.MessageId)).DoesNotContain(sharedProbeId)
        .Because("the shared-inbox probe was published FIRST and synchronously delivered to nobody — "
               + "the retirement consumer holds no subscription on the legacy shared topic");
    }

    [Test]
    public async Task NamespaceInboxConsumer_ListensOnOwnedInbox_SystemBroadcast_AndTransitionalSharedAsync() {
      _ensureJsonContext();
      await using var harness = await _startNamespaceInboxServiceAsync();

      // The migration topology in one glance, proven by DELIVERY: per-namespace inbox
      // (permanent), system broadcast inbox (permanent), transitional shared inbox
      // (retires phase 7) — one probe to each, all three land.
      await _publishProbeAsync(harness, NAMESPACE_INBOX, "via-owned-inbox");
      await _publishProbeAsync(harness, CommandInboxNaming.SystemBroadcastTopic, "via-broadcast-inbox");
      await _publishProbeAsync(harness, SHARED_INBOX, "via-shared-inbox");

      var stored = await harness.GetService(SERVICE).Inbox.WaitForInboxAsync(3, TimeSpan.FromSeconds(10));
      await Assert.That(stored.Count).IsEqualTo(3)
        .Because("the namespace-inbox consumer holds all three subscriptions during the migration window");
    }
  }

  /// <summary>JSON context for the flip-migration probe (production-parity registration).</summary>
  [JsonSerializable(typeof(WbFlip.Orders.Commands.FlipProbe))]
  [JsonSerializable(typeof(MessageEnvelope<WbFlip.Orders.Commands.FlipProbe>))]
  public sealed partial class PublisherFlipMigrationE2EJsonContext : JsonSerializerContext;
}
