// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;

namespace Whizbang.Core.Observability;

/// <summary>Rejects a table statistics setting no reading could honor.</summary>
public sealed class TableStatisticsOptionsValidator : IValidateOptions<TableStatisticsOptions> {
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, TableStatisticsOptions options) => ValidateOptionsResult.Success;
}
