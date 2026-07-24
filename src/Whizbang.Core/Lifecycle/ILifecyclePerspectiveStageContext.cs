using System;
using System.Collections.Generic;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Stage-specific context for perspective lifecycle stages.
/// Carries perspective-relevant information alongside the base lifecycle context.
/// </summary>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator#perspective-context</docs>
public interface ILifecyclePerspectiveStageContext {
  /// <summary>
  /// Gets the parent lifecycle context.
  /// </summary>
  ILifecycleContext Lifecycle { get; }

  /// <summary>
  /// Gets the names of perspectives being processed in this stage.
  /// </summary>
  IReadOnlyList<string> PerspectiveNames { get; }

  /// <summary>
  /// Gets the stream ID being processed.
  /// </summary>
  Guid StreamId { get; }

  /// <summary>
  /// Gets the last successfully processed event ID (checkpoint position).
  /// </summary>
  Guid? LastProcessedEventId { get; }

  /// <summary>
  /// Gets the perspective type being processed, if applicable.
  /// </summary>
  Type? PerspectiveType { get; }
}
