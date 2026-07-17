using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Why an ephemeral event / stream / perspective row is being destroyed — the trigger the reaper acted on.
/// Carried on <see cref="DestructionContext"/> so a <c>PreDestruction</c> hook can branch on it (e.g. archive
/// on <see cref="TtlExpired"/>, crypto-shred on <see cref="Erasure"/>).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public enum DestructionReason {
  /// <summary>Every consuming perspective has processed the event (E1's consumption gate).</summary>
  ConsumptionComplete = 0,

  /// <summary>A logical <c>expires_at</c> passed (E2's <see cref="Whizbang.Core.Attributes.Destruction.AfterTtl"/>).</summary>
  TtlExpired = 1,

  /// <summary>A whole ephemeral stream is being purged or compacted.</summary>
  StreamPurge = 2,

  /// <summary>A subject-scoped erasure (GDPR crypto-shred, phase G1).</summary>
  Erasure = 3
}

/// <summary>
/// What the reaper is about to remove, at the moment a destruction hook fires.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public enum DestructionGranularity {
  /// <summary>A single ephemeral event's body.</summary>
  Event = 0,

  /// <summary>A whole ephemeral stream (all its perspectives).</summary>
  Stream = 1,

  /// <summary>A single <c>TtlRow</c> perspective row.</summary>
  PerspectiveRow = 2
}

/// <summary>
/// What happens to the data at destruction — the built-in + user-pluggable handlers the retention
/// "strategies" collapse into. A <c>PreDestruction</c> hook returns one; the reaper runs the matching
/// handler on its critical path before the physical delete.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public enum Disposition {
  /// <summary>Physical delete — the default, and all E2 wires today.</summary>
  Delete = 0,

  /// <summary>Fold the detail into an authoritative <em>ephemeral</em> <c>Compacted&lt;T&gt;</c> summary, then
  /// delete the detail (phase E3). Never promotes to a Sourced event.</summary>
  Compact = 1,

  /// <summary>Move the detail to cold storage — preserved and auditable — then drop it from the hot store
  /// (phase A1).</summary>
  Archive = 2,

  /// <summary>Destroy the subject's per-subject key so the ciphertext is unreadable; the durable data is
  /// not deleted (phase G1).</summary>
  CryptoShred = 3
}

/// <summary>
/// Everything a <c>PreDestruction</c> / <c>PostDestruction</c> receptor needs to decide what to do before a
/// batch of ephemeral events / a stream / a perspective row is destroyed. The reaper populates it and awaits
/// the <c>Inline</c> hook ONCE per cycle (its side-effects must commit before the physical delete). The hook
/// is <strong>batched</strong>: <see cref="Targets"/> carries every event the reaper is about to remove this
/// cycle, so a hook writes one compacted summary / runs one archive pass over the whole set rather than being
/// invoked per event.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed record DestructionContext {
  /// <summary>Why this destruction is happening.</summary>
  public required DestructionReason Reason { get; init; }

  /// <summary>What is being destroyed (event batch / stream / perspective row).</summary>
  public required DestructionGranularity Granularity { get; init; }

  /// <summary>
  /// The disposition declared for the type (via <c>[Ephemeral(OnDestroy = …)]</c>) or the framework
  /// default (<see cref="Disposition.Delete"/>). A hook receives this and MAY override it at runtime.
  /// </summary>
  public Disposition DeclaredDefault { get; init; } = Disposition.Delete;

  /// <summary>The tenant / subject scope — set for single-subject granularities (stream / perspective row);
  /// <c>null</c> for a cross-stream event batch, where each target carries its own stream.</summary>
  public PerspectiveScope? Scope { get; init; }

  /// <summary>
  /// The FULL batch of events being destroyed this invocation — the hook runs ONCE for the whole batch, not
  /// once per event. Each target carries its own event id, stream, and event type. May span many streams.
  /// </summary>
  public IReadOnlyList<EphemeralDestructionTarget> Targets { get; init; } = [];
}

/// <summary>
/// A destruction hook's decision. Exactly one intent applies: <see cref="Cancel"/> keeps the data
/// (still ephemeral); <see cref="DeferUntil"/> reschedules; otherwise the reaper proceeds with
/// <see cref="Disposition"/>. Cancel/Defer keep the ephemeral→Sourced boundary closed — there is no
/// "promote to durable".
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed record DestructionResult {
  /// <summary>The disposition to run when proceeding. Ignored when <see cref="Cancel"/> or
  /// <see cref="DeferUntil"/> is set.</summary>
  public Disposition Disposition { get; init; } = Disposition.Delete;

  /// <summary>Keep the data — do not destroy it now. It stays ephemeral (unbounded cancel = leak risk,
  /// the developer's call; observable via metrics).</summary>
  public bool Cancel { get; init; }

  /// <summary>Reschedule the destruction to this instant (a new <c>expires_at</c>) instead of destroying now.</summary>
  public DateTimeOffset? DeferUntil { get; init; }

  /// <summary>Proceed with the given disposition (default <see cref="Disposition.Delete"/>).</summary>
  public static DestructionResult Proceed(Disposition disposition = Disposition.Delete) =>
    new() { Disposition = disposition };

  /// <summary>Keep the data — cancel this destruction.</summary>
  public static DestructionResult Cancelled { get; } = new() { Cancel = true };

  /// <summary>Reschedule the destruction to <paramref name="until"/>.</summary>
  public static DestructionResult Defer(DateTimeOffset until) => new() { DeferUntil = until };
}
