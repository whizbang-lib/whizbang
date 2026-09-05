using System;
using System.Collections.Generic;
using System.Text.Json;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Minting;

/// <summary>
/// A composite whose children ride as <b>raw stored wire JSON</b> plus wire type names instead of
/// typed <see cref="IMessage"/> instances. The origin already holds each child's exact wire-form
/// JSON (<c>event_data</c>); rehydrating it into typed payloads only to re-serialize them
/// polymorphically is redundant work, an upcast/version-skew fidelity risk, and an AOT cliff — a
/// consumer payload shape whose metadata is not reachable through the polymorphic resolver chain
/// (observed live: a collection-typed property) makes the re-serialization throw, so the repair
/// never ships. Raw carry removes the class: the origin needs no type knowledge at all, and the
/// receive-side fan-out builds children directly from the raw payloads.
/// </summary>
/// <remarks>
/// <see cref="InnerPayloads"/> and <see cref="InnerTypeNames"/> are parallel (same order, same
/// count). The fan-out is STRICT: any desync fails the whole expansion — these composites are
/// machine-built, so a mismatch is a producer bug, never data.
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs:TryExpand_RawComposite_ChildrenBuiltFromRawPayloadsAsync</tests>
public interface IRawInnerComposite : ICompositeEvent {
  /// <summary>Each child's raw stored payload JSON, verbatim from the origin's store.</summary>
  IReadOnlyList<JsonElement> InnerPayloads { get; }

  /// <summary>Each child's stored wire type name ("Type, Assembly"), parallel to <see cref="InnerPayloads"/>.</summary>
  IReadOnlyList<string> InnerTypeNames { get; }

  /// <summary>
  /// Each child's ORIGINAL stream id, parallel to <see cref="InnerPayloads"/> — or null/empty
  /// when the producer predates the field (#596), in which case children inherit the
  /// composite's stream as before. When present, expansion restores each child to its own
  /// stream: the composite is transport packaging, never a stream-identity rewrite —
  /// collapsing many source streams onto one serialized the producer's parallelism behind a
  /// single drain lane at every receiver.
  /// </summary>
  IReadOnlyList<Guid>? InnerStreamIds => null;
}

/// <summary>
/// A composite whose children must keep <b>caller-supplied</b> message identities instead of the
/// fan-out's fresh ids. Normal composites bundle NEW events (fresh ids are correct); a re-delivery
/// bundle carries PREVIOUSLY PERSISTED events whose original ids are load-bearing — consumers
/// converge via the event-id conflict skip, so a re-minted id would append a duplicate instead of
/// skipping an already-present event.
/// </summary>
/// <remarks>
/// <see cref="InnerEventIds"/> is parallel to <see cref="ICompositeEvent.InnerEvents"/> (same order,
/// same count). The fan-out is STRICT for identity-preserving composites: a null inner event or an
/// id/inner count mismatch fails the whole expansion (routed to the DLQ) rather than desynchronizing
/// the pairing — these bundles are machine-built, so a mismatch is a producer bug, never data.
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs</tests>
public interface IIdentityPreservingComposite : ICompositeEvent {
  /// <summary>Original message ids, parallel to the inner events (same order and count).</summary>
  IReadOnlyList<Guid> InnerEventIds { get; }

  /// <summary>
  /// The origin service the bundled events were emitted by, or <see cref="Guid.Empty"/> when
  /// unknown. When set, fanned-out children carry it as their source identity — a repaired window
  /// recounts under the SAME origin the live delivery would have (stream-integrity Phase B).
  /// </summary>
  Guid OriginServiceId => Guid.Empty;

  /// <summary>
  /// Original origin commit sequences, parallel to <see cref="InnerEventIds"/> (same order and
  /// count when non-null; null entries = the event predates commit-sequence stamping). Null list =
  /// not carried. When present, fanned-out children carry each event's ORIGINAL sequence so
  /// windowed integrity accounting sees the repaired events inside their original window.
  /// </summary>
  IReadOnlyList<long?>? InnerCommitSequences => null;
}

/// <summary>
/// Stream-integrity R1: the re-delivery bundle. One instance carries one stream's repair slice —
/// original events in version order, with their original ids — published wire-only at the origin
/// and expanded by the normal composite fan-out at consumers. Identity preservation makes
/// convergence free: consumers that already hold an inner event skip it via the event-id conflict;
/// consumers missing it append and converge through the standard late-history path.
/// </summary>
/// <remarks>
/// <see cref="CompositeEventBase.Atomicity"/> stays <see cref="FanoutAtomicity.Independent"/> (the
/// base default): one poison inner event must not dead-letter a stream's whole repair — the next
/// integrity cycle re-detects any remainder.
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs</tests>
[PinnedId("b3d9f2a1-6c47-4e0d-9a58-1f2e3c4d5b6a")]
public sealed class RedeliveryComposite
  : CompositeEventBase, IIdentityPreservingComposite, IRawInnerComposite, IControlPlaneMessage {
  /// <summary>
  /// Each child's raw stored payload JSON, verbatim from the origin's <c>event_data</c> — the
  /// origin never rehydrates typed payloads (see <see cref="IRawInnerComposite"/>).
  /// </summary>
  public List<JsonElement> InnerPayloads { get; init; } = [];

  /// <summary>Each child's stored wire type name ("Type, Assembly"), parallel to <see cref="InnerPayloads"/>.</summary>
  public List<string> InnerTypeNames { get; init; } = [];

  /// <summary>Original message ids, parallel to <see cref="InnerPayloads"/>.</summary>
  public List<Guid> InnerEventIds { get; init; } = [];

  /// <summary>The origin service the bundled events were emitted by (Guid.Empty = unknown).</summary>
  public Guid OriginServiceId { get; init; }

  /// <summary>Original origin commit sequences, parallel to <see cref="InnerEventIds"/> (null = not carried).</summary>
  public List<long?>? InnerCommitSequences { get; init; }

  IReadOnlyList<Guid> IIdentityPreservingComposite.InnerEventIds => InnerEventIds;

  Guid IIdentityPreservingComposite.OriginServiceId => OriginServiceId;

  IReadOnlyList<long?>? IIdentityPreservingComposite.InnerCommitSequences => InnerCommitSequences;

  IReadOnlyList<JsonElement> IRawInnerComposite.InnerPayloads => InnerPayloads;

  IReadOnlyList<string> IRawInnerComposite.InnerTypeNames => InnerTypeNames;
}
