namespace Whizbang.Core.Attributes;

/// <summary>
/// Declares an event <em>ephemeral</em> — an event-driven, self-destructing trigger rather than a durable
/// event-sourced fact. The read model / snapshot becomes the source of truth; there is no replay from a
/// log. This attribute is the <strong>compile-time authority</strong>: the analyzer, source generators,
/// and AOT read it, and the emit path <em>translates</em> it into the runtime carriers (generated catalog
/// metadata, cross-service envelope metadata, and an optional hot-path <c>expires_at</c> hint).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Composable.</strong> Because it targets classes, structs <em>and</em> interfaces and is
/// inherited, it can sit directly on an event, on an abstract base record, or on a marker interface —
/// letting a team define a reusable ephemeral profile once. The runtime is zero-reflection, so a source
/// generator <em>resolves</em> the effective mode at compile time by walking own type → base records →
/// implemented interfaces (most-specific wins); it does not rely on CLR reflection inheritance (which
/// covers base classes only, never interfaces).
/// </para>
/// <para>
/// <strong>Enforced.</strong> The WHIZ130–139 analyzer band flags mixed-mode perspectives, illegal
/// rebuild/rewind, ephemeral→Sourced laundering, mixed-mode streams, and ambiguous composition; a runtime
/// guard backstops them.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Presence: perspective state may live in RAM (lost on rebalance) — a deliberate opt-in.
/// [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.InMemory)]
/// public sealed record UserIsTyping(Guid ConversationId, Guid UserId) : IEvent;
/// </code>
/// </example>
/// <docs>fundamentals/events/ephemeral-events</docs>
[AttributeUsage(
  AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct,
  AllowMultiple = false,
  Inherited = true)]
public sealed class EphemeralAttribute : Attribute {
  /// <summary>How/when the event self-destructs. Defaults to <see cref="Destruction.WhenConsumed"/>.</summary>
  public Destruction Destruction { get; init; } = Destruction.WhenConsumed;

  /// <summary>
  /// Where the perspective's read model lives. Defaults to <see cref="TransientStorage.PersistedRow"/>
  /// (a persisted, restart-safe <c>wh_per_*</c> row — the source of truth for a WhenConsumed stream). The
  /// event itself always persists and routes regardless; this only picks the read-model store strategy.
  /// </summary>
  public TransientStorage Storage { get; init; } = TransientStorage.PersistedRow;

  /// <summary>
  /// Per-type override (seconds) for the rewind grace window — how long a consumed ephemeral body is
  /// retained so an out-of-order straggler can still rewind through it. Defaults to <c>-1</c>, which
  /// inherits the global <c>ephemeral_rewind_grace_seconds</c> setting.
  /// </summary>
  public int RewindGraceSeconds { get; init; } = -1;

  /// <summary>
  /// Time-to-live in seconds for a <see cref="Destruction.AfterTtl"/> event — the event logically expires
  /// (filtered from reads) and then physically reaps once its age exceeds this window, regardless of
  /// consumption. Defaults to <c>-1</c>, meaning <em>no age-based expiry</em>; it is meaningful only when
  /// <see cref="Destruction"/> is <see cref="Destruction.AfterTtl"/> (a <see cref="Destruction.WhenConsumed"/>
  /// event has no TTL — it self-destructs on consumption instead).
  /// </summary>
  public int TtlSeconds { get; init; } = -1;
}
