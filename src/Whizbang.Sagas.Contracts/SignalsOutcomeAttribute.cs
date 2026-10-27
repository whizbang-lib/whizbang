// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Sagas;

/// <summary>
/// Opts a saga in to a terminal commit-or-rollback signal: exactly one of
/// <c>SagaCommittedEvent</c> or <c>SagaRolledBackEvent</c> per run, whichever way the run ends.
/// </summary>
/// <remarks>
/// <para>
/// For a saga whose steps change state that must not outlive a failed run. An early step switches
/// which version of something is active, a later step fails, and without a signal the switch stays.
/// With one, the consumer's rollback receptor undoes it, and its commit receptor applies what the
/// steps staged.
/// </para>
/// <para>
/// The generator reads the declaration and emits its registration into a module initializer, so
/// nothing is discovered by reflection and it works for a saga generated with
/// <c>GenerateService = false</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/commit-rollback</docs>
/// <tests>tests/Whizbang.Sagas.Tests/Generators/SagaOutcomeGeneratorTests.cs</tests>
/// <example>
/// <code>
/// [Saga("ConfigurationRollout")]
/// [SignalsOutcome]
/// public partial class ConfigurationRolloutSaga;
///
/// // A saga that tolerates some failed items, and keeps what the rest did.
/// [Saga("ReportRefresh")]
/// [SignalsOutcome(OnCompletedWithFailures = SagaOutcome.Commit)]
/// public partial class ReportRefreshSaga;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SignalsOutcomeAttribute : Attribute {

  /// <summary>
  /// The outcome of a run that reached the end with some items failed. Defaults to
  /// <see cref="SagaOutcome.Rollback"/>: a saga that opted in to an outcome treats any failure as a
  /// failed run unless it says otherwise.
  /// </summary>
  public SagaOutcome OnCompletedWithFailures { get; init; } = SagaOutcome.Rollback;
}
