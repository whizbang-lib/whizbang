namespace Whizbang.Core.Messaging;

/// <summary>
/// Indicates the processing mode for the current lifecycle invocation.
/// Receptors can use this to distinguish between live processing, replay (rewind), and rebuild operations.
/// </summary>
/// <remarks>
/// <para>
/// During replay and rebuild, side-effect receptors (email, webhooks, cache busting) should typically
/// NOT fire, as those side effects already occurred during original processing. Use
/// <see cref="FireDuringReplayAttribute"/> to opt specific receptors into replay/rebuild invocation.
/// </para>
/// <para>
/// <strong>Example:</strong> Receptor that branches on processing mode:
/// </para>
/// <code>
/// [FireDuringReplay]
/// [FireAt(LifecycleStage.PostPerspectiveInline)]
/// public class DependentModelUpdater : IReceptor&lt;OrderCreatedEvent&gt; {
///   private readonly ILifecycleContext? _context;
///
///   public DependentModelUpdater(ILifecycleContext? context = null) {
///     _context = context;
///   }
///
///   public ValueTask HandleAsync(OrderCreatedEvent evt, CancellationToken ct) {
///     if (_context?.ProcessingMode == ProcessingMode.Replay) {
///       // Skip expensive operations during replay, just update dependent model
///     }
///     return ValueTask.CompletedTask;
///   }
/// }
/// </code>
/// </remarks>
/// <docs>core-concepts/lifecycle-receptors#processing-mode</docs>
public enum ProcessingMode {
  /// <summary>
  /// Normal live processing. All receptors fire as usual.
  /// </summary>
  Live = 0,

  /// <summary>
  /// Rewind replay triggered by a late-arriving event.
  /// Receptors are suppressed by default unless decorated with <see cref="FireDuringReplayAttribute"/>.
  /// </summary>
  Replay = 1,

  /// <summary>
  /// Full or partial perspective rebuild.
  /// Receptors are suppressed by default unless decorated with <see cref="FireDuringReplayAttribute"/>.
  /// </summary>
  Rebuild = 2
}
