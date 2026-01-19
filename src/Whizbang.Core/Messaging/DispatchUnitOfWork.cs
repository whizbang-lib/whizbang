using System;
using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Tracks a unit of work originating from Dispatcher.PublishAsync or Dispatcher.SendAsync.
/// Contains messages queued together for a single process_work_batch call.
/// Unit ID is time-ordered (Uuid7) for chronological processing.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IUnitOfWorkStrategyContractTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateUnitOfWorkStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedUnitOfWorkStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalUnitOfWorkStrategyTests.cs</tests>
public class DispatchUnitOfWork {
  /// <summary>
  /// Unique time-ordered identifier for this unit (Uuid7).
  /// Generated via Uuid7.NewUuid7().ToGuid() for chronological ordering.
  /// </summary>
  public Guid UnitId { get; init; }

  /// <summary>
  /// Messages in this unit (Commands or Events).
  /// Preserved in order queued for deterministic processing.
  /// </summary>
  public List<object> Messages { get; init; } = [];

  /// <summary>
  /// When this unit was created.
  /// Used for metrics and timeout detection.
  /// </summary>
  public DateTimeOffset CreatedAt { get; init; }

  /// <summary>
  /// Lifecycle stage assignments for each message.
  /// Key = message instance, Value = lifecycle stage.
  /// Determines when receptors execute relative to database operations.
  /// </summary>
  public Dictionary<object, LifecycleStage> LifecycleStages { get; init; } = [];
}
