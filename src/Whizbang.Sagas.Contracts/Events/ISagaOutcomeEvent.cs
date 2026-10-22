// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Sagas;

/// <summary>
/// The terminal outcome signal of a saga: <see cref="ISagaCommittedEvent"/> or
/// <see cref="ISagaRolledBackEvent"/>, published once per run.
/// </summary>
/// <docs>fundamentals/sagas/commit-rollback</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaOutcomeTests.cs</tests>
public interface ISagaOutcomeEvent : ISagaEvent {

  /// <summary>The saga's stream id: the run this outcome belongs to.</summary>
  Guid StreamId { get; }

  /// <summary>Whether the run is committed or rolled back.</summary>
  SagaOutcome Outcome { get; }

  /// <summary>
  /// The status the run ended in, or <see langword="null"/> for a rollback asked for explicitly
  /// before the run reached a terminal status (a cancel).
  /// </summary>
  SagaStatus? FinalStatus { get; }

  /// <summary>Why, in words: the caller's reason for an explicit rollback, otherwise a short account of the status.</summary>
  string? Reason { get; }
}
