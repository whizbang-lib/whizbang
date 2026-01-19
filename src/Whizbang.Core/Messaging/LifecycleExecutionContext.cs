namespace Whizbang.Core.Messaging;

/// <summary>
/// Default implementation of <see cref="ILifecycleContext"/> that provides contextual information
/// about lifecycle stage invocations.
/// </summary>
/// <remarks>
/// This record is typically instantiated by infrastructure code (e.g., PerspectiveWorker, Dispatcher)
/// when invoking lifecycle receptors. User code rarely needs to create instances directly.
/// Uses record type for convenient 'with' syntax when updating context properties.
/// </remarks>
/// <docs>core-concepts/lifecycle-receptors</docs>
public sealed record LifecycleExecutionContext : ILifecycleContext {
  /// <inheritdoc/>
  public required LifecycleStage CurrentStage { get; init; }

  /// <inheritdoc/>
  public Guid? EventId { get; init; }

  /// <inheritdoc/>
  public Guid? StreamId { get; init; }

  /// <inheritdoc/>
  public Type? PerspectiveType { get; init; }

  /// <inheritdoc/>
  public Guid? LastProcessedEventId { get; init; }

  /// <inheritdoc/>
  public MessageSource? MessageSource { get; init; }

  /// <inheritdoc/>
  public int? AttemptNumber { get; init; }
}
