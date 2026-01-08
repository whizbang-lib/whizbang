namespace Whizbang.Core.Messaging;

/// <summary>
/// Default implementation of <see cref="ILifecycleContext"/> that provides contextual information
/// about lifecycle stage invocations.
/// </summary>
/// <remarks>
/// This class is typically instantiated by infrastructure code (e.g., PerspectiveWorker, Dispatcher)
/// when invoking lifecycle receptors. User code rarely needs to create instances directly.
/// </remarks>
/// <docs>core-concepts/lifecycle-receptors</docs>
public sealed class LifecycleExecutionContext : ILifecycleContext {
  /// <inheritdoc/>
  public required LifecycleStage CurrentStage { get; init; }

  /// <inheritdoc/>
  public Guid? EventId { get; init; }

  /// <inheritdoc/>
  public Guid? StreamId { get; init; }

  /// <inheritdoc/>
  public string? PerspectiveName { get; init; }

  /// <inheritdoc/>
  public Guid? LastProcessedEventId { get; init; }
}
