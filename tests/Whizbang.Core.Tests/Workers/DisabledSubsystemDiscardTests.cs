using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <para>Locks the disabled-subsystem discard (issue #664). Disabling a subsystem stops its
/// producers, but messages already in flight — or minted by an old build — still arrive
/// wrapped (<c>MessageEnvelope`1[[Inner,…]]</c>), find no active handler, never complete,
/// and livelock on lease-expiry re-claims. Observed live: leftover integrity checkpoints
/// from a build ~60 versions old churning at 28+ attempts.</para>
/// <para>The fix is keyed on the INNER payload type (the wrapper is a known, subscribed
/// type — checking it is the blindness), is pure string work (an unresolvable old-version
/// type must drop, not throw), and discards by COMPLETING through the normal machinery —
/// "process it, but the processing is throwing it away".</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/EventTypeMatchingHelper.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/DisabledSubsystemDiscardPolicy.cs</code-under-test>
[Category("Shard2")]
public sealed class DisabledSubsystemDiscardTests {

  private const string WRAPPED_OLD_CHECKPOINT =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Messaging.IntegrityCheckpoint, "
    + "Whizbang.Core, Version=0.900.0.0, Culture=neutral, PublicKeyToken=null]], Whizbang.Core";

  [Test]
  public async Task ExtractInnerPayloadTypeName_UnwrapsEnvelopeWrappersAsync() {
    var inner = EventTypeMatchingHelper.ExtractInnerPayloadTypeName(WRAPPED_OLD_CHECKPOINT);
    await Assert.That(inner).Contains("Whizbang.Core.Messaging.IntegrityCheckpoint")
      .Because("the wrapper is transport plumbing — every policy decision belongs to the "
             + "payload inside it");
    await Assert.That(inner).DoesNotContain("MessageEnvelope")
      .Because("a check that still sees the wrapper has not unwrapped anything");
  }

  [Test]
  public async Task ExtractInnerPayloadTypeName_PlainTypePassesThroughAsync() {
    const string plain = "MyApp.Orders.OrderPlaced, MyApp";
    await Assert.That(EventTypeMatchingHelper.ExtractInnerPayloadTypeName(plain)).IsEqualTo(plain);
  }

  [Test]
  public async Task ControlPlaneRegistry_SeesThroughTheWrapperAsync() {
    await Assert.That(ControlPlaneTypeRegistry.IsControlPlane(WRAPPED_OLD_CHECKPOINT)).IsTrue()
      .Because("the live livelock was WRAPPED control plane slipping past a check keyed on "
             + "the wrapper type — the registry must judge the inner payload");
  }

  [Test]
  public async Task Policy_DisabledCheckpoints_DiscardsWrappedCheckpointAsync() {
    var options = new StreamIntegrityOptions { CheckpointsEnabled = false };
    await Assert.That(DisabledSubsystemDiscardPolicy.ShouldDiscard(WRAPPED_OLD_CHECKPOINT, options)).IsTrue()
      .Because("a disabled subsystem's leftovers are noise with no handler — retrying them "
             + "to death is the livelock this policy removes");
  }

  [Test]
  public async Task Policy_EnabledSubsystem_NeverDiscardsAsync() {
    var options = new StreamIntegrityOptions { CheckpointsEnabled = true };
    await Assert.That(DisabledSubsystemDiscardPolicy.ShouldDiscard(WRAPPED_OLD_CHECKPOINT, options)).IsFalse()
      .Because("while the subsystem is on, its messages are real traffic — the discard "
             + "must key on configuration, not on the type alone");
  }

  [Test]
  public async Task Policy_DomainMessage_NeverDiscardsAsync() {
    var options = new StreamIntegrityOptions {
      CheckpointsEnabled = false,
      GapDetectionEnabled = false,
      AuditEnabled = false,
    };
    await Assert.That(DisabledSubsystemDiscardPolicy.ShouldDiscard("MyApp.Orders.OrderPlaced, MyApp", options)).IsFalse()
      .Because("only the disabled subsystem's OWN types are eligible — domain traffic is "
             + "never collateral");
  }

  [Test]
  public async Task Policy_UnreadableTypeName_FailsSafeAsync() {
    var options = new StreamIntegrityOptions { CheckpointsEnabled = false };
    await Assert.That(DisabledSubsystemDiscardPolicy.ShouldDiscard("", options)).IsFalse();
    await Assert.That(DisabledSubsystemDiscardPolicy.ShouldDiscard("garbage[[", options)).IsFalse()
      .Because("an unreadable name keeps the message — discard requires a positive match");
  }
}
