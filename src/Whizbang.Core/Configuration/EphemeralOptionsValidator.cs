// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Configuration;

/// <summary>
/// Rejects a negative ephemeral retention setting in <see cref="EphemeralOptions"/> where the options are read,
/// naming the configuration key, instead of letting it reach the sweep.
/// </summary>
/// <remarks>Registered by <c>AddWhizbangWorkers</c> and checked at host start.</remarks>
/// <docs>operations/configuration/configuration-reference#database-side-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsValidationTests.cs</tests>
public sealed class EphemeralOptionsValidator : IValidateOptions<EphemeralOptions> {
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, EphemeralOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var failures = RetentionSettingsBridge.Validate(options);
    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }
}
