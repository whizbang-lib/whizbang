using System;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Records timing information for a lifecycle stage that has been traversed.
/// Captured in <see cref="ILifecycleTracking"/> stage history for diagnostics.
/// </summary>
/// <param name="Stage">The lifecycle stage that was executed.</param>
/// <param name="Duration">How long the stage took to execute.</param>
/// <param name="StartedAt">When the stage began executing.</param>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator#diagnostics</docs>
public sealed record StageRecord(
  LifecycleStage Stage,
  TimeSpan Duration,
  DateTimeOffset StartedAt);
