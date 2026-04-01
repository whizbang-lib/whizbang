using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Lifecycle.Integration.Tests.Domain;

// ============================================================
// Mock events, commands, and models for batch overflow testing.
// These exist to reproduce a bug where PostAllPerspectivesDetached
// fires N times per event (once per perspective/batch cycle)
// instead of once after ALL perspectives complete.
// ============================================================

/// <summary>Mock command that triggers a batch of events for testing.</summary>
public record MockBatchTestCommand : ICommand {
  [StreamId]
  public required Guid StreamId { get; init; }
  /// <summary>Number of noise events to generate alongside the main event.</summary>
  public int NoiseEventCount { get; init; } = 50;
}

/// <summary>The main event we track for PostAllPerspectivesDetached firing count.</summary>
[SignalTag(Tag = "mock-batch-test")]
public record MockBatchTestEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Noise event to flood the stream and force batch overflow.</summary>
public record MockBatchNoiseEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public int Index { get; init; }
}
