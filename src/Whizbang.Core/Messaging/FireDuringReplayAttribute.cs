using System;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Opts a receptor into firing during replay and rebuild operations.
/// By default, receptors are suppressed during replay/rebuild to prevent duplicate side effects.
/// Apply this attribute to receptors that must fire during replay (e.g., dependent model updaters).
/// </summary>
/// <remarks>
/// <para>
/// When a perspective rewinds (late event) or rebuilds, all events are replayed from a snapshot
/// or from the beginning. Side-effect receptors (sending emails, calling APIs, invalidating caches)
/// should NOT fire again for events that were already processed. This is the safe default.
/// </para>
/// <para>
/// Receptors that need to fire during replay — such as those updating dependent read models
/// or signaling test completion — should be decorated with this attribute.
/// </para>
/// <para>
/// <strong>Example:</strong>
/// </para>
/// <code>
/// // This receptor WILL fire during replay/rebuild
/// [FireDuringReplay]
/// [FireAt(LifecycleStage.PostPerspectiveInline)]
/// public class DependentModelUpdater : IReceptor&lt;OrderCreatedEvent&gt; {
///   public ValueTask HandleAsync(OrderCreatedEvent evt, CancellationToken ct) {
///     // Update dependent read model — safe to replay
///     return ValueTask.CompletedTask;
///   }
/// }
///
/// // This receptor will NOT fire during replay (default behavior)
/// [FireAt(LifecycleStage.PostPerspectiveInline)]
/// public class EmailNotificationReceptor : IReceptor&lt;OrderCreatedEvent&gt; {
///   public ValueTask HandleAsync(OrderCreatedEvent evt, CancellationToken ct) {
///     // Send email — should NOT fire again during replay
///     return ValueTask.CompletedTask;
///   }
/// }
/// </code>
/// </remarks>
/// <docs>fundamentals/receptors/lifecycle-receptors#replay-safety</docs>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FireDuringReplayAttribute : Attribute;
