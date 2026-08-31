using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// The chain an audit record travels: built, folded into a composite, shipped, fanned out.
/// </summary>
/// <remarks>
/// <para>
/// Audit records reach a consumer as children of an <c>AuditEventsComposite</c>, and a child's
/// stored scope is derived from the composite's hop at fan-out. Three seams have to carry the
/// marker or the record lands unscoped, and none of them had end-to-end coverage.
/// </para>
/// <para>
/// The case pinned here is the one most likely to break: an audit of a CONTROL-PLANE event. Such an
/// event has no tenant, so its audit record's scope is the system marker ALONE — and a scope
/// carrying neither tenant nor user looks empty to anything that decides emptiness by testing those
/// two fields. <c>ScopeDelta.FromPerspectiveScope</c> has exactly that check, with the marker as an
/// explicit exception.
/// </para>
/// <para>
/// These are characterization tests, not a regression fix: all three passed when written. They
/// exist because a deployment showed a burst of unscoped audit records after this fix shipped,
/// which looked like a live defect and turned out to be pre-fix composites draining from an outbox.
/// The chain was never covered end to end, so there was nothing to distinguish those two
/// explanations. Now there is.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditOutboxMessageBuilder.cs</code-under-test>
[Category("SystemEvents")]
public class AuditCompositeScopeChainTests {

  // ---- seam 1: building the audit record for an unscoped (control-plane) source ----

  [Test]
  public async Task Seam1_AuditOfAnUnscopedEventIsStillMarkedSystemAsync() {
    var options = new SystemEventOptions().EnableEventAudit();
    var source = _outboxEvent(scope: null);

    var audit = AuditOutboxMessageBuilder.TryBuildAuditMessage(source, options);

    await Assert.That(audit).IsNotNull()
      .Because("no audit record was produced at all, so the rest of the chain never runs");
    await Assert.That(audit!.Envelope.GetCurrentScope()?.Scope.IsSystem).IsTrue()
      .Because("a control-plane event has no tenant, so the marker alone IS the scope — dropping "
             + "it here leaves the record indistinguishable from one that lost its scope");
  }

  // ---- seam 2: fan-out gives children the composite's scope ----

  [Test]
  public async Task Seam2_ChildrenInheritASystemOnlyCompositeScopeAsync() {
    // The composite's hop carries the marker and nothing else — no tenant, no user.
    var streamId = Guid.CreateVersion7();
    var composite = new AuditEventsComposite {
      StreamId = streamId,
      InnerPayloads = [JsonDocument.Parse("{\"v\":1}").RootElement],
      InnerTypeNames = ["Whizbang.Core.SystemEvents.EventAudited, Whizbang.Core"],
      InnerEventIds = [Guid.CreateVersion7()],
    };
    var source = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { }),
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Scope = AuditRecordScope.For(auditedTenantId: null),
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };

    // Isolate the seam: confirm the SOURCE carries the marker before fan-out is blamed for losing it.
    var sourceScope = source.GetCurrentScope();
    await Assert.That(sourceScope).IsNotNull()
      .Because("if the composite envelope itself cannot report a system-only scope, the loss is in "
             + "scope resolution rather than in fan-out");
    await Assert.That(sourceScope!.Scope.IsSystem).IsTrue()
      .Because("same — this pins which side of the seam the marker disappears on");

    var result = CompositeInboxFanout.TryExpand(composite, source, _provider());

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children).IsNotEmpty();
    await Assert.That(result.Children[0].Scope?.IsSystem).IsTrue()
      .Because("this value becomes the child's stored scope column; a system-only scope that is "
             + "dropped here is written to disk as null and reads as a lost scope forever after");
  }


  // ---- seam 3: the coalesce fold, which builds the composite the consumer fans out ----

  [Test]
  public async Task Seam3_TheFoldedCompositeKeepsASystemOnlyScopeAsync() {
    // CoalesceShipWorker groups singles by scope and stamps the group's scope on the composite hop.
    // A system-only scope carries no tenant and no user, so anything that decides "empty" by
    // testing those two fields drops it here — and the composite is what the consumer's fan-out
    // reads to scope every child.
    var single = AuditOutboxMessageBuilder.TryBuildAuditMessage(_outboxEvent(scope: null),
      new SystemEventOptions().EnableEventAudit());

    await Assert.That(single).IsNotNull();
    var singleScope = single!.Metadata.Hops.Count > 0 ? single.Metadata.Hops[0].Scope : null;

    await Assert.That(singleScope).IsNotNull()
      .Because("the fold reads the scope off the single's METADATA hops; if the marker is only on "
             + "the envelope hops it is invisible to grouping and to the composite it produces");
    await Assert.That(singleScope!.ApplyTo(null).Scope.IsSystem).IsTrue()
      .Because("this exact delta becomes the composite's hop scope, and from there every child's "
             + "stored scope — losing it here is what writes a null scope to disk");
  }

  private static OutboxMessage _outboxEvent(PerspectiveScope? scope) {
    var hop = new MessageHop {
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
    };
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { v = 1 }),
      Hops = [hop],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [hop] },
      EnvelopeType = "T",
      StreamId = Guid.CreateVersion7(),
      IsEvent = true,
      MessageType = "Whizbang.Core.Messaging.PerspectiveCoverageGapDetected, Whizbang.Core",
      Scope = scope,
    };
  }

  private static Microsoft.Extensions.DependencyInjection.ServiceProvider _provider() =>
    new Microsoft.Extensions.DependencyInjection.ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new _fakeSerializer())
      .BuildServiceProvider();

  /// <summary>Minimal serializer: fan-out needs one registered, and only the hops matter here.</summary>
  private sealed class _fakeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var aqn = envelope.Payload!.GetType().AssemblyQualifiedName!;
      return new SerializedEnvelope(
        JsonEnvelope: new MessageEnvelope<JsonElement> {
          DispatchContext = envelope.DispatchContext,
          MessageId = envelope.MessageId,
          Payload = JsonSerializer.SerializeToElement(new { }),
          Hops = envelope.Hops?.ToList() ?? [],
        },
        EnvelopeType: $"Whizbang.Core.Observability.MessageEnvelope`1[[{aqn}]], Whizbang.Core",
        MessageType: aqn);
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }
}
