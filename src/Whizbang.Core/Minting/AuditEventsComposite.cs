using System;
using System.Collections.Generic;
using System.Text.Json;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Minting;

/// <summary>
/// The batched-audit carrier: one wire envelope bundling many pending
/// <see cref="Whizbang.Core.SystemEvents.EventAudited"/> singles, folded by the sliding-window audit
/// shipper (see <see cref="Whizbang.Core.SystemEvents.SystemEventOptions.AuditShipSlideSeconds"/>)
/// and expanded by the standard composite
/// fan-out at the consumer. Per-event audit singles under a bulk workload multiply broker traffic
/// by the event count; folding N singles into one composite divides it back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raw carry</b> (<see cref="IRawInnerComposite"/>): the folded singles' payloads are already
/// serialized <see cref="EventAudited"/> wire JSON sitting in outbox rows — they ride verbatim
/// with their stored wire type names. No rehydration, no polymorphic re-serialization, no AOT
/// metadata cliff (the proven <see cref="RedeliveryComposite"/> idiom).
/// </para>
/// <para>
/// <b>Identity preservation</b> (<see cref="IIdentityPreservingComposite"/>): each child keeps its
/// single's ORIGINAL message id. A single that raced past its
/// <see cref="Whizbang.Core.SystemEvents.SystemEventOptions.AuditShipMaxDelaySeconds"/> floor and shipped individually while
/// also being folded dedups at the consumer's inbox instead of double-recording the audit entry.
/// </para>
/// <para>
/// <b>Non-atomic</b>: <see cref="CompositeEventBase.Atomicity"/> stays
/// <see cref="FanoutAtomicity.Independent"/> (the base default) — audit records are independent
/// facts, so one poison record must dead-letter alone, never its siblings.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/system-events#audit-shipping</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs:TryExpand_AuditEventsComposite_DeliversEachInnerEventAuditedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs:AuditEventsComposite_IsNonAtomic_OneBadRecordMustNotSinkSiblingsAsync</tests>
[PinnedId("d7a2b7c4-8e15-4f4a-9c3e-5b1a2f9e6d01")]
public sealed class AuditEventsComposite : CompositeEventBase, IIdentityPreservingComposite, IRawInnerComposite {
  /// <summary>
  /// Each folded single's raw stored payload JSON (the serialized
  /// <see cref="Whizbang.Core.SystemEvents.EventAudited"/>), verbatim from its outbox row — never
  /// rehydrated (see <see cref="IRawInnerComposite"/>).
  /// </summary>
  public List<JsonElement> InnerPayloads { get; init; } = [];

  /// <summary>Each folded single's stored wire type name ("Type, Assembly"), parallel to <see cref="InnerPayloads"/>.</summary>
  public List<string> InnerTypeNames { get; init; } = [];

  /// <summary>Each folded single's ORIGINAL outbox message id, parallel to <see cref="InnerPayloads"/>.</summary>
  public List<Guid> InnerEventIds { get; init; } = [];

  IReadOnlyList<JsonElement> IRawInnerComposite.InnerPayloads => InnerPayloads;

  IReadOnlyList<string> IRawInnerComposite.InnerTypeNames => InnerTypeNames;

  IReadOnlyList<Guid> IIdentityPreservingComposite.InnerEventIds => InnerEventIds;
}
