using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Minting;

/// <summary>
/// Rename-compatibility locks for the minted composite family's move from
/// <c>Whizbang.Core.Messaging</c> / <c>Whizbang.Core.SystemEvents</c> into
/// <c>Whizbang.Core.Minting</c> (topology arc phase 4). Persisted rows
/// (wh_outbox / wh_inbox / wh_event_store) and in-flight wire envelopes written BEFORE the move
/// carry the OLD CLR names in their <c>EnvelopeType</c> / <c>MessageType</c> strings — the
/// pinned-type ledger's <c>formerNames</c> must keep every one of them resolving to the moved
/// type, or a deploy strands every undelivered composite with no dead-letter and no recovery.
/// </summary>
/// <code-under-test>src/Whizbang.Core/.whizbang/pinned-type-ledger.json</code-under-test>
/// <code-under-test>src/Whizbang.Core/EventMarkerResolver.cs</code-under-test>
public class MintedTypeRenameCompatibilityTests {
  private const string OLD_REDELIVERY = "Whizbang.Core.Messaging.RedeliveryComposite, Whizbang.Core";
  private const string OLD_COALESCED = "Whizbang.Core.Messaging.CoalescedEventsComposite, Whizbang.Core";
  private const string OLD_AUDIT = "Whizbang.Core.SystemEvents.AuditEventsComposite, Whizbang.Core";

  private static string _envelopeForm(string inner) =>
    $"Whizbang.Core.Observability.MessageEnvelope`1[[{inner}]], Whizbang.Core";

  // ── Old-name type resolution (the ledger's formerNames → RegisterTypeName aliases) ────────

  [Test]
  [Arguments(OLD_REDELIVERY, typeof(RedeliveryComposite))]
  [Arguments(OLD_COALESCED, typeof(CoalescedEventsComposite))]
  [Arguments(OLD_AUDIT, typeof(AuditEventsComposite))]
  public async Task GetTypeInfoByName_OldBareName_ResolvesToMovedTypeAsync(string oldName, Type movedType) {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var typeInfo = JsonContextRegistry.GetTypeInfoByName(oldName, options);

    await Assert.That(typeInfo).IsNotNull()
      .Because("rows persisted under the pre-move CLR name must keep resolving after the "
             + "namespace move — the ledger's formerNames emit alias RegisterTypeName calls");
    await Assert.That(typeInfo!.Type).IsEqualTo(movedType);
  }

  [Test]
  [Arguments(OLD_REDELIVERY, typeof(MessageEnvelope<RedeliveryComposite>))]
  [Arguments(OLD_COALESCED, typeof(MessageEnvelope<CoalescedEventsComposite>))]
  [Arguments(OLD_AUDIT, typeof(MessageEnvelope<AuditEventsComposite>))]
  public async Task GetTypeInfoByName_OldEnvelopeName_ResolvesToMovedEnvelopeTypeAsync(string oldName, Type movedEnvelopeType) {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var typeInfo = JsonContextRegistry.GetTypeInfoByName(_envelopeForm(oldName), options);

    await Assert.That(typeInfo).IsNotNull()
      .Because("wire envelopes and stored EnvelopeType strings carry the MessageEnvelope`1 form "
             + "of the pre-move name — the alias covers both shapes");
    await Assert.That(typeInfo!.Type).IsEqualTo(movedEnvelopeType);
  }

  // ── Old-name deserialization (the inbox-row seam: EnvelopeSerializer.DeserializeMessage) ──

  [Test]
  public async Task DeserializeMessage_InboxRowWithOldMessageType_LandsOnMovedTypeAsync() {
    // An inbox row written BEFORE the move: its MessageType column says
    // "Whizbang.Core.Messaging.RedeliveryComposite, Whizbang.Core" while its payload JSON is the
    // composite's wire form. The dispatch seam resolves the stored string through
    // EnvelopeSerializer.DeserializeMessage — it must land on the moved type with the raw-carry
    // fields intact.
    var options = JsonContextRegistry.CreateCombinedOptions();
    var serializer = new EnvelopeSerializer(options);
    var streamId = Guid.NewGuid();
    var innerId = Guid.NewGuid();
    using var innerDoc = JsonDocument.Parse(/*lang=json,strict*/ "{\"seeded\":true}");
    var composite = new RedeliveryComposite {
      StreamId = streamId,
      InnerPayloads = [innerDoc.RootElement.Clone()],
      InnerTypeNames = ["Contracts.ProbeHappened, Contracts"],
      InnerEventIds = [innerId],
    };
    var typedEnvelope = new MessageEnvelope<RedeliveryComposite> {
      MessageId = MessageId.New(),
      Payload = composite,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    var serialized = serializer.SerializeEnvelope(typedEnvelope);

    var message = serializer.DeserializeMessage(serialized.JsonEnvelope, OLD_REDELIVERY);

    var roundTripped = (RedeliveryComposite)message;
    await Assert.That(roundTripped.StreamId).IsEqualTo(streamId);
    await Assert.That(roundTripped.InnerEventIds).IsEquivalentTo([innerId])
      .Because("identity preservation must survive an old-name resolution — the ids are what "
             + "make redelivery convergence idempotent");
    await Assert.That(roundTripped.InnerPayloads[0].GetRawText()).IsEqualTo(/*lang=json,strict*/ "{\"seeded\":true}");
    await Assert.That(roundTripped.InnerTypeNames).IsEquivalentTo(["Contracts.ProbeHappened, Contracts"]);
  }

  // ── Old-name marker resolution (EventFlags derivation from catalog formerNames) ───────────

  [Test]
  [Arguments("Whizbang.Core.Messaging.RedeliveryComposite")]
  [Arguments("Whizbang.Core.Messaging.CoalescedEventsComposite")]
  [Arguments("Whizbang.Core.SystemEvents.AuditEventsComposite")]
  public async Task EventMarkerResolver_OldClrName_ResolvesCompositeFlagAsync(string oldClrName) {
    // The receive-path discard gates and the flag deriver look composites up by CLR name against
    // the generated catalog. An in-flight or stored row still naming the OLD namespace must keep
    // resolving EventFlags.Composite, or the no-consumer gates drop the whole bundle silently.
    var resolver = new EventMarkerResolver(new Whizbang.Core.Generated.GeneratedMessageTypeCatalog());

    var flags = resolver.Resolve(oldClrName);

    await Assert.That(flags is not null).IsTrue()
      .Because("the catalog carries formerNames from the pinned-type ledger — the resolver must "
             + "index them as alias keys");
    await Assert.That(flags!.Value.HasFlag(EventFlags.Composite)).IsTrue();
  }

  [Test]
  public async Task EventMarkerResolver_NewClrName_StillResolvesCompositeFlagAsync() {
    // Guard: former-name indexing must not displace the current-name lookup.
    var resolver = new EventMarkerResolver(new Whizbang.Core.Generated.GeneratedMessageTypeCatalog());

    var flags = resolver.Resolve("Whizbang.Core.Minting.RedeliveryComposite");

    await Assert.That(flags is not null).IsTrue();
    await Assert.That(flags!.Value.HasFlag(EventFlags.Composite)).IsTrue();
  }
}
