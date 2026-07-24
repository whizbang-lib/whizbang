namespace Whizbang.Core.Messaging;

/// <summary>
/// Indicates the processing mode for the current lifecycle invocation.
/// Receptors can use this to distinguish between live processing, replay (rewind), and rebuild operations.
/// </summary>
/// <remarks>
/// <para>
/// During replay and rebuild, side-effect receptors (email, webhooks, cache busting) should typically
/// NOT fire, as those side effects already occurred during original processing. Use
/// <see cref="ReceptorIdempotentAttribute"/> with <c>AlwaysFire = true</c> to opt specific receptors
/// into firing for every applied event during replay and rebuild.
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
/// <docs>fundamentals/receptors/lifecycle-receptors#processing-mode</docs>
public enum ProcessingMode {
  /// <summary>
  /// Normal live processing. All receptors fire as usual.
  /// </summary>
  Live = 0,

  /// <summary>
  /// Rewind replay triggered by a late-arriving event.
  /// Receptors are suppressed for already-processed events by default. Receptors decorated
  /// with <see cref="ReceptorIdempotentAttribute"/> (AlwaysFire = true) fire for every event.
  /// </summary>
  Replay = 1,

  /// <summary>
  /// Full or partial perspective rebuild.
  /// Receptors are suppressed for already-processed events by default. Receptors decorated
  /// with <see cref="ReceptorIdempotentAttribute"/> (AlwaysFire = true) fire for every event.
  /// </summary>
  Rebuild = 2
}
