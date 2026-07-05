using Whizbang.Core.Messaging;

namespace Whizbang.Testing.Tests.TestSupport;

/// <summary>
/// Immutable <see cref="ILifecycleContext"/> test double with init-settable properties.
/// </summary>
internal sealed class FakeLifecycleContext : ILifecycleContext {
  public LifecycleStage CurrentStage { get; init; }
  public Guid? EventId { get; init; }
  public Guid? StreamId { get; init; }
  public Type? PerspectiveType { get; init; }
  public Guid? LastProcessedEventId { get; init; }
  public MessageSource? MessageSource { get; init; }
  public int? AttemptNumber { get; init; }
  public ProcessingMode? ProcessingMode { get; init; }
}
