using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace ECommerce.RabbitMQ.Integration.Tests.Fixtures;

/// <summary>
/// Generic test receptor that signals completion when invoked at any lifecycle stage.
/// Used for integration test synchronization across all 18 lifecycle stages.
/// </summary>
/// <typeparam name="TMessage">The message type to wait for (ICommand, IEvent, or IMessage).</typeparam>
/// <remarks>
/// <para>
/// This is a more flexible version of PerspectiveCompletionReceptor that works for
/// any message type and lifecycle stage. Use [FireAt] attribute or runtime registration
/// to control which stage it fires at.
/// </para>
/// <para>
/// <strong>Usage Pattern:</strong>
/// </para>
/// <code>
/// // Create completion source
/// var completionSource = new TaskCompletionSource&lt;bool&gt;();
/// var receptor = new GenericLifecycleCompletionReceptor&lt;CreateProductCommand&gt;(
///   completionSource,
///   expectedStage: LifecycleStage.ImmediateAsync
/// );
///
/// // Register with runtime registry
/// var registry = host.Services.GetRequiredService&lt;ILifecycleReceptorRegistry&gt;();
/// registry.Register&lt;CreateProductCommand&gt;(receptor, LifecycleStage.ImmediateAsync);
///
/// try {
///   // Dispatch command
///   await dispatcher.SendAsync(createProductCommand);
///
///   // Wait for lifecycle stage to complete
///   await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(15));
///
///   // Verify stage was invoked
///   Assert.That(receptor.InvocationCount).IsEqualTo(1);
/// } finally {
///   registry.Unregister&lt;CreateProductCommand&gt;(receptor, LifecycleStage.ImmediateAsync);
/// }
/// </code>
/// </remarks>
/// <docs>testing/lifecycle-synchronization</docs>
public sealed class GenericLifecycleCompletionReceptor<TMessage> : IReceptor<TMessage>, IAcceptsLifecycleContext
  where TMessage : IMessage {

  private readonly TaskCompletionSource<bool> _completionSource;
  private readonly LifecycleStage? _expectedStage;
  private readonly string? _perspectiveName;
  private readonly Func<TMessage, bool>? _messageFilter;
  private int _invocationCount;

  /// <summary>
  /// Gets the number of times this receptor has been invoked.
  /// </summary>
  public int InvocationCount => _invocationCount;

  /// <summary>
  /// Gets the lifecycle context from the last invocation (if context injection is used).
  /// </summary>
  public ILifecycleContext? LastLifecycleContext { get; private set; }

  /// <summary>
  /// Gets the last message received by this receptor.
  /// </summary>
  public TMessage? LastMessage { get; private set; }

  /// <summary>
  /// Creates a new generic lifecycle completion receptor.
  /// </summary>
  /// <param name="completionSource">Task completion source to signal when processing completes.</param>
  /// <param name="expectedStage">Optional lifecycle stage to validate (null = any stage).</param>
  /// <param name="perspectiveName">Optional perspective name to filter by (null = any perspective).</param>
  /// <param name="messageFilter">Optional predicate to filter which messages signal completion.</param>
  public GenericLifecycleCompletionReceptor(
    TaskCompletionSource<bool> completionSource,
    LifecycleStage? expectedStage = null,
    string? perspectiveName = null,
    Func<TMessage, bool>? messageFilter = null) {

    _completionSource = completionSource ?? throw new ArgumentNullException(nameof(completionSource));
    _expectedStage = expectedStage;
    _perspectiveName = perspectiveName;
    _messageFilter = messageFilter;
  }

  /// <summary>
  /// Handles the message by signaling completion.
  /// Filters by stage, perspective, and custom predicate if specified.
  /// </summary>
  public ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
    Console.WriteLine("##########################################################");
    Console.WriteLine($"[RECEPTOR] >>> HandleAsync INVOKED! Message type: {typeof(TMessage).Name}");
    Console.WriteLine($"[RECEPTOR] Expected stage: {_expectedStage?.ToString() ?? "ANY"}");
    Console.WriteLine($"[RECEPTOR] Expected perspective: {_perspectiveName ?? "ANY"}");
    Console.WriteLine("##########################################################");

    // Store last message (before filtering)
    LastMessage = message;
    Console.WriteLine($"[RECEPTOR] Message stored: {message}");

    // Apply message filter if specified
    if (_messageFilter is not null && !_messageFilter(message)) {
      Console.WriteLine("[RECEPTOR] Message FILTERED OUT by custom filter");
      return ValueTask.CompletedTask;  // Message doesn't match filter
    }

    // Check if we have lifecycle context (set via SetLifecycleContext before this method)
    if (LastLifecycleContext is not null) {
      Console.WriteLine($"[RECEPTOR] Lifecycle context available:");
      Console.WriteLine($"[RECEPTOR]   - Current stage: {LastLifecycleContext.CurrentStage}");
      Console.WriteLine($"[RECEPTOR]   - Perspective name: {LastLifecycleContext.PerspectiveName ?? "NULL"}");
      Console.WriteLine($"[RECEPTOR]   - Stream ID: {LastLifecycleContext.StreamId}");

      // Validate stage if specified
      if (_expectedStage.HasValue && LastLifecycleContext.CurrentStage != _expectedStage.Value) {
        Console.WriteLine($"[RECEPTOR] FILTERED: Expected stage {_expectedStage.Value}, got {LastLifecycleContext.CurrentStage}");
        return ValueTask.CompletedTask;
      }

      // Validate perspective name if specified
      if (_perspectiveName is not null && LastLifecycleContext.PerspectiveName != _perspectiveName) {
        Console.WriteLine($"[RECEPTOR] FILTERED: Expected perspective '{_perspectiveName}', got '{LastLifecycleContext.PerspectiveName}'");
        return ValueTask.CompletedTask;
      }

      Console.WriteLine("[RECEPTOR] ✓ Context validation PASSED");
    } else {
      Console.WriteLine("[RECEPTOR] WARNING: ILifecycleContext NOT available - cannot validate stage/perspective");
    }

    // Increment invocation count ONLY if we passed all filters
    var newCount = Interlocked.Increment(ref _invocationCount);
    Console.WriteLine($"[RECEPTOR] Invocation count incremented to: {newCount}");

    // Signal completion (use TrySetResult to handle multiple invocations)
    var signaled = _completionSource.TrySetResult(true);
    if (signaled) {
      Console.WriteLine("[RECEPTOR] ✓✓✓ Completion signaled SUCCESSFULLY!");
    } else {
      Console.WriteLine("[RECEPTOR] Completion already signaled (duplicate invocation)");
    }

    Console.WriteLine("##########################################################");
    return ValueTask.CompletedTask;
  }

  /// <summary>
  /// Sets the lifecycle context (called by test infrastructure when context is available).
  /// </summary>
  /// <param name="context">The lifecycle context from the current invocation.</param>
  public void SetLifecycleContext(ILifecycleContext context) {
    LastLifecycleContext = context;

    // Note: Stage and perspective filtering is done in HandleAsync()
    // We just store the context here for validation later
  }

  /// <summary>
  /// Resets the receptor state for reuse in multiple test iterations.
  /// </summary>
  public void Reset() {
    _invocationCount = 0;
    LastMessage = default;
    LastLifecycleContext = null;
  }
}
