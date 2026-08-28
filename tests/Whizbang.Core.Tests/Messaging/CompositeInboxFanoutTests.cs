#pragma warning disable CA1707

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the dispatch-time fan-out contract (<see cref="CompositeInboxFanout"/>): a composite event
/// arriving as an inbox row expands into N child inbox messages, each carrying the inner event,
/// inheriting the composite's identity context, with a fresh MessageId. Cap / expansion failures are
/// returned (not thrown) so the dispatch worker can dead-letter the composite row. The real JSON
/// serialization is covered by EnvelopeSerializerTests + JsonContextRegistryTests; here a fake
/// serializer isolates the fan-out orchestration logic.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#dispatch-fanout</docs>
[Category("Messaging")]
public class CompositeInboxFanoutTests {

  [Test]
  public async Task TryExpand_NonComposite_ReturnsNotCompositeAsync() {
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite: null, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.NotComposite);
    await Assert.That(result.Children).IsEmpty();
  }

  [Test]
  public async Task TryExpand_YieldsOneChildInboxMessagePerInnerAsync() {
    var streamId = Guid.NewGuid();
    var composite = new _testComposite(new _innerEvent("J-001"), new _innerEvent("J-002"), new _innerEvent("J-003"));
    var source = _sourceEnvelope(streamId);
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(3);
    // Each child's MessageType is the concrete inner event's assembly-qualified name.
    await Assert.That(result.Children.All(c => c.MessageType.Contains("_innerEvent", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task TryExpand_ChildrenInheritCompositeStreamIdFromHopsAsync() {
    var streamId = Guid.NewGuid();
    var composite = new _testComposite(new _innerEvent("X"));
    var source = _sourceEnvelope(streamId);
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    var child = result.Children.Single();
    await Assert.That(child.StreamId).IsEqualTo(streamId)
      .Because("Inner events inherit the composite's stream — the first hop's AggregateId is the composite StreamId.");
  }

  [Test]
  public async Task TryExpand_AssignsFreshDistinctMessageIdsPerChildAsync() {
    var composite = new _testComposite(new _innerEvent("A"), new _innerEvent("B"));
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Children[0].MessageId).IsNotEqualTo(result.Children[1].MessageId);
    await Assert.That(result.Children[0].MessageId).IsNotEqualTo(source.MessageId.Value)
      .Because("Children must not collide with the composite's MessageId or each other — inbox dedup keeps them distinct.");
  }

  [Test]
  public async Task TryExpand_IdentityPreservingComposite_ChildrenCarryProvidedIdsAsync() {
    // Re-delivery bundles carry PREVIOUSLY PERSISTED events: their original ids are what make
    // consumer convergence free (event-id conflict skip). A fresh fan-out id would append a
    // duplicate instead of skipping an already-present event.
    var streamId = Guid.NewGuid();
    var idA = Guid.NewGuid();
    var idB = Guid.NewGuid();
    var composite = new RedeliveryComposite {
      StreamId = streamId,
      InnerPayloads = [_raw("{\"v\":\"A\"}"), _raw("{\"v\":\"B\"}")],
      InnerTypeNames = ["Contracts.Repaired, Contracts", "Contracts.Repaired, Contracts"],
      InnerEventIds = [idA, idB],
    };
    var source = _sourceEnvelope(streamId);
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(2);
    await Assert.That(result.Children[0].MessageId).IsEqualTo(idA)
      .Because("an identity-preserving composite's children must keep their caller-supplied " +
               "(original) ids — identity is what makes re-delivery idempotent at consumers.");
    await Assert.That(result.Children[1].MessageId).IsEqualTo(idB);
  }

  [Test]
  public async Task TryExpand_IdentityComposite_ChildrenCarryOriginIdentityAsync() {
    // Stream-integrity Phase B: windowed integrity accounting keys on each event's ORIGIN identity
    // (origin service + origin commit sequence). Re-delivered children must carry the ORIGINALS —
    // under the bundle's own identity a repaired window would never recount as filled.
    var streamId = Guid.NewGuid();
    var origin = Guid.NewGuid();
    var composite = new RedeliveryComposite {
      StreamId = streamId,
      InnerPayloads = [_raw("{\"v\":\"A\"}"), _raw("{\"v\":\"B\"}"), _raw("{\"v\":\"C\"}")],
      InnerTypeNames = ["Contracts.Repaired, Contracts", "Contracts.Repaired, Contracts", "Contracts.Repaired, Contracts"],
      InnerEventIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
      OriginServiceId = origin,
      InnerCommitSequences = [10, 11, null],
    };
    var source = _sourceEnvelope(streamId);
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children[0].SourceServiceId).IsEqualTo(origin)
      .Because("the bundle names the ORIGIN the events were emitted by — children carry it, not " +
               "the repair bundle's own source identity.");
    await Assert.That(result.Children[1].SourceServiceId).IsEqualTo(origin);
    await Assert.That(result.Children[0].SourceCommitSequence).IsEqualTo(10L);
    await Assert.That(result.Children[1].SourceCommitSequence).IsEqualTo(11L);
    await Assert.That(result.Children[2].SourceCommitSequence).IsEqualTo(source.SourceCommitSequence)
      .Because("a null entry means the event predates commit-sequence stamping — fall back to the " +
               "composite envelope's value rather than inventing one.");
  }

  [Test]
  public async Task TryExpand_IdentityComposite_SequenceCountMismatch_FailsAsync() {
    // Strict, mirroring the id pairing: a machine-built bundle with desynchronized sequences is a
    // producer bug — fail the whole expansion rather than misattribute origin windows.
    var composite = new RedeliveryComposite {
      StreamId = Guid.NewGuid(),
      InnerPayloads = [_raw("{\"v\":\"A\"}"), _raw("{\"v\":\"B\"}")],
      InnerTypeNames = ["Contracts.Repaired, Contracts", "Contracts.Repaired, Contracts"],
      InnerEventIds = [Guid.NewGuid(), Guid.NewGuid()],
      InnerCommitSequences = [10],
    };
    var source = _sourceEnvelope(composite.StreamId);
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Failed);
    await Assert.That(result.Children).IsEmpty();
  }

  [Test]
  public async Task TryExpand_IdentityPreservingComposite_IdCountMismatch_FailsAsync() {
    // Strict: a machine-built repair bundle with desynchronized ids/inners is a producer bug —
    // fail the whole expansion (DLQ route) rather than guess at the pairing.
    var composite = new RedeliveryComposite {
      StreamId = Guid.NewGuid(),
      InnerPayloads = [_raw("{\"v\":\"A\"}"), _raw("{\"v\":\"B\"}")],
      InnerTypeNames = ["Contracts.Repaired, Contracts", "Contracts.Repaired, Contracts"],
      InnerEventIds = [Guid.NewGuid()],
    };
    var source = _sourceEnvelope(composite.StreamId);
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Failed);
    await Assert.That(result.Children).IsEmpty();
  }

  [Test]
  public async Task TryExpand_ChildrenAreMarkedAsEventsAsync() {
    var composite = new _testComposite(new _innerEvent("E"));
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Children.Single().IsEvent).IsTrue()
      .Because("The inner events implement IEvent, so the child inbox rows persist to the event store.");
  }

  [Test]
  public async Task TryExpand_ChildrenCarryCompositeLineage_CausationIsCompositeMessageIdAsync() {
    // Each child's creation hop must point back to the parent composite so "these events came from
    // composite X" is queryable off the event-store rows (Hops[0].CausationId / CausationType).
    var composite = new _testComposite(new _innerEvent("J-1"), new _innerEvent("J-2"));
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Children.Count).IsEqualTo(2);
    foreach (var child in result.Children) {
      var hop0 = child.Metadata!.Hops[0];
      await Assert.That(hop0.CausationId).IsEqualTo(source.MessageId)
        .Because("the child's creation hop is caused by the composite — CausationId is the composite's MessageId.");
      await Assert.That(hop0.CausationType).IsEqualTo(nameof(_testComposite))
        .Because("CausationType records that the cause was this composite type.");
    }
    // All children of one composite share the same causation → groupable as one batch.
    await Assert.That(result.Children[0].Metadata!.Hops[0].CausationId)
      .IsEqualTo(result.Children[1].Metadata!.Hops[0].CausationId);
  }

  [Test]
  public async Task TryExpand_ChildrenCarryNoRebroadcastFlagAsync() {
    var composite = new _testComposite(new _innerEvent("J-1"), new _innerEvent("J-2"));
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Children.All(c => (c.Flags & EventFlags.NoRebroadcast) != 0)).IsTrue()
      .Because("Every fan-out child is stamped NoRebroadcast so the outbox-enqueue boundary can drop any re-broadcast.");
  }

  [Test]
  public async Task TryExpand_CollectiveInnerEvent_ChildKeepsCollectiveFlagAsync() {
    // A collective event carried INSIDE a composite must behave on the receiving service exactly as
    // a locally-emitted collective event would: its child inbox row needs EventFlags.Collective or
    // the inbox emit chain never routes it to the collective sink. NoRebroadcast must still be
    // present — deriving the inner event's real flags never replaces the fan-out containment marker.
    var composite = new _testComposite(new _collectiveInnerEvent(new TenantCollectiveScope("tenant-1"), []));
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Children.Count).IsEqualTo(1);
    await Assert.That(result.Children[0].Flags & EventFlags.Collective).IsEqualTo(EventFlags.Collective)
      .Because("the child of a composite keeps the inner event's own marker flags — a collective " +
               "inner event that loses Collective is silently never applied on the receiving service.");
    await Assert.That(result.Children[0].Flags & EventFlags.NoRebroadcast).IsEqualTo(EventFlags.NoRebroadcast);
  }

  [Test]
  public async Task TryExpand_OverCap_ReturnsCapExceededAsync() {
    var inners = Enumerable.Range(0, 11).Select(i => new _innerEvent($"i-{i}")).ToArray();
    var composite = new _testComposite(inners) { MaxInnerEventsAllowedOverride = 10 };
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.CapExceeded);
    await Assert.That(result.Children).IsEmpty()
      .Because("No partial fan-out — a cap breach dead-letters the whole composite.");
    await Assert.That(result.CompositeTypeName).IsNotNull();
  }

  [Test]
  public async Task TryExpand_NullInner_Atomic_ReturnsFailedAsync() {
    var composite = new _nullYieldingComposite { AtomicityOverride = FanoutAtomicity.Atomic };
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Failed)
      .Because("Atomic: any bad child sinks the whole composite.");
    await Assert.That(result.Children).IsEmpty();
  }

  [Test]
  public async Task TryExpand_NullInner_Independent_DropsBadChildAndKeepsRestAsync() {
    // Independent (default): a null inner is dropped; the valid inner still fans out.
    var composite = new _mixedNullComposite();
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(1)
      .Because("Independent: one bad child doesn't sink the batch — the good child survives.");
  }

  [Test]
  public async Task TryExpand_NullInner_Independent_LogsTheDroppedChildAsync() {
    // Independent mode drops a bad child, but the drop must be LOGGED — a partial fan-out that
    // silently reports Expanded is invisible message loss (the swallow-audit finding).
    var captured = new _capturingLogger();
    var composite = new _mixedNullComposite();
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _providerWithLogger(captured);

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(1);
    await Assert.That(captured.Entries.Any(e => e.Level == LogLevel.Warning)).IsTrue()
      .Because("a dropped inner event must be logged, not silently swallowed");
  }

  [Test]
  public async Task TryExpand_ReplacementInner_FansOutTheReplacementSetAsync() {
    // A pre-fanout ReplaceWith directive supplies the children to fan out instead of InnerEvents.
    var composite = new _testComposite(new _innerEvent("original"));
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();
    var replacement = new IMessage[] { new _innerEvent("R-1"), new _innerEvent("R-2") };

    var result = CompositeInboxFanout.TryExpand(composite, source, sp, replacement);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(2)
      .Because("The replacement set (2) is fanned out, not the composite's own InnerEvents (1).");
  }

  [Test]
  public async Task TryExpand_RawComposite_ChildrenBuiltFromRawPayloadsAsync() {
    // Raw carry: no typed payloads exist on either side — the child inbox row is built DIRECTLY
    // from the stored wire JSON and wire type name, with no serializer on the path.
    var streamId = Guid.NewGuid();
    var idA = Guid.NewGuid();
    var idB = Guid.NewGuid();
    var composite = new RedeliveryComposite {
      StreamId = streamId,
      InnerPayloads = [_raw("{\"tags\":[\"a\",\"b\"]}"), _raw("{\"n\":7}")],
      InnerTypeNames = ["Contracts.RepairedA, Contracts", "Contracts.RepairedB, Contracts"],
      InnerEventIds = [idA, idB],
    };
    var source = _sourceEnvelope(streamId);
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(2);
    await Assert.That(result.Children[0].Envelope.Payload.GetRawText()).IsEqualTo("{\"tags\":[\"a\",\"b\"]}")
      .Because("the payload bytes that were emitted are the payload bytes that repair — verbatim, " +
               "no rehydration, no polymorphic metadata for arbitrary consumer shapes.");
    await Assert.That(result.Children[0].MessageType).IsEqualTo("Contracts.RepairedA, Contracts");
    await Assert.That(result.Children[0].EnvelopeType)
      .IsEqualTo("Whizbang.Core.Observability.MessageEnvelope`1[[Contracts.RepairedA, Contracts]], Whizbang.Core")
      .Because("the child envelope type composes from the carried wire name — same shape the " +
               "serializer would have produced from a typed payload.");
    await Assert.That(result.Children[1].MessageType).IsEqualTo("Contracts.RepairedB, Contracts");
    await Assert.That(result.Children[0].Flags.HasFlag(EventFlags.NoRebroadcast)).IsTrue();
  }

  [Test]
  public async Task TryExpand_AuditEventsComposite_DeliversEachInnerEventAuditedAsync() {
    // The batched audit shipper folds pending EventAudited singles into one AuditEventsComposite
    // (raw carry + identity preservation — the singles' payloads are already wire JSON, and their
    // original message ids make a fold/deadline race dedup at the consumer's inbox instead of
    // double-recording). The fan-out must deliver each inner as its own child inbox message: an
    // ISystemEvent inner expands exactly like a domain-event inner.
    var streamId = Guid.NewGuid();
    var auditWireType = typeof(Whizbang.Core.SystemEvents.EventAudited).AssemblyQualifiedName!;
    var idA = Guid.NewGuid();
    var idB = Guid.NewGuid();
    var composite = new Whizbang.Core.Minting.AuditEventsComposite {
      StreamId = streamId,
      InnerPayloads = [
        _raw("{\"Id\":\"" + idA + "\",\"OriginalEventType\":\"Contracts.SomethingHappened\"}"),
        _raw("{\"Id\":\"" + idB + "\",\"OriginalEventType\":\"Contracts.SomethingElseHappened\"}"),
      ],
      InnerTypeNames = [auditWireType, auditWireType],
      InnerEventIds = [idA, idB],
    };
    var source = _sourceEnvelope(streamId);
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(2);
    await Assert.That(result.Children[0].MessageType).IsEqualTo(auditWireType)
      .Because("each child must be a first-class EventAudited delivery — same wire type the " +
               "per-event singles would have carried.");
    await Assert.That(result.Children[0].MessageId).IsEqualTo(idA)
      .Because("identity preservation: a single that also shipped individually at the deadline " +
               "must dedup at the consumer's inbox, not double-record.");
    await Assert.That(result.Children[1].MessageId).IsEqualTo(idB);
    await Assert.That(result.Children[0].Envelope.Payload.GetProperty("OriginalEventType").GetString())
      .IsEqualTo("Contracts.SomethingHappened")
      .Because("raw carry: the audit record's stored wire JSON rides verbatim — no rehydration.");
  }

  [Test]
  public async Task AuditEventsComposite_IsNonAtomic_OneBadRecordMustNotSinkSiblingsAsync() {
    // Audit records are independent facts; a poison record must dead-letter alone, never take its
    // siblings with it. Lock the carrier's atomicity so a future refactor cannot flip it.
    var composite = new Whizbang.Core.Minting.AuditEventsComposite();

    await Assert.That(composite.Atomicity).IsEqualTo(FanoutAtomicity.Independent);
  }

  [Test]
  public async Task TryExpand_CoalescedEventsComposite_DeliversEachInnerAsync() {
    // The GENERIC coalesce carrier (the default a coalesce binding ships with) must expand
    // exactly like the audit-specific one: raw carry, identity preservation, one first-class
    // child per folded single.
    var streamId = Guid.NewGuid();
    var wireType = "Contracts.RecordCaptured, Contracts";
    var idA = Guid.NewGuid();
    var idB = Guid.NewGuid();
    var composite = new CoalescedEventsComposite {
      StreamId = streamId,
      InnerPayloads = [
        _raw("{\"Name\":\"first\"}"),
        _raw("{\"Name\":\"second\"}"),
      ],
      InnerTypeNames = [wireType, wireType],
      InnerEventIds = [idA, idB],
    };
    var source = _sourceEnvelope(streamId);
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(2);
    await Assert.That(result.Children[0].MessageType).IsEqualTo(wireType);
    await Assert.That(result.Children[0].MessageId).IsEqualTo(idA)
      .Because("identity preservation: a single that raced its floor dedups at the consumer's inbox");
    await Assert.That(result.Children[1].MessageId).IsEqualTo(idB);
    await Assert.That(result.Children[0].Envelope.Payload.GetProperty("Name").GetString()).IsEqualTo("first")
      .Because("raw carry: the folded single's stored wire JSON rides verbatim — no rehydration");
  }

  [Test]
  public async Task CoalescedEventsComposite_DefaultsToIndependentAtomicityAsync() {
    // Coalesce groups bundle self-contained records; the binding may opt into Atomic, but the
    // carrier's default must stay Independent so one poison inner dead-letters alone.
    var composite = new CoalescedEventsComposite();

    await Assert.That(composite.Atomicity).IsEqualTo(FanoutAtomicity.Independent);
  }

  [Test]
  public async Task TryExpand_RawComposite_TypeNameCountMismatch_FailsAsync() {
    var composite = new RedeliveryComposite {
      StreamId = Guid.NewGuid(),
      InnerPayloads = [_raw("{}"), _raw("{}")],
      InnerTypeNames = ["Contracts.OnlyOne, Contracts"],
      InnerEventIds = [Guid.NewGuid(), Guid.NewGuid()],
    };

    var result = CompositeInboxFanout.TryExpand(composite, _sourceEnvelope(Guid.NewGuid()), _provider());

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Failed)
      .Because("raw bundles are machine-built — a payload/type-name desync is a producer bug and " +
               "guessing at the pairing would mislabel repaired events.");
  }

  [Test]
  public async Task TryExpand_RawComposite_OverCap_ReturnsCapExceededAsync() {
    var composite = new RedeliveryComposite {
      StreamId = Guid.NewGuid(),
      InnerPayloads = [_raw("{}"), _raw("{}"), _raw("{}")],
      InnerTypeNames = ["T, A", "T, A", "T, A"],
      InnerEventIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
      MaxInnerEventsAllowed = 2,
    };

    var result = CompositeInboxFanout.TryExpand(composite, _sourceEnvelope(Guid.NewGuid()), _provider());

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.CapExceeded);
  }

  // ============================================================
  // Fakes + helpers
  // ============================================================

  private static JsonElement _raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

  // ── Hypothesis battery: malformed raw composites must FAIL, never THROW ──────────────────────
  // The dispatch path handles a RETURNED failure correctly (dead-letter, then a terminal completion
  // so the row never re-claims). A THROWN failure takes a different path, so any shape that throws
  // instead of returning is a candidate for leaving a composite unprocessed and re-claimed forever —
  // the shape of a production incident where composites sat at status=1 with attempts climbing.
  // The type documents its parallel-array contract as STRICT and a mismatch as "a producer bug,
  // never data"; a producer bug in the wild still has to fail safely.

  [Test]
  public async Task TryExpand_RawComposite_MoreTypeNamesThanPayloads_FailsWithoutThrowingAsync() {
    var composite = new RedeliveryComposite {
      StreamId = Guid.NewGuid(),
      InnerPayloads = [_raw("{\"n\":1}")],
      InnerTypeNames = ["Contracts.A, Contracts", "Contracts.B, Contracts"],
      InnerEventIds = [Guid.NewGuid(), Guid.NewGuid()],
    };

    var result = CompositeInboxFanout.TryExpand(composite, _sourceEnvelope(composite.StreamId), _provider());

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Failed)
      .Because("a desynced parallel array must be REPORTED so the caller dead-letters the row; a "
             + "throw takes a different path and is how a composite ends up neither completed nor "
             + "dead-lettered, re-claimed on every lease expiry");
  }

  [Test]
  public async Task TryExpand_RawComposite_MorePayloadsThanTypeNames_FailsWithoutThrowingAsync() {
    var composite = new RedeliveryComposite {
      StreamId = Guid.NewGuid(),
      InnerPayloads = [_raw("{\"n\":1}"), _raw("{\"n\":2}")],
      InnerTypeNames = ["Contracts.A, Contracts"],
      InnerEventIds = [Guid.NewGuid(), Guid.NewGuid()],
    };

    var result = CompositeInboxFanout.TryExpand(composite, _sourceEnvelope(composite.StreamId), _provider());

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Failed)
      .Because("desync in the other direction must fail identically — an asymmetry means one of the "
             + "two orderings strands the row");
  }

  [Test]
  public async Task TryExpand_RawComposite_EmptyTypeName_FailsWithoutThrowingAsync() {
    var composite = new RedeliveryComposite {
      StreamId = Guid.NewGuid(),
      InnerPayloads = [_raw("{\"n\":1}")],
      InnerTypeNames = [""],
      InnerEventIds = [Guid.NewGuid()],
    };

    var result = CompositeInboxFanout.TryExpand(composite, _sourceEnvelope(composite.StreamId), _provider());

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Failed)
      .Because("an empty wire type name cannot address a receptor — the child would be undeliverable, "
             + "so the composite must fail loudly rather than emit an unroutable row");
  }

  [Test]
  public async Task TryExpand_RawComposite_NoInnerEvents_StillReachesADecisionAsync() {
    var composite = new RedeliveryComposite {
      StreamId = Guid.NewGuid(),
      InnerPayloads = [],
      InnerTypeNames = [],
      InnerEventIds = [],
    };

    var result = CompositeInboxFanout.TryExpand(composite, _sourceEnvelope(composite.StreamId), _provider());

    // Whatever the verdict, it must be a DECISION the dispatch path can act on. NotComposite would
    // send an actual composite down the non-composite branch, which commits nothing for a row that
    // has no payload to handle — the shape of a row that sits and is re-claimed.
    await Assert.That(result.Outcome).IsNotEqualTo(CompositeInboxFanout.FanoutOutcome.NotComposite)
      .Because("an empty composite is still a composite; misrouting it leaves the row uncommitted");
  }

  private static ServiceProvider _provider() =>
    new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new _fakeSerializer())
      .BuildServiceProvider();

  private static ServiceProvider _providerWithLogger(_capturingLogger captured) =>
    new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new _fakeSerializer())
      .AddLogging(b => b.AddProvider(new _capturingLoggerProvider(captured)))
      .BuildServiceProvider();

  /// <summary>Captures log entries emitted during fan-out for assertions.</summary>
  private sealed class _capturingLogger {
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
  }

  private sealed class _capturingLoggerProvider(_capturingLogger captured) : ILoggerProvider {
    public ILogger CreateLogger(string categoryName) => new _sink(captured);
    public void Dispose() { }

    private sealed class _sink(_capturingLogger captured) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => captured.Entries.Add((logLevel, formatter(state, exception), exception));
    }
  }

  /// <summary>
  /// A source inbox envelope whose first hop carries the composite's StreamId as AggregateId — the
  /// shape <c>_extractStreamId</c> reads to inherit the stream onto each child.
  /// </summary>
  [Test]
  public async Task TryExpand_ChildrenInheritTheCompositesSecurityScopeAsync() {
    // Pins existing behavior rather than fixing it, because the whole repair chain rests on it.
    // Fan-out is where a bundle's scope becomes each child's PERSISTED scope: the child's stored
    // scope column and its lineage hop are both derived from the composite's hop here. If that
    // derivation ever silently stops, the children are written unscoped and no later read can
    // recover them -- a perspective requiring a security context rejects every one until it parks.
    // The producers were fixed to carry the scope this far; this locks the other half.
    var streamId = Guid.NewGuid();
    var composite = new RedeliveryComposite {
      StreamId = streamId,
      InnerPayloads = [_raw("{\"v\":\"A\"}")],
      InnerTypeNames = ["Contracts.Repaired, Contracts"],
      InnerEventIds = [Guid.NewGuid()],
    };
    var source = _scopedSourceEnvelope(streamId, "tenant-a", "user-a");

    var result = CompositeInboxFanout.TryExpand(composite, source, _provider());

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(1);

    var child = result.Children[0];
    await Assert.That(child.Scope).IsNotNull()
      .Because("this value becomes the child's scope COLUMN in the event store; a null here is "
             + "written to disk and is indistinguishable from an event that never had a scope");
    await Assert.That(child.Scope!.TenantId).IsEqualTo("tenant-a");
    await Assert.That(child.Metadata!.Hops[0].Scope).IsNotNull()
      .Because("the lineage hop must carry it too — the security extractor reads the hop chain, "
             + "not the column");
  }

  private static MessageEnvelope<JsonElement> _scopedSourceEnvelope(Guid streamId, string tenantId, string userId) {
    var source = _sourceEnvelope(streamId);
    return new MessageEnvelope<JsonElement> {
      DispatchContext = source.DispatchContext,
      MessageId = source.MessageId,
      Payload = source.Payload,
      Hops = [source.Hops![0] with {
        Scope = Whizbang.Core.Security.ScopeDelta.FromPerspectiveScope(
          new Whizbang.Core.Lenses.PerspectiveScope { TenantId = tenantId, UserId = userId }),
      }],
      SourceServiceId = source.SourceServiceId,
      SourceCommitSequence = source.SourceCommitSequence,
    };
  }

  private static MessageEnvelope<JsonElement> _sourceEnvelope(Guid streamId) {
    var aggregateMeta = new Dictionary<string, JsonElement> {
      ["AggregateId"] = JsonSerializer.SerializeToElement(streamId.ToString()),
    };
    return new MessageEnvelope<JsonElement> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { }),
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Metadata = aggregateMeta,
      }],
      SourceServiceId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
      SourceCommitSequence = 42,
    };
  }

  /// <summary>
  /// Minimal serializer: records the payload's runtime AQN as MessageType and produces a JsonElement
  /// envelope. The real serializer is tested elsewhere — this isolates fan-out orchestration.
  /// </summary>
  private sealed class _fakeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var payloadType = envelope.Payload!.GetType();
      var aqn = payloadType.AssemblyQualifiedName!;
      var jsonEnv = new MessageEnvelope<JsonElement> {
        DispatchContext = envelope.DispatchContext,
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Hops = envelope.Hops?.ToList() ?? [],
      };
      return new SerializedEnvelope(
        JsonEnvelope: jsonEnv,
        EnvelopeType: $"Whizbang.Core.Observability.MessageEnvelope`1[[{aqn}]], Whizbang.Core",
        MessageType: aqn);
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private sealed record _innerEvent(string Id) : IEvent;
  private sealed record _collectiveInnerEvent(CollectiveScope Scope, IReadOnlyList<Guid> MatchedStreamIds) : ICollectiveEvent;

  private sealed class _nullYieldingComposite : ICompositeEvent {
    public int MaxInnerEventsAllowed => 10;
    public FanoutAtomicity AtomicityOverride { get; init; } = FanoutAtomicity.Independent;
    public FanoutAtomicity Atomicity => AtomicityOverride;
    public IEnumerable<IMessage> InnerEvents {
      get {
        yield return null!;
      }
    }
  }

  private sealed class _mixedNullComposite : ICompositeEvent {
    public int MaxInnerEventsAllowed => 10;
    // Default Atomicity (Independent) via the interface default-impl.
    public IEnumerable<IMessage> InnerEvents {
      get {
        yield return null!;
        yield return new _innerEvent("good");
      }
    }
  }

  private sealed class _testComposite : ICompositeEvent {
    public _testComposite(params IEvent[] inner) {
      _inner = inner;
    }
    private readonly IEvent[] _inner;
    public int? MaxInnerEventsAllowedOverride { get; init; }
    public int MaxInnerEventsAllowed => MaxInnerEventsAllowedOverride ?? 10_000;
    public IEnumerable<IMessage> InnerEvents => _inner;
  }

  // ---------------------------------------------------------------------------------------------
  // IsCompositeWireType — the receive-boundary lookup that keeps composites past the
  // "no local consumer" gates. A composite is wire-only, so nothing registers a consumer for the
  // composite type itself; recognition has to come from the compile-time catalog, by type name,
  // because the payload is still an undeserialized JsonElement at the gate.
  // ---------------------------------------------------------------------------------------------

  private sealed class _compositeMarker;
  private sealed class _plainMarker;

  private sealed class _markerCatalog : IMessageTypeCatalog {
    private static readonly IReadOnlyList<MessageTypeCatalogEntry> _entries = [
      new(typeof(_compositeMarker), TypeNameFormatter.FormatClrTypeName(typeof(_compositeMarker)), "event", null) { IsComposite = true },
      new(typeof(_plainMarker), TypeNameFormatter.FormatClrTypeName(typeof(_plainMarker)), "event", null),
    ];
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => _entries;
  }

  private static EventMarkerResolver _markerResolver() => new(new _markerCatalog());

  /// <summary>The assembly-qualified wire form the receive gates hand to the lookup.</summary>
  private static string _wireName(Type type) => type.AssemblyQualifiedName!;

  [Test]
  public async Task IsCompositeWireType_CatalogStampsComposite_ReturnsTrueAsync() {
    var isComposite = CompositeInboxFanout.IsCompositeWireType(_wireName(typeof(_compositeMarker)), _markerResolver());

    await Assert.That(isComposite).IsTrue()
      .Because("A composite must be recognisable at the receive boundary from its wire type name alone — " +
               "the payload is an undeserialized JsonElement there, so the compile-time catalog stamp is the only signal.");
  }

  [Test]
  public async Task IsCompositeWireType_CatalogStampsPlainEvent_ReturnsFalseAsync() {
    var isComposite = CompositeInboxFanout.IsCompositeWireType(_wireName(typeof(_plainMarker)), _markerResolver());

    await Assert.That(isComposite).IsFalse()
      .Because("Ordinary events must stay subject to the no-consumer gate — exempting them would refill the inbox " +
               "with cross-service types this service knows nothing about.");
  }

  [Test]
  public async Task IsCompositeWireType_TypeNotInCatalog_ReturnsFalseAsync() {
    var isComposite = CompositeInboxFanout.IsCompositeWireType("Some.Unknown.Type, Some.Assembly", _markerResolver());

    await Assert.That(isComposite).IsFalse()
      .Because("A catalog miss means 'unknown here', not 'composite' — the gate keeps its normal behaviour.");
  }

  [Test]
  public async Task IsCompositeWireType_NoMarkerResolver_ReturnsFalseAsync() {
    var isComposite = CompositeInboxFanout.IsCompositeWireType(_wireName(typeof(_compositeMarker)), markerResolver: null);

    await Assert.That(isComposite).IsFalse()
      .Because("Without a catalog there is nothing to consult; the caller keeps its pre-existing behaviour rather than guessing.");
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("   ")]
  public async Task IsCompositeWireType_BlankTypeName_ReturnsFalseAsync(string? wireTypeName) {
    var isComposite = CompositeInboxFanout.IsCompositeWireType(wireTypeName, _markerResolver());

    await Assert.That(isComposite).IsFalse()
      .Because("An unusable type name cannot be catalog-addressed, so it cannot be claimed as a composite.");
  }

  [Test]
  public async Task IsCompositeWireType_GenericTypeName_ReturnsFalseAsync() {
    var isComposite = CompositeInboxFanout.IsCompositeWireType(
      "Whizbang.Core.Observability.MessageEnvelope`1[[Some.Inner, Some.Assembly]], Whizbang.Core",
      _markerResolver());

    await Assert.That(isComposite).IsFalse()
      .Because("Generic payload names are not catalog-addressed — the catalog holds concrete message types only.");
  }
}
