// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Issue #1730 (WS-2 decision 2): an ephemeral event type is excluded from audit by default, and audited only when its
/// type asks for its body to be kept, because an audit record that only points at an ephemeral event points at nothing
/// once the origin reaps it.
/// </summary>
/// <docs>fundamentals/security/audit-logging#ephemeral-events</docs>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditEligibility.cs</code-under-test>
[Category("SystemEvents")]
public class AuditEphemeralExclusionTests {

  internal sealed record UnmarkedEvent : IEvent;

  [AuditEvent(Reason = "presence is audited for this tenant")]
  internal sealed record MarkedEvent : IEvent;

  [AuditEvent(IncludeBody = true)]
  internal sealed record BodyKeptEvent : IEvent;

  /// <summary>A hook that asks for every occurrence to be recorded.</summary>
  private sealed class RecordEverythingHook : IAuditDecisionHook {
    public AuditDecision Decide(object payload, Type eventType) => AuditDecision.Record("forced");
  }

  [Test]
  public async Task AnEphemeralType_IsNotAuditedByDefaultAsync() {
    var decision = AuditEligibility.Decide(new UnmarkedEvent(), typeof(UnmarkedEvent), AuditMode.OptOut, hook: null, isEphemeral: true);

    await Assert.That(decision.ShouldAudit).IsFalse()
      .Because("a pointer to an ephemeral event dangles once the origin reaps it");
  }

  [Test]
  public async Task TheSameType_NotEphemeral_IsAuditedAsync() {
    // The control: the harness observes the other outcome when only the ephemeral bit changes.
    var decision = AuditEligibility.Decide(new UnmarkedEvent(), typeof(UnmarkedEvent), AuditMode.OptOut, hook: null, isEphemeral: false);

    await Assert.That(decision.ShouldAudit).IsTrue();
  }

  [Test]
  public async Task AnEphemeralTypeMarkedForAudit_WithoutItsBody_IsStillNotAuditedAsync() {
    // Marking the type for audit is not the opt-in: without the body the record would still dangle.
    var decision = AuditEligibility.Decide(new MarkedEvent(), typeof(MarkedEvent), AuditMode.OptIn, hook: null, isEphemeral: true);

    await Assert.That(decision.ShouldAudit).IsFalse();
  }

  [Test]
  [Arguments(AuditMode.OptOut)]
  [Arguments(AuditMode.OptIn)]
  public async Task AnEphemeralTypeThatKeepsItsBody_IsAuditedAsync(AuditMode mode) {
    var decision = AuditEligibility.Decide(new BodyKeptEvent(), typeof(BodyKeptEvent), mode, hook: null, isEphemeral: true);

    await Assert.That(decision.ShouldAudit).IsTrue()
      .Because("IncludeBody is the opt-in: the record carries the event, so nothing dangles when the event is reaped");
  }

  [Test]
  public async Task AHookThatRecordsEverything_DoesNotReopenAnEphemeralTypeAsync() {
    var decision = AuditEligibility.Decide(new UnmarkedEvent(), typeof(UnmarkedEvent), AuditMode.OptOut, new RecordEverythingHook(), isEphemeral: true);

    await Assert.That(decision.ShouldAudit).IsFalse()
      .Because("a hook decides per occurrence but cannot keep a body, so it cannot undo the dangling pointer");
  }

  [Test]
  public async Task AHookThatRecordsEverything_IsHonoredForADurableTypeAsync() {
    // The control for the test above: the same hook is consulted when the type is not ephemeral.
    var decision = AuditEligibility.Decide(new UnmarkedEvent(), typeof(UnmarkedEvent), AuditMode.OptIn, new RecordEverythingHook(), isEphemeral: false);

    await Assert.That(decision.ShouldAudit).IsTrue();
    await Assert.That(decision.Name).IsEqualTo("forced");
  }

  [Test]
  public async Task AnEphemeralTypeThatKeepsItsBody_StillPassesThroughTheHookAsync() {
    var decision = AuditEligibility.Decide(new BodyKeptEvent(), typeof(BodyKeptEvent), AuditMode.OptOut, new RecordEverythingHook(), isEphemeral: true);

    await Assert.That(decision.Name).IsEqualTo("forced")
      .Because("once a type has opted in, the hook decides its occurrences as for any other type");
  }
}
