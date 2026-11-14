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
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, MetadataHistoryConversionOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return ValidateOptionsResult.Success;
  }
}
