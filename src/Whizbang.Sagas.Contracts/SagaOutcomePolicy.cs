// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Sagas;

/// <summary>
/// How a saga that signals its outcome maps the status it ended in to commit or rollback.
/// </summary>
/// <remarks>
/// <para>
/// The one place the mapping lives. <see cref="SagaStatus.Completed"/> commits;
/// <see cref="SagaStatus.Failed"/> and <see cref="SagaStatus.Abandoned"/> roll back, because a saga that
/// stopped partway left its earlier steps' effects behind with nothing to finish them.
/// <see cref="SagaStatus.CompletedWithFailures"/> is the one a saga chooses, through
/// <see cref="OnCompletedWithFailures"/>: a run where some items failed is a failure for a saga that
/// must be all-or-nothing, and an acceptable result for one that tolerates gaps.
/// </para>
/// <para>
/// A non-terminal status has no outcome. <see cref="SagaStatus.Running"/> means the saga is still
/// going, and <see cref="SagaStatus.Reset"/> is a transition marker rather than a resting state.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/commit-rollback#which-status-commits</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaOutcomeTests.cs</tests>
/// <param name="onCompletedWithFailures">
/// The outcome of a saga that ran to the end with some items failed. Defaults to
/// <see cref="SagaOutcome.Rollback"/>.
/// </param>
public sealed class SagaOutcomePolicy(SagaOutcome onCompletedWithFailures = SagaOutcome.Rollback) {

  /// <summary>The policy a saga gets when it declares nothing beyond opting in.</summary>
  public static SagaOutcomePolicy Default { get; } = new();

  /// <summary>The outcome of a saga that ran to the end with some items failed.</summary>
  public SagaOutcome OnCompletedWithFailures { get; } = onCompletedWithFailures;

  /// <summary>The outcome a saga that ended in <paramref name="finalStatus"/> signals.</summary>
  /// <param name="finalStatus">The status the saga ended in.</param>
  /// <returns>The outcome, or <see langword="null"/> when the status is not terminal.</returns>
  public SagaOutcome? OutcomeFor(SagaStatus finalStatus) => finalStatus switch {
    SagaStatus.Completed => SagaOutcome.Commit,
    SagaStatus.CompletedWithFailures => OnCompletedWithFailures,
    SagaStatus.Failed or SagaStatus.Abandoned => SagaOutcome.Rollback,
    _ => null,
  };
}
