using System;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Declares that a receptor is safe to re-invoke for the same event id and controls how
/// it behaves during <see cref="ProcessingMode.Replay"/> and <see cref="ProcessingMode.Rebuild"/>.
/// </summary>
/// <remarks>
/// <para>
/// During a rewind, the <see cref="Whizbang.Core.Perspectives.IPerspectiveReplayReader"/>
/// annotates each event with an <c>IsNew</c> flag derived from the perspective work queue.
/// Events marked new have never had their lifecycle fire; those already processed in a
/// prior run do not fire side-effect receptors by default.
/// </para>
/// <para>
/// Attribute-less receptors are treated as non-idempotent: they fire only for new events
/// and are suppressed for already-processed events during Replay/Rebuild.
/// </para>
/// <para>
/// A receptor decorated with <c>[ReceptorIdempotent(AlwaysFire = true)]</c> opts into
/// firing for every applied event during Replay and Rebuild — the useful pattern for
/// derived read-model updaters that must witness every perspective apply regardless of
/// whether the event was processed before.
/// </para>
/// <para>
/// <strong>Example:</strong>
/// </para>
/// <code>
/// // Fires for every event during rewind/rebuild, including already-processed ones.
/// [ReceptorIdempotent(AlwaysFire = true)]
/// [FireAt(LifecycleStage.PostPerspectiveInline)]
/// public class DependentModelUpdater : IReceptor&lt;OrderCreatedEvent&gt; {
///   public ValueTask HandleAsync(OrderCreatedEvent evt, CancellationToken ct) {
///     // Safe to replay — updates a dependent read model deterministically.
///     return ValueTask.CompletedTask;
///   }
/// }
///
/// // Default: fires only for events that have never been processed before.
/// [FireAt(LifecycleStage.PostPerspectiveInline)]
/// public class EmailNotificationReceptor : IReceptor&lt;OrderCreatedEvent&gt; {
///   public ValueTask HandleAsync(OrderCreatedEvent evt, CancellationToken ct) {
///     // Only fires for brand-new events — never re-sends emails during rewind.
///     return ValueTask.CompletedTask;
///   }
/// }
/// </code>
/// </remarks>
/// <docs>fundamentals/receptors/lifecycle-receptors#replay-safety</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorIdempotentAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ReceptorIdempotentAttribute : Attribute {
  /// <summary>
  /// When <c>true</c>, the receptor fires for every applied event during Replay and
  /// Rebuild — even events that were already processed. When <c>false</c> (the default),
  /// the receptor fires only for events marked new by the replay reader.
  /// </summary>
  public bool AlwaysFire { get; init; }
}
