using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Locks the audit rebase onto generic tag-bound coalescing (increment D): EventAudited /
/// CommandAudited carry the framework sys-audit tag in the GENERATED registry (the
/// MessageTagDiscoveryGenerator saw the explicit <c>Tag = SystemTags.AUDIT</c> at the usage
/// site); EnableAudit() translates the audit-ship knobs into the built-in
/// <c>Coalesce(SystemTags.AUDIT)</c> binding (registered FIRST — a host binding replaces it);
/// audit singles gain group + floor via the generic mint path with NO audit-specific stamping
/// left in the builder; slide = 0 keeps today's immediate individual shipping.
/// </summary>
[Category("SystemEvents")]
public class AuditCoalesceRebaseTests {
  private static readonly DateTimeOffset _testNow = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

  #region Generated-registry tag discovery

  [Test]
  public async Task EventAudited_CarriesTheSysAuditTag_InTheGeneratedRegistryAsync() {
    // The REAL process-global registry: Whizbang.Core's own generated MessageTagRegistry must
    // have discovered [SystemAuditTag(Tag = SystemTags.AUDIT)] on EventAudited.
    var tags = MessageTagRegistry.GetTagsFor(typeof(EventAudited)).ToList();

    await Assert.That(tags.Any(t => t.Tag == SystemTags.AUDIT)).IsTrue()
      .Because("the generic mint path can only resolve the audit group if the generator registered the tag");
  }

  [Test]
  public async Task CommandAudited_CarriesTheSysAuditTag_InTheGeneratedRegistryAsync() {
    var tags = MessageTagRegistry.GetTagsFor(typeof(CommandAudited)).ToList();

    await Assert.That(tags.Any(t => t.Tag == SystemTags.AUDIT)).IsTrue();
  }

  #endregion

  #region EnableAudit → built-in binding translation

  [Test]
  public async Task Apply_AuditEnabled_RegistersTheBuiltInBindingFromTheKnobsAsync() {
    var systemEventOptions = new SystemEventOptions {
      AuditShipSlideSeconds = 20,
      AuditShipMaxDelaySeconds = 300,
      AuditShipMaxBatchCount = 50
    };
    systemEventOptions.EnableAudit();
    var tagOptions = new TagOptions();

    SystemEventCoalesceDefaults.Apply(tagOptions, systemEventOptions);

    var binding = tagOptions.CoalesceBindings[SystemTags.AUDIT];
    await Assert.That(binding.SlideSeconds).IsEqualTo(20);
    await Assert.That(binding.MaxDelaySeconds).IsEqualTo(300);
    await Assert.That(binding.MaxBatchCount).IsEqualTo(50);
    await Assert.That(binding.Atomicity).IsEqualTo(FanoutAtomicity.Independent);
  }

  [Test]
  public async Task Apply_SlideZero_RegistersNothingAsync() {
    // Slide = 0 is the bypass: no binding, no group, no floor — today's immediate per-event
    // shipping, unchanged.
    var systemEventOptions = new SystemEventOptions { AuditShipSlideSeconds = 0 };
    systemEventOptions.EnableAudit();
    var tagOptions = new TagOptions();

    SystemEventCoalesceDefaults.Apply(tagOptions, systemEventOptions);

    await Assert.That(tagOptions.CoalesceBindings.ContainsKey(SystemTags.AUDIT)).IsFalse();
  }

  [Test]
  public async Task Apply_AuditNotEnabled_RegistersNothingAsync() {
    var tagOptions = new TagOptions();

    SystemEventCoalesceDefaults.Apply(tagOptions, new SystemEventOptions());
    SystemEventCoalesceDefaults.Apply(tagOptions, null);

    await Assert.That(tagOptions.CoalesceBindings.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Apply_HostBindingForSysAudit_AlwaysWinsAsync() {
    // Built-ins ship as defaults, never as locks: whichever order the host composed its
    // registrations in, the host's Coalesce(SystemTags.AUDIT) binding survives.
    var systemEventOptions = new SystemEventOptions();
    systemEventOptions.EnableAudit();

    // Host bound first, built-in applied after.
    var hostFirst = new TagOptions();
    hostFirst.Coalesce(SystemTags.AUDIT, c => c.SlideSeconds = 45);
    SystemEventCoalesceDefaults.Apply(hostFirst, systemEventOptions);
    await Assert.That(hostFirst.CoalesceBindings[SystemTags.AUDIT].SlideSeconds).IsEqualTo(45);

    // Built-in applied first, host bound after.
    var builtInFirst = new TagOptions();
    SystemEventCoalesceDefaults.Apply(builtInFirst, systemEventOptions);
    builtInFirst.Coalesce(SystemTags.AUDIT, c => c.SlideSeconds = 45);
    await Assert.That(builtInFirst.CoalesceBindings[SystemTags.AUDIT].SlideSeconds).IsEqualTo(45);
  }

  [Test]
  public async Task Apply_BuiltInBinding_SuppliesTheAuditEventsCompositeFactoryAsync() {
    var systemEventOptions = new SystemEventOptions();
    systemEventOptions.EnableAudit();
    var tagOptions = new TagOptions();
    SystemEventCoalesceDefaults.Apply(tagOptions, systemEventOptions);
    var binding = tagOptions.CoalesceBindings[SystemTags.AUDIT];
    var single = _auditSingle(systemEventOptions);

    var composite = binding.CompositeFactory!(new CoalesceFoldBatch {
      Group = SystemTags.AUDIT,
      Singles = [single!],
      Atomicity = binding.Atomicity
    });

    await Assert.That(composite).IsTypeOf<AuditEventsComposite>()
      .Because("the audit group folds into its proven carrier, not the generic composite");
    var audit = (AuditEventsComposite)composite;
    await Assert.That(audit.InnerEventIds).IsEquivalentTo(new List<Guid> { single!.MessageId });
    await Assert.That(audit.InnerTypeNames[0]).IsEqualTo(single.MessageType);
  }

  #endregion

  #region The generic mint path replaces the builder's special case

  [Test]
  public async Task AuditSingle_GainsGroupAndFloor_ViaTheGenericMintPathAsync() {
    // End to end through the real seam: EnableAudit + the built-in binding; the audit
    // companion built inside AddOutboxMessage comes out stamped with group sys-audit and the
    // MaxDelay floor — by the RESOLVER, not by audit-specific builder code.
    var time = new FakeTimeProvider(_testNow);
    var systemEventOptions = new SystemEventOptions();
    systemEventOptions.EnableEventAudit();
    var tagOptions = new TagOptions();
    SystemEventCoalesceDefaults.Apply(tagOptions, systemEventOptions);
    var resolver = new CoalesceGroupResolver(tagOptions, time);  // REAL generated registry
    var queues = new WorkCoordinatorQueues(logger: null, coalesceResolver: resolver);

    queues.AddOutboxMessage(_domainEvent(), systemEventOptions);

    await Assert.That(queues.PendingAuditMessages.Count).IsEqualTo(1);
    var auditSingle = queues.PendingAuditMessages[0];
    await Assert.That(auditSingle.CoalesceGroup).IsEqualTo(SystemTags.AUDIT);
    await Assert.That(auditSingle.ScheduledFor)
      .IsEqualTo(_testNow.AddSeconds(systemEventOptions.AuditShipMaxDelaySeconds))
      .Because("the floor now rides the generic stamping — same transaction durability, invisible to the pump");
  }

  [Test]
  public async Task SlideZero_AuditSingle_ShipsImmediatelyWithNoGroupAndNoFloorAsync() {
    // Regression lock on today's behavior: slide = 0 → no binding is registered, so the
    // companion has no group and no floor and the pump claims it immediately.
    var time = new FakeTimeProvider(_testNow);
    var systemEventOptions = new SystemEventOptions { AuditShipSlideSeconds = 0 };
    systemEventOptions.EnableEventAudit();
    var tagOptions = new TagOptions();
    SystemEventCoalesceDefaults.Apply(tagOptions, systemEventOptions);
    var resolver = new CoalesceGroupResolver(tagOptions, time);
    var queues = new WorkCoordinatorQueues(logger: null, coalesceResolver: resolver);

    queues.AddOutboxMessage(_domainEvent(), systemEventOptions);

    var auditSingle = queues.PendingAuditMessages[0];
    await Assert.That(auditSingle.CoalesceGroup).IsNull();
    await Assert.That(auditSingle.ScheduledFor).IsNull();
  }

  [Test]
  public async Task HostOverrideBinding_ChangesTheAuditCadenceAsync() {
    // The shipped-default-override mechanism end to end: a host binding for SystemTags.AUDIT
    // replaces the built-in cadence — the floor tracks the HOST's MaxDelaySeconds.
    var time = new FakeTimeProvider(_testNow);
    var systemEventOptions = new SystemEventOptions();
    systemEventOptions.EnableEventAudit();
    var tagOptions = new TagOptions();
    tagOptions.Coalesce(SystemTags.AUDIT, c => {
      c.SlideSeconds = 30;
      c.MaxDelaySeconds = 600;
    });
    SystemEventCoalesceDefaults.Apply(tagOptions, systemEventOptions);
    var resolver = new CoalesceGroupResolver(tagOptions, time);
    var queues = new WorkCoordinatorQueues(logger: null, coalesceResolver: resolver);

    queues.AddOutboxMessage(_domainEvent(), systemEventOptions);

    var auditSingle = queues.PendingAuditMessages[0];
    await Assert.That(auditSingle.CoalesceGroup).IsEqualTo(SystemTags.AUDIT);
    await Assert.That(auditSingle.ScheduledFor).IsEqualTo(_testNow.AddSeconds(600));
  }

  [Test]
  public async Task Builder_NoLongerStampsItsOwnFloorAsync() {
    // The audit-specific floor stamping is DELETED: without a resolver in play, the builder's
    // output carries no ScheduledFor even with slide > 0 — the generic mint path is the one
    // and only stamping seam.
    var systemEventOptions = new SystemEventOptions();
    systemEventOptions.EnableEventAudit();

    var built = AuditOutboxMessageBuilder.TryBuildAuditMessage(_domainEvent(), systemEventOptions);

    await Assert.That(built).IsNotNull();
    await Assert.That(built!.ScheduledFor).IsNull()
      .Because("zero audit-specific shipping code: the builder builds, the resolver stamps");
    await Assert.That(built.CoalesceGroup).IsNull();
  }

  #endregion

  #region Helpers

  private static OutboxMessage? _auditSingle(SystemEventOptions options) =>
    AuditOutboxMessageBuilder.TryBuildAuditMessage(_domainEvent(), options);

  private static OutboxMessage _domainEvent() {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = Whizbang.Core.ValueObjects.MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { test = "data" }),
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "test-destination",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
      EnvelopeType = "TestEnvelopeType",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      MessageType = "TestNamespace.TestEvent, TestAssembly"
    };
  }

  #endregion
}
