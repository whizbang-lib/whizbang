// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Sagas;

/// <summary>
/// The one terminal decision a saga that signals its outcome reaches: keep what it did, or undo it.
/// </summary>
/// <remarks>
/// <para>
/// A saga's <see cref="SagaStatus"/> says how it ended. The outcome says what the consumer should do
/// about it, and the two differ on purpose: four statuses end a saga, but a step that staged an effect
/// only needs to know whether to apply it or discard it.
/// </para>
/// <para>
/// The framework signals the outcome and does not act on it. Compensation order is domain-specific,
/// so what a commit applies and what a rollback undoes belong to the consumer's receptors.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/commit-rollback</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaOutcomeTests.cs</tests>
public enum SagaOutcome {
  /// <summary>Keep what the saga did: apply what its steps staged.</summary>
  Commit = 1,

  /// <summary>Undo what the saga did: discard what its steps staged and compensate for what they applied.</summary>
  Rollback = 2,
}
