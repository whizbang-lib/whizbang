using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Locks the <c>sys-control</c> traffic class membership (topology arc phase 9) — the split the
/// two specs left open. The control class is a DELIVERY-SEMANTICS class (short TTL, sessionless,
/// non-durable receive), so its membership is the SUPERSEDABLE control signals only:
/// <list type="bullet">
/// <item><b>In the class</b> — <c>Whizbang.Core.Messaging</c>'s integrity/redelivery families.
/// Every one of them is re-derived on the next cadence, so an expired copy costs nothing.</item>
/// <item><b>NOT in the class</b> — <c>Whizbang.Core.Commands.System</c>'s durable system commands
/// (run-control, killswitches, rebuild/reseed). They are one-shot operator intent: expiring one on
/// the broker would silently lose it. They stay on the phase-7 system BROADCAST inbox.</item>
/// <item><b>NOT in the class</b> — <c>Whizbang.Core.Minting</c>'s composite envelopes. They carry
/// real durable payload (re-delivered events, coalesced singles, audit records); the envelope is
/// wire-only but its contents are not supersedable.</item>
/// </list>
/// The trap this locks out: <see cref="IControlPlaneMessage"/> is a SECURITY/DLQ marker, not a
/// traffic class — <see cref="RebuildPerspectiveCommand"/> carries it and must NOT join the class.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tags/SystemTags.cs</code-under-test>
[Category("Core")]
[Category("Tags")]
public class SystemControlTagTests {
  private static readonly Type[] _supersedableControlSignals = [
    typeof(IntegrityCheckpoint),
    typeof(IntegrityGapDetected),
    typeof(IntegrityManifest),
    typeof(RequestIntegrityManifest),
    typeof(IntegrityDivergenceDetected),
    typeof(PerspectiveCoverageGapDetected),
    typeof(RequestRedeliveryCommand),
  ];

  private static readonly Type[] _durableSystemCommands = [
    typeof(RebuildPerspectiveCommand),
    typeof(CancelPerspectiveRebuildCommand),
    typeof(ClearCacheCommand),
    typeof(DiagnosticsCommand),
    typeof(PauseProcessingCommand),
    typeof(ResumeProcessingCommand),
  ];

  private static readonly Type[] _durableCompositeEnvelopes = [
    typeof(RedeliveryComposite),
    typeof(CoalescedEventsComposite),
    typeof(AuditEventsComposite),
  ];

  // Membership is read from the ATTRIBUTE, not from the process-global MessageTagRegistry: that
  // registry is cleared by another suite that legitimately tests registration itself, so an
  // aggregate read here would be order-dependent. The attribute is the source of truth the
  // generator reads, and asserting on it makes these locks deterministic.
  private static bool _carriesControlTag(Type messageType) =>
    messageType.GetCustomAttributes(typeof(SystemControlTagAttribute), inherit: false)
      .Cast<SystemControlTagAttribute>()
      .Any(a => string.Equals(a.Tag, SystemTags.CONTROL, StringComparison.Ordinal));

  [Test]
  public async Task Control_IsSysControlAsync() {
    var control = SystemTags.CONTROL;

    await Assert.That(control).IsEqualTo("sys-control");
  }

  [Test]
  public async Task Control_CarriesTheReservedPrefixAsync() {
    await Assert.That(SystemTags.CONTROL.StartsWith(SystemTags.RESERVED_PREFIX, StringComparison.Ordinal)).IsTrue();
  }

  [Test]
  public async Task IsFrameworkTag_KnowsControlAsync() {
    // Without this, a host binding options.Tags.RouteNamespace("sys-control", "control") — the
    // spec's own example — would fail the reserved-prefix validation at startup.
    await Assert.That(SystemTags.IsFrameworkTag(SystemTags.CONTROL)).IsTrue();
  }

  [Test]
  public async Task SupersedableControlSignals_CarryTheControlTagAsync() {
    foreach (var signal in _supersedableControlSignals) {
      await Assert.That(_carriesControlTag(signal)).IsTrue()
        .Because($"{signal.Name} is a supersedable control signal — it belongs to the control class");
    }
  }

  [Test]
  public async Task DurableSystemCommands_DoNotCarryTheControlTagAsync() {
    foreach (var command in _durableSystemCommands) {
      await Assert.That(_carriesControlTag(command)).IsFalse()
        .Because($"{command.Name} is one-shot operator intent — a short TTL would silently lose it");
    }
  }

  [Test]
  public async Task RebuildPerspectiveCommand_IsControlPlaneButNotControlClassAsync() {
    // The exact contradiction the specs left open. IControlPlaneMessage exempts a type from
    // message security and drops it instead of dead-lettering; it says nothing about whether the
    // type's VALUE expires. Rebuild is durable operator intent that happens to be framework-owned.
    await Assert.That(typeof(IControlPlaneMessage).IsAssignableFrom(typeof(RebuildPerspectiveCommand))).IsTrue();
    await Assert.That(_carriesControlTag(typeof(RebuildPerspectiveCommand))).IsFalse();
  }

  [Test]
  public async Task CompositeEnvelopes_DoNotCarryTheControlTagAsync() {
    foreach (var composite in _durableCompositeEnvelopes) {
      await Assert.That(_carriesControlTag(composite)).IsFalse()
        .Because($"{composite.Name} is a wire-only envelope around DURABLE payload — never supersedable");
    }
  }

  [Test]
  public async Task ControlClassMembership_IsExactlyTheMessagingControlFamiliesAsync() {
    // Closed-set lock over the WHOLE framework assembly: a type joining the class anywhere must be
    // a deliberate edit here. Delivery semantics change silently otherwise — a type that quietly
    // acquires a short TTL and a non-durable receive path is a type whose messages start
    // disappearing under load, with nothing in the diff to explain it.
    var tagged = typeof(IntegrityCheckpoint).Assembly.GetTypes()
      .Where(_carriesControlTag)
      .OrderBy(t => t.FullName, StringComparer.Ordinal)
      .ToList();

    var expected = _supersedableControlSignals
      .OrderBy(t => t.FullName, StringComparer.Ordinal)
      .ToList();

    await Assert.That(tagged.Count).IsEqualTo(expected.Count);
    for (var i = 0; i < expected.Count; i++) {
      await Assert.That(tagged[i]).IsEqualTo(expected[i]);
    }
  }


  [Test]
  public async Task ControlClassMembers_AllLiveInTheControlPlaneContractNamespaceAsync() {
    // The subscription filter that carries this class is a namespace pattern
    // (whizbang.core.messaging.#), so class membership and contract namespace must agree —
    // a tagged type outside the namespace would be routed by the tag but never subscribed.
    foreach (var signal in _supersedableControlSignals) {
      await Assert.That(signal.Namespace).IsEqualTo("Whizbang.Core.Messaging");
    }
  }
}
