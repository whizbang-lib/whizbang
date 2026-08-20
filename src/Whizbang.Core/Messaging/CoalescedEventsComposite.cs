using System;
using System.Collections.Generic;
using System.Text.Json;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The generic coalesced-batch carrier: one wire envelope bundling the pending singles a
/// coalesce-bound tag group folded (see <c>TagOptions.Coalesce</c>), expanded by the standard
/// composite fan-out at the consumer. This is the default composite a coalesce binding ships
/// with; a binding may supply its own factory instead (the built-in audit binding ships
/// <c>AuditEventsComposite</c>) — factories are code, never reflection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raw carry</b> (<see cref="IRawInnerComposite"/>): the folded singles' payloads are already
/// serialized wire JSON sitting in outbox rows — they ride verbatim with their stored wire type
/// names. No rehydration, no polymorphic re-serialization, no AOT metadata cliff (the proven
/// <see cref="RedeliveryComposite"/> idiom).
/// </para>
/// <para>
/// <b>Identity preservation</b> (<see cref="IIdentityPreservingComposite"/>): each child keeps
/// its single's ORIGINAL message id, so a single that raced its max-delay floor (shipping
/// individually) while also being folded dedups at the consumer's inbox instead of
/// double-delivering.
/// </para>
/// <para>
/// <b>Atomicity</b> follows the binding's <c>CoalescePolicyOptions.Atomicity</c>
/// (default <see cref="FanoutAtomicity.Independent"/> — coalesce groups bundle self-contained
/// records, so one poison inner dead-letters alone).
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/message-tags#coalescing</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/CoalesceShipWorkerTests.cs:RunOnce_DefaultFactory_BuildsRawCarryCompositeFromSinglesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs:TryExpand_CoalescedEventsComposite_DeliversEachInnerAsync</tests>
[PinnedId("4cdff50e-b094-4bb8-b6ef-acc86ce2469e")]
public sealed class CoalescedEventsComposite
  : CompositeEventBase, IIdentityPreservingComposite, IRawInnerComposite, IControlPlaneMessage {
  /// <summary>
  /// Each folded single's raw stored payload JSON, verbatim from its outbox row — never
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
