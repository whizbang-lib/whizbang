// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Observability;

/// <summary>
/// Rejects a <see cref="TableStatisticsOptions"/> setting no reading could honor, naming its configuration key,
/// when the options are first read rather than on the first sample.
/// </summary>
/// <remarks>Registered by <c>AddWhizbangTableStatistics</c> and checked at host start.</remarks>
/// <docs>operations/configuration/configuration-reference#tablestatisticsoptions</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsRegistrationTests.cs:AValueNoReadingCouldHonor_IsRefusedNamingItsKeyAsync</tests>
public sealed class TableStatisticsOptionsValidator : IValidateOptions<TableStatisticsOptions> {
  private const string SECTION = "Whizbang:TableStatistics:";

  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, TableStatisticsOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var failures = new List<string>();
    if (options.MaxTrackedTables < 1) {
      failures.Add(_failure(nameof(options.MaxTrackedTables), "must be at least 1", options.MaxTrackedTables));
    }
    if (options.EventTypeSampleInterval < TimeSpan.Zero) {
      failures.Add(_failure(nameof(options.EventTypeSampleInterval), "must not be negative; 00:00:00 turns the sample off",
        options.EventTypeSampleInterval));
    }
    if (!(options.EventTypeSamplePercent > 0 && options.EventTypeSamplePercent <= 100)) {
      failures.Add(_failure(nameof(options.EventTypeSamplePercent), "must be greater than 0 and at most 100",
        options.EventTypeSamplePercent));
    }
    if (options.MaxEventTypes < 1) {
      failures.Add(_failure(nameof(options.MaxEventTypes), "must be at least 1", options.MaxEventTypes));
    }
    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }

  private static string _failure(string key, string rule, object value) =>
    string.Create(CultureInfo.InvariantCulture, $"{SECTION}{key} {rule}; it is {value}.");
}
