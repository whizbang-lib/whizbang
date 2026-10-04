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
/// <docs>fundamentals/receptors/lifecycle-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/LifecycleContextTests.cs:LifecycleExecutionContext_Constructor_StoresAllPropertiesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/LifecycleContextTests.cs:LifecycleExecutionContext_OptionalProperties_CanBeNullAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/LifecycleContextAccessorTests.cs:Current_DifferentAsyncContexts_AreIsolatedAsync</tests>
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

  /// <inheritdoc/>
  public ProcessingMode? ProcessingMode { get; init; }

  /// <inheritdoc/>
  public bool IsReplay =>
    ProcessingMode is Messaging.ProcessingMode.Replay or Messaging.ProcessingMode.Rebuild;

  /// <inheritdoc/>
  public bool IsNewEvent { get; init; } = true;

  /// <summary>
  /// The fields the message changed, when the stage knows them better than the message does: a collective event's
  /// applied specs (#1045). Tag hooks receive it as <see cref="Tags.TagContext{TAttribute}.Changes"/>.
  /// </summary>
  public Tags.MessageChanges? Changes { get; init; }
}
