using Whizbang.Core.Messaging;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Test receptor that signals completion when invoked at the PostPerspectiveInline lifecycle stage.
/// Used for deterministic test synchronization - wait for perspective processing to complete instead of polling.
/// </summary>
/// <typeparam name="TEvent">The event type to wait for.</typeparam>
/// <remarks>
/// <para>
/// This receptor fires at <see cref="LifecycleStage.PostPerspectiveInline"/> (blocking stage),
/// which guarantees perspective data is saved to database before the receptor completes.
/// </para>
/// <para>
/// <strong>Usage Pattern:</strong>
/// </para>
/// <code>
/// // Create completion source
/// var completionSource = new TaskCompletionSource&lt;bool&gt;();
/// var receptor = new PerspectiveCompletionReceptor&lt;ProductCreatedEvent&gt;(
///   completionSource,
///   perspectiveName: "ProductCatalog"  // Optional: filter by perspective
/// );
///
/// // Register with runtime registry
/// var registry = host.Services.GetRequiredService&lt;ILifecycleReceptorRegistry&gt;();
/// registry.Register&lt;ProductCreatedEvent&gt;(receptor, LifecycleStage.PostPerspectiveInline);
///
/// try {
///   // Dispatch command
///   await dispatcher.SendAsync(createProductCommand);
///
///   // Wait for perspective processing to complete (no polling!)
///   await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(15));
///
///   // Verify perspective data
///   var product = await productLens.GetByIdAsync(createProductCommand.ProductId);
///   Assert.That(product).IsNotNull();
/// } finally {
///   registry.Unregister&lt;ProductCreatedEvent&gt;(receptor, LifecycleStage.PostPerspectiveInline);
/// }
/// </code>
/// </remarks>
/// <docs>testing/lifecycle-synchronization</docs>
[FireAt(LifecycleStage.PostPerspectiveInline)]  // Blocking stage - guarantees persistence
public sealed class PerspectiveCompletionReceptor<TEvent> : IReceptor<TEvent>
  where TEvent : IEvent {

  private readonly TaskCompletionSource<bool> _completionSource;
  private readonly string? _perspectiveName;
  private readonly ILifecycleContext? _lifecycleContext;

  /// <summary>
  /// Creates a new perspective completion receptor.
  /// </summary>
  /// <param name="completionSource">Task completion source to signal when processing completes.</param>
  /// <param name="perspectiveName">Optional perspective name to filter by (null = any perspective).</param>
  /// <param name="lifecycleContext">Optional lifecycle context for filtering (injected by test fixture).</param>
  public PerspectiveCompletionReceptor(
    TaskCompletionSource<bool> completionSource,
    string? perspectiveName = null,
    ILifecycleContext? lifecycleContext = null) {

    _completionSource = completionSource ?? throw new ArgumentNullException(nameof(completionSource));
    _perspectiveName = perspectiveName;
    _lifecycleContext = lifecycleContext;
  }

  /// <summary>
  /// Handles the event by signaling completion.
  /// Filters by perspective name if specified.
  /// </summary>
  public ValueTask HandleAsync(TEvent message, CancellationToken cancellationToken = default) {
    // Filter by perspective if specified
    if (_lifecycleContext is not null && _perspectiveName is not null) {
      if (_lifecycleContext.PerspectiveName != _perspectiveName) {
        return ValueTask.CompletedTask;  // Not the perspective we're waiting for
      }
    }

    // Signal completion (use TrySetResult to handle multiple events)
    _completionSource.TrySetResult(true);
    return ValueTask.CompletedTask;
  }
}
