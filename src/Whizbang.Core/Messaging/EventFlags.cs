namespace Whizbang.Core.Messaging;

/// <summary>
/// Bitmask categorizing events on the event store and on transport
/// (outbox / inbox). One column (<c>flags INTEGER NOT NULL DEFAULT 0</c>)
/// per row replaces multiple per-category boolean columns — new event
/// categories ship by adding a new flag value, no schema migration
/// required.
/// </summary>
/// <remarks>
/// <para>
/// Whizbang's framework dispatch checks specific flag combinations to
/// route events through the right path. The
/// <see cref="Collective"/> flag fires the collective-event apply
/// pipeline; the <see cref="Composite"/> flag fires the per-stream
/// expansion at the transport boundary. An event with no flags set
/// (<see cref="None"/>) is a regular per-stream event.
/// </para>
/// <para>
/// <strong>Why a bitmask, not booleans:</strong> two booleans
/// (<c>is_collective</c>, <c>is_composite</c>) couldn't be extended
/// without another schema migration. A bitmask defers that decision —
/// adding <c>Compensating</c> or <c>Migrating</c> tomorrow costs zero
/// migrations. Postgres handles bitwise ops at index speed
/// (<c>WHERE (flags &amp; 1) = 1</c>).
/// </para>
/// <para>
/// <strong>Multiple flags coexist:</strong> a single event can be both
/// composite and another category in the future. The framework only
/// inspects the specific flag it cares about at each dispatch site,
/// rather than treating the value as a discriminated enum.
/// </para>
/// <para>
/// <strong>Two kinds of flag — the extension convention.</strong> Values fall into two groups:
/// <em>category</em> flags describe what an event <em>is</em> (<see cref="Collective"/>,
/// <see cref="Composite"/>); <em>treatment</em> flags tell the framework to bypass or alter some
/// functionality for an event (<see cref="NoRebroadcast"/> is the first). Treatment flags are
/// <strong>not composite-specific</strong> — any event can carry one. The convention for adding a new
/// treatment flag:
/// </para>
/// <list type="number">
///   <item><description>Add the bit here (next free position) and ship it <strong>with the gate that reads it</strong> — a flag with no gate is dead code.</description></item>
///   <item><description>Let an event opt in by a <strong>marker interface</strong> (the builders already derive <see cref="Composite"/>/<see cref="Collective"/> via <c>payload is IXxxEvent</c>) for a type-level treatment, or by setting <see cref="Observability.IMessageEnvelope.Flags"/> on the envelope for a <strong>per-instance</strong> treatment.</description></item>
///   <item><description>Composites <strong>propagate</strong> their treatment flags to fan-out children (today the child stamp carries <see cref="NoRebroadcast"/>; extend that line when a composite should pass a new flag down).</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/messaging/composite-events#treatment-flags</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventFlagsTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventFlagsTransportTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/NoRebroadcastGuardTests.cs</tests>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "EventFlags is the natural name for the [Flags] enum stored in the wh_event_store / wh_outbox / wh_inbox 'flags' column. CA1711's 'Flags' suffix rule is exactly the case where the suffix carries semantic meaning that the rule was not designed for.")]
public enum EventFlags {
  /// <summary>Regular per-stream event. No special handling.</summary>
  None = 0,

  /// <summary>
  /// Event is a collective mutation — descriptor for "apply this
  /// mutation to every row in the scope." Dispatched through
  /// <c>ICollectiveDispatcher</c> instead of the per-stream Apply path.
  /// </summary>
  Collective = 1 << 0,

  /// <summary>
  /// Event is a wire-only composite envelope — multiple inner events
  /// bundled into one transport hop. The receiver expands it before the
  /// inbox stores anything.
  /// </summary>
  Composite = 1 << 1,

  /// <summary>
  /// Per-instance marker on a fan-out child: this message is confined to the inbox → event-store →
  /// local-processing path and MUST NOT be re-broadcast. Composite fan-out stamps every child with this
  /// flag; the outbox-enqueue boundary hard-checks it and drops any flagged message, turning "children
  /// never outbox" into an enforced invariant on top of hop-based echo suppression (defense-in-depth).
  /// </summary>
  /// <docs>fundamentals/messaging/composite-events#no-rebroadcast</docs>
  NoRebroadcast = 1 << 2,

  // Future flags add new values here without requiring schema migrations.
}
