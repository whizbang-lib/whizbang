// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Rejects a <see cref="StreamIntegrityOptions.MaxRepairDrainBacklog"/> below one, and a
/// <see cref="StreamIntegrityOptions.RepairAnswerTimeoutMinutes"/> below one, and a re-attribution interval, batch size
/// or batches-per-pass bound below one (#2008), a paced backfill setting below one (#2092), an audit window cap below one
/// sequence or an audit window budget below one (#2107), an idle ledger re-check bound below one (#2115), and a negative outstanding repair-answer cap (#2184), where the options are read,
/// naming the configuration key.
/// </summary>
/// <remarks>
/// A bound of zero or less admits no deficit to the paced repair drain, so with auto-repair on nothing
/// is ever repaired and every comparison logs a deferral: a broken setting that reads as an unlucky
/// backlog. An answer timeout below one minute makes every requested bucket claimable again at once, which is the
/// re-asking storm the timeout exists to stop (#2029). Registered by <c>AddWhizbangWorkers</c> and checked at host start.
/// </remarks>
/// <docs>resilience/stream-integrity#repair-caps</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIntegrityOptionsValidatorTests.cs</tests>
public sealed class StreamIntegrityOptionsValidator : IValidateOptions<StreamIntegrityOptions> {
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, StreamIntegrityOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    List<string> failures = [];
    if (options.MaxRepairDrainBacklog < 1) {
      failures.Add(
        $"Whizbang:StreamIntegrity:MaxRepairDrainBacklog must be at least 1 (was {options.MaxRepairDrainBacklog}); " +
        "it bounds the paced repair drain's backlog, and a bound below one would admit nothing to repair.");
    }
    if (options.RepairAnswerTimeoutMinutes < 1) {
      failures.Add(
        $"Whizbang:StreamIntegrity:RepairAnswerTimeoutMinutes must be at least 1 (was {options.RepairAnswerTimeoutMinutes}); " +
        "a requested bucket waits that long for its answer, and a timeout below one minute asks again at once.");
    }
    _atLeastOne(failures, "ControlMessageTimeToLiveMinutes", options.ControlMessageTimeToLiveMinutes,
      "it is how long a directed integrity request lives at the broker, and below a minute it expires before its addressee reads it (#2078)");
    _atLeastOne(failures, "ReattributionIntervalSeconds", options.ReattributionIntervalSeconds,
      "it is the wait between re-attribution passes, and a wait below one second spins the loop");
    _atLeastOne(failures, "ReattributionBatchSize", options.ReattributionBatchSize,
      "it is the rows one re-attribution batch scans, and a batch below one walks nothing");
    _atLeastOne(failures, "MaxReattributionBatchesPerPass", options.MaxReattributionBatchesPerPass,
      "it bounds one re-attribution pass, and a bound below one runs no batch");
    _atLeastOne(failures, "BackfillWindowSize", options.BackfillWindowSize,
      "it is the events one backfill window asks for, and a window below one asks for nothing (#2092)");
    _atLeastOne(failures, "BackfillEventsPerMinute", options.BackfillEventsPerMinute,
      "it is the backfill's pace, and a pace below one never affords a window (#2092)");
    _atLeastOne(failures, "BackfillMaxConcurrentWindows", options.BackfillMaxConcurrentWindows,
      "it bounds the backfill windows awaiting an answer, and a bound below one requests none (#2092)");
    _atLeastOne(failures, "BackfillMaxWindowAttempts", options.BackfillMaxWindowAttempts,
      "it is how often an unanswered window is asked, and below one no window is ever asked (#2092)");
    _atLeastOne(failures, "MaxAuditWindowSequences", options.MaxAuditWindowSequences,
      "it is the widest window one audit answer covers, and a window below one sequence covers nothing, so no seal could ever advance (#2107)");
    _atLeastOne(failures, "MaxAuditWindowsPerAudit", options.MaxAuditWindowsPerAudit,
      "it is the windows one audit walks against an origin, and an audit is at least its first window (#2107)");
    _atLeastOne(failures, "LedgerRecheckIntervalSeconds", options.LedgerRecheckIntervalSeconds,
      "it is the wait between idle ledger re-check ticks, and a wait below one second spins the loop (#2115)");
    _atLeastOne(failures, "LedgerRecheckRowsPerTick", options.LedgerRecheckRowsPerTick,
      "it bounds the ledger rows one re-check tick claims, and a bound below one re-checks nothing (#2115)");
    _atLeastOne(failures, "LedgerRecheckAfterMinutes", options.LedgerRecheckAfterMinutes,
      "it is how stale a row's report must be before it is re-checked, and below one minute a row just reported is asked again (#2115)");
    if (options.MaxOutstandingRepairAnswers < 0) {
      failures.Add(
        $"Whizbang:StreamIntegrity:MaxOutstandingRepairAnswers must not be negative (was {options.MaxOutstandingRepairAnswers}); " +
        "it caps the repair answers awaited at once, and 0 turns the cap off (#2184).");
    }
    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }

  private static void _atLeastOne(List<string> failures, string key, int value, string reason) {
    if (value < 1) {
      failures.Add($"Whizbang:StreamIntegrity:{key} must be at least 1 (was {value}); {reason}.");
    }
  }
}
