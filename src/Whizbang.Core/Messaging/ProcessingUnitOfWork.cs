using System;
using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Tracks a unit of work from channel processing (Outbox/Inbox/Perspective workers).
/// Similar to DispatchUnitOfWork but for worker-side processing.
/// Accumulates completions and failures to report back to process_work_batch.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerTests.cs</tests>
public class ProcessingUnitOfWork {
  /// <summary>
  /// Unique time-ordered identifier for this unit (Uuid7).
  /// Generated via Uuid7.NewUuid7().ToGuid() for chronological ordering.
  /// </summary>
  public Guid UnitId { get; init; }

  /// <summary>
  /// Work items in this unit (OutboxWork, InboxWork, or PerspectiveWork).
  /// Preserved in order received for deterministic processing.
  /// </summary>
  public List<object> WorkItems { get; init; } = [];

  /// <summary>
  /// When this unit was created.
  /// Used for metrics and timeout detection.
  /// </summary>
  public DateTimeOffset CreatedAt { get; init; }

  /// <summary>
  /// Completions to report back to process_work_batch.
  /// Accumulated as work items are successfully processed.
  /// </summary>
  public List<MessageCompletion> Completions { get; init; } = [];

  /// <summary>
  /// Failures to report back to process_work_batch.
  /// Accumulated when work items fail processing.
  /// </summary>
  public List<MessageFailure> Failures { get; init; } = [];
}
