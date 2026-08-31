using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// A per-OCCURRENCE audit decision, on top of the per-TYPE attribute.
/// </summary>
/// <remarks>
/// <para>
/// <c>[AuditEvent]</c> answers whether a TYPE is ever worth auditing. Three real cases need a
/// decision about a particular occurrence instead, and none can be expressed by an attribute:
/// </para>
/// <list type="bullet">
///   <item>
///     The same edit event is emitted whether a person edited one record by hand or an import wrote
///     ten thousand. Only the first is worth a trail. The events are identical in type and differ
///     only in a payload flag.
///   </item>
///   <item>
///     A bulk operation should read as one line — "a person imported 500 records" — not as the
///     constituent events. The interesting record is the start and the end, and its label depends
///     on the payload.
///   </item>
///   <item>
///     A saga's start and end are worth recording as the ACTIVITY they represent. "SagaStartedEvent"
///     tells an auditor nothing; "Bulk acknowledgment assignment started" tells them what happened.
///   </item>
/// </list>
/// <para>
/// The existing humanizers cannot serve this: they are <c>Func&lt;string, string?&gt;</c> over the
/// type NAME, so they never see the instance and cannot count, name, or veto anything.
/// </para>
/// <para>
/// A decision must therefore be three-state. A bool cannot distinguish "skip this occurrence" from
/// "no opinion, defer to the attribute", and collapsing those makes a hook that declines to decide
/// silently suppress everything it is asked about.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditEligibility.cs</code-under-test>
[Category("SystemEvents")]
public class AuditDecisionHookTests {

  [AuditEvent(Reason = "field edits are audited only when a person made them")]
  private sealed record _recordFieldEdited : IEvent {
    public bool FromImport { get; init; }
  }

  [AuditEvent(Reason = "activity boundary")]
  private sealed record _bulkImportStarted : IEvent {
    public int RecordCount { get; init; }
  }

  private sealed record _unmarkedEvent : IEvent;

  /// <summary>Vetoes import-driven edits and names bulk activity.</summary>
  private sealed class _hook : IAuditDecisionHook {
    public AuditDecision Decide(object payload, Type eventType) => payload switch {
      _recordFieldEdited e when e.FromImport => AuditDecision.Skip,
      _bulkImportStarted b => AuditDecision.Record(
        name: "Bulk record import", description: $"Imported {b.RecordCount} records"),
      _ => AuditDecision.NoOpinion,
    };
  }

  private static readonly SystemEventOptions _optIn = new SystemEventOptions().EnableEventAudit();

  [Test]
  public async Task AHookCanVetoAnOccurrenceOfAnAuditedTypeAsync() {
    var decision = AuditEligibility.Decide(
      new _recordFieldEdited { FromImport = true }, typeof(_recordFieldEdited), AuditMode.OptIn, new _hook());

    await Assert.That(decision.ShouldAudit).IsFalse()
      .Because("an import writing ten thousand edits is not ten thousand things a person did; the "
             + "type is auditable but this occurrence is not, and only the payload knows which");
  }

  [Test]
  public async Task TheSameTypeIsStillAuditedWhenAPersonDidItAsync() {
    var decision = AuditEligibility.Decide(
      new _recordFieldEdited { FromImport = false }, typeof(_recordFieldEdited), AuditMode.OptIn, new _hook());

    await Assert.That(decision.ShouldAudit).IsTrue()
      .Because("the veto has to be per-occurrence — vetoing the type would lose the manual edits "
             + "that are the whole reason the type is audited");
  }

  [Test]
  public async Task AHookCanNameAndDescribeAnOccurrenceAsync() {
    var decision = AuditEligibility.Decide(
      new _bulkImportStarted { RecordCount = 500 }, typeof(_bulkImportStarted), AuditMode.OptIn, new _hook());

    await Assert.That(decision.ShouldAudit).IsTrue();
    await Assert.That(decision.Name).IsEqualTo("Bulk record import");
    await Assert.That(decision.Description).IsEqualTo("Imported 500 records")
      .Because("the count comes from the payload, so a type-name humanizer cannot produce it — and "
             + "'BulkImportStartedEvent' tells an auditor nothing about what happened");
  }

  [Test]
  public async Task NoOpinionDefersToTheAttributeRatherThanSuppressingAsync() {
    var decision = AuditEligibility.Decide(
      new _recordFieldEdited { FromImport = false }, typeof(_recordFieldEdited), AuditMode.OptIn, new _hook());

    await Assert.That(decision.ShouldAudit).IsTrue()
      .Because("a hook that declines to decide must not silently suppress; if 'no opinion' read as "
             + "'skip', adding a hook for one event type would mute every other type it saw");
  }

  [Test]
  public async Task AnUnmarkedTypeStaysUnauditedUnderOptInAsync() {
    var decision = AuditEligibility.Decide(
      new _unmarkedEvent(), typeof(_unmarkedEvent), AuditMode.OptIn, new _hook());

    await Assert.That(decision.ShouldAudit).IsFalse()
      .Because("OptIn means the attribute is the gate; a hook returning no opinion must not open it");
  }

  [Test]
  public async Task WithoutAHookTheAttributeAloneDecidesAsync() {
    var marked = AuditEligibility.Decide(
      new _bulkImportStarted(), typeof(_bulkImportStarted), AuditMode.OptIn, hook: null);
    var unmarked = AuditEligibility.Decide(
      new _unmarkedEvent(), typeof(_unmarkedEvent), AuditMode.OptIn, hook: null);

    await Assert.That(marked.ShouldAudit).IsTrue();
    await Assert.That(unmarked.ShouldAudit).IsFalse()
      .Because("the hook is additive — everything must behave exactly as before when none is registered");
  }


  // ---- the label has to reach the stored record, not just the decision ----

  [Test]
  public async Task TheOccurrenceLabelIsCarriedOnTheAuditRecordAsync() {
    // A decision that names an activity is worthless if the name is discarded before storage. The
    // existing humanizers run at PROJECTION time from the type name, so there is nowhere for a
    // payload-derived label to live unless the record itself carries one.
    var audit = new EventAudited {
      Id = Guid.CreateVersion7(),
      OriginalEventType = "Contracts.BulkImportStarted, Contracts",
      OriginalStreamId = Guid.CreateVersion7().ToString(),
      OriginalStreamPosition = 1,
      OriginalBody = System.Text.Json.JsonDocument.Parse("{}").RootElement,
      Timestamp = DateTimeOffset.UtcNow,
      ActivityName = "Bulk record import",
      ActivityDescription = "Imported 500 records",
    };

    await Assert.That(audit.ActivityName).IsEqualTo("Bulk record import");
    await Assert.That(audit.ActivityDescription).IsEqualTo("Imported 500 records")
      .Because("an auditor reading 'BulkImportStartedEvent' learns nothing; the activity and its "
             + "size are what the record exists to convey");
  }

  [Test]
  public async Task AuditRecordsAreNeverThemselvesAuditedAsync() {
    var decision = AuditEligibility.Decide(
      new object(), typeof(EventAudited), AuditMode.OptOut, new _hook());

    await Assert.That(decision.ShouldAudit).IsFalse()
      .Because("auditing an audit record is an infinite loop, and no hook or mode may re-open it");
  }
}
