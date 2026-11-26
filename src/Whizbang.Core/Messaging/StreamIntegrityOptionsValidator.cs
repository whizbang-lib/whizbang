// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Rejects a <see cref="StreamIntegrityOptions.MaxRepairDrainBacklog"/> below one, and a
/// <see cref="StreamIntegrityOptions.RepairAnswerTimeoutMinutes"/> below one, and a re-attribution interval, batch size
/// or batches-per-pass bound below one (#2008), a paced backfill setting below one (#2092), an audit window cap below one
/// sequence or an audit window budget below one (#2107), an idle ledger re-check bound below one (#2115), a negative outstanding repair-answer cap (#2184), and an unowned-type origin silence below one day (#2185), and a sealing or seal re-check limit below its floor (#2353), where the options are read,
/// naming the configuration key.
/// </summary>
/// <remarks>
/// A bound of zero or less admits no deficit to the paced repair drain, so with auto-repair on nothing
/// is ever repaired and every comparison logs a deferral: a broken setting that reads as an unlucky
/// backlog. An answer timeout below one minute makes every requested bucket claimable again at once, which is the
/// re-asking storm the timeout exists to stop (#2029). Registered by <c>AddWhizbangWorkers</c> and checked at host start.
/// </remarks>
/// <docs>resilience/stream-integrity#repair-caps</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/StreamIntegrityOptionsValidatorTests.cs</tests>
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
    _atLeastOne(failures, "UnownedTypeOriginSilenceDays", options.UnownedTypeOriginSilenceDays,
      "it is how long an origin may be silent before its ledger rows for a type no service owns are forgotten, and below a "
      + "day an origin between two checkpoints would read as silent (#2185)");
    _atLeastOne(failures, "CompletionWindowMinutes", options.CompletionWindowMinutes,
      "it is the window the close ratio and the backlog ETA are computed over, and an empty window divides by nothing (#2261, #2317)");
    _atLeastOne(failures, "Sealing:EpochWidth", options.Sealing.EpochWidth,
      "it is how many sequence positions one sealed epoch covers, and an epoch below one position covers nothing (#2353)");
    _atLeastOne(failures, "Sealing:MaxClosuresPerCycle", options.Sealing.MaxClosuresPerCycle,
      "it bounds the epochs one maintenance cycle seals, and a bound below one seals nothing, so every audit keeps paying the full-history cost (#2353)");
    _atLeastOne(failures, "SealRecheck:MaxEpochsPerRun", options.SealRecheck.MaxEpochsPerRun,
      "it bounds the epochs one re-check run recomputes, and a bound below one re-checks nothing (#2353)");
    _positive(failures, "SealRecheck:Lookback", options.SealRecheck.Lookback,
      "it is how far back, by seal time, the re-check reaches, and an empty lookback reaches no epoch (#2353)");
    _positive(failures, "SealRecheck:RecheckEvery", options.SealRecheck.RecheckEvery,
      "it is how often one epoch may be re-checked, and an empty interval re-checks the same epochs on every run (#2353)");
    _positive(failures, "FullHistoryCompare:Lookback", options.FullHistoryCompare.Lookback,
      "it is how far back the full-history compare reaches, and an empty lookback compares nothing (#2361)");
    _positive(failures, "PairGuard:HaltedAlertAfter", options.PairGuard.HaltedAlertAfter,
      "it is how long a service pair may stay halted before the guard alerts, and an empty threshold alerts on every halt, "
      + "including the brief ones every rollout makes (#2362)");
    _atLeastOne(failures, "FullHistoryCompare:MaxPerRun", options.FullHistoryCompare.MaxPerRun,
      "it bounds the streams one compare run heals, and a bound below one heals nothing, so the pass never ends (#2361)");
    _atLeastOne(failures, "OriginPositionFill:BatchSize", options.OriginPositionFill.BatchSize,
      "it is the events one position request names, and a batch below one asks for nothing (#2544)");
    _atLeastOne(failures, "OriginPositionFill:MaxBatchesPerCycle", options.OriginPositionFill.MaxBatchesPerCycle,
      "it bounds the position requests one maintenance cycle sends, and a bound below one sends none (#2544)");
    _positive(failures, "OriginPositionFill:LapPause", options.OriginPositionFill.LapPause,
      "it is the pause before a finished walk asks again, and an empty pause re-asks every cycle (#2544)");
    if (options.MaxOutstandingRepairAnswers < 0) {
      failures.Add(
        $"Whizbang:StreamIntegrity:MaxOutstandingRepairAnswers must not be negative (was {options.MaxOutstandingRepairAnswers}); " +
        "it caps the repair answers awaited at once, and 0 turns the cap off (#2184).");
    }
    if (options.RepairReceptorLookback < TimeSpan.Zero) {
      failures.Add(
        $"Whizbang:StreamIntegrity:RepairReceptorLookback must not be negative (was {options.RepairReceptorLookback}); " +
        "it is how far back a repaired event can be proven never handled, and zero turns receptors on repair off (#2273).");
    }
    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }

  private static void _atLeastOne(List<string> failures, string key, long value, string reason) {
    if (value < 1) {
      failures.Add($"Whizbang:StreamIntegrity:{key} must be at least 1 (was {value}); {reason}.");
    }
  }

  private static void _positive(List<string> failures, string key, TimeSpan value, string reason) {
    if (value <= TimeSpan.Zero) {
      failures.Add($"Whizbang:StreamIntegrity:{key} must be positive (was {value}); {reason}.");
    }
  }
}
