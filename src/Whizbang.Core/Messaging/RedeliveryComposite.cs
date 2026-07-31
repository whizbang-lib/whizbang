using System;
using System.Collections.Generic;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Messaging;

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
/// <docs>proposals/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs</tests>
public interface IIdentityPreservingComposite : ICompositeEvent {
  /// <summary>Original message ids, parallel to the inner events (same order and count).</summary>
  IReadOnlyList<Guid> InnerEventIds { get; }
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
/// <docs>proposals/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs</tests>
[PinnedId("b3d9f2a1-6c47-4e0d-9a58-1f2e3c4d5b6a")]
public sealed class RedeliveryComposite : CompositeEventBase, IIdentityPreservingComposite {
  /// <summary>Original message ids, parallel to <see cref="CompositeEventBase.Inner"/>.</summary>
  public List<Guid> InnerEventIds { get; init; } = [];

  IReadOnlyList<Guid> IIdentityPreservingComposite.InnerEventIds => InnerEventIds;
}
