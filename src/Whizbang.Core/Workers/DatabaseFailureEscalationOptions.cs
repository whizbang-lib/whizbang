// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;

namespace Whizbang.Core.Workers;

/// <summary>
/// When a worker loop's run of transient database failures stops being a blip and becomes worth an alert
/// (<see cref="DatabaseFailureEscalation"/>). Bound from <c>Whizbang:DatabaseFailureEscalation</c>.
/// </summary>
/// <remarks>
/// <para>Each transient database failure a worker loop meets is logged at Warning by the worker, with its reason
/// and SQLSTATE: the batch goes back, the loop retries, and nothing is lost. A failover of a second or two
/// therefore logs a handful of Warnings and no Error. The run escalates, with one Error naming how long the
/// database has been failing, once it has lasted at least <see cref="FailingFor"/> <em>and</em> counted at least
/// <see cref="ConsecutiveFailures"/> failures in a row. The worker logs once more, at Information, when the
/// database answers again.</para>
/// <para>Both conditions must hold, so one can be switched off with the other's help: a
/// <see cref="FailingFor"/> of zero escalates on the count alone, and a <see cref="ConsecutiveFailures"/> of 1
/// escalates on the time alone. A value that cannot be met as written (a count below 1, a negative time) is
/// refused when the host starts (<see cref="DatabaseFailureEscalationOptionsValidator"/>).</para>
/// </remarks>
/// <docs>fundamentals/workers/transient-database-failures#when-the-database-keeps-failing</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationOptionsTests.cs</tests>
public sealed class DatabaseFailureEscalationOptions {
  /// <summary>The configuration section the options bind from.</summary>
  public const string SECTION = "Whizbang:DatabaseFailureEscalation";

  /// <summary>
  /// How many transient database failures in a row a worker meets before its run can escalate. At least 1.
  /// </summary>
  /// <docs>fundamentals/workers/transient-database-failures#when-the-database-keeps-failing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationOptionsTests.cs:Defaults_AreThreeFailuresOverAMinuteAsync</tests>
  public int ConsecutiveFailures { get; set; }

  /// <summary>
  /// How long a worker's run of transient database failures lasts, from its first failure, before it can escalate.
  /// Zero or more.
  /// </summary>
  /// <docs>fundamentals/workers/transient-database-failures#when-the-database-keeps-failing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationOptionsTests.cs:Defaults_AreThreeFailuresOverAMinuteAsync</tests>
  public TimeSpan FailingFor { get; set; }
}

/// <summary>
/// Refuses a <see cref="DatabaseFailureEscalationOptions"/> that cannot be met as written, naming the configuration
/// key. Registered with the options and checked when the host starts.
/// </summary>
/// <docs>fundamentals/workers/transient-database-failures#when-the-database-keeps-failing</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationOptionsTests.cs</tests>
public sealed class DatabaseFailureEscalationOptionsValidator : IValidateOptions<DatabaseFailureEscalationOptions> {
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, DatabaseFailureEscalationOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return ValidateOptionsResult.Success;
  }
}
