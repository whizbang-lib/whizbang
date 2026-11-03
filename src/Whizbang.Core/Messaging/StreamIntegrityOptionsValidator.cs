// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Rejects a <see cref="StreamIntegrityOptions.MaxRepairDrainBacklog"/> below one, and a
/// <see cref="StreamIntegrityOptions.RepairAnswerTimeoutMinutes"/> below one, where the options are read, naming the
/// configuration key.
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
    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }
}
