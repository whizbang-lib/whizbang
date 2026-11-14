// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;

namespace Whizbang.Core.Configuration;

/// <summary>
/// Rejects a historical metadata conversion budget that would never convert anything, naming the configuration key.
/// </summary>
/// <remarks>Registered by <c>AddWhizbangWorkers</c> and checked at host start.</remarks>
/// <docs>data/event-store#history-conversion</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/MetadataHistoryConversionOptionsTests.cs:Validator_RejectsABudgetThatConvertsNothing_NamingTheKeyAsync</tests>
public sealed class MetadataHistoryConversionOptionsValidator : IValidateOptions<MetadataHistoryConversionOptions> {
  private const string SECTION = "Whizbang:Workers:Maintenance:MetadataHistoryConversion:";

  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, MetadataHistoryConversionOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var failures = new List<string>();
    if (options.BatchSize < 1) {
      failures.Add($"{SECTION}{nameof(options.BatchSize)} must be at least 1; it is {options.BatchSize}.");
    }
    if (options.MaxBatchesPerCycle < 1) {
      failures.Add($"{SECTION}{nameof(options.MaxBatchesPerCycle)} must be at least 1; it is {options.MaxBatchesPerCycle}.");
    }
    if (options.TimeBudget <= TimeSpan.Zero) {
      failures.Add($"{SECTION}{nameof(options.TimeBudget)} must be greater than zero; it is {options.TimeBudget}.");
    }
    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }
}
