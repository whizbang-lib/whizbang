// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;
using Whizbang.Core;

namespace Whizbang.Sagas;

/// <summary>
/// Concrete rollback signal the framework publishes, once per run, when a saga that signals its outcome ends in a status that rolls back, or when a caller rolls the run back explicitly.
/// </summary>
/// <remarks>
/// <para>
/// Concrete, following <see cref="SagaContinuationRequestedEvent"/>, so the framework constructs it with
/// no per-saga subclass. A consumer writes one receptor on this type, filters on
/// <see cref="SagaName"/>, and in it discard what the run's steps staged and compensate for what they applied.
/// </para>
/// <para>
/// A run gets this or its opposite, never both: the two share one claim per run (see
/// <c>SagaOutcomeGuard</c>), so whichever is asked for first is the outcome.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/commit-rollback</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaOutcomeTests.cs</tests>
public class SagaRolledBackEvent : SagaEventBase, ISagaRolledBackEvent {

  /// <summary>The name of the saga the run belongs to.</summary>
  public string SagaName { get; set; } = string.Empty;

  /// <summary>The run's entity id: the business entity the saga was initiated for.</summary>
  public Guid EntityId { get; set; }

  /// <summary>Stream id this signal is bound to (the run's own stream).</summary>
  /// <remarks>Stored on this stream; see <see cref="SagaFrameworkEventStreamIds"/>.</remarks>
  [StreamId]
  public Guid StreamId { get; set; }

  /// <inheritdoc />
  [JsonIgnore]
  public SagaOutcome Outcome => SagaOutcome.Rollback;

  /// <inheritdoc />
  public SagaStatus? FinalStatus { get; set; }

  /// <inheritdoc />
  public string? Reason { get; set; }
}
