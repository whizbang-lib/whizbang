// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;

namespace Whizbang.Core.Workers;

/// <summary>
/// Rejects a negative retention window in <see cref="MaintenanceWorkerOptions.Retention"/> where the options are read,
/// naming the configuration key, instead of letting it reach the sweep.
/// </summary>
/// <remarks>
/// The sweep turns each window into <c>NOW() - n * INTERVAL</c>. A negative value puts the cutoff in the future,
/// so every row of that table becomes eligible at once. Registered by <c>AddWhizbangWorkers</c> and checked at
/// host start.
/// </remarks>
/// <docs>operations/configuration/configuration-reference#database-side-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsValidationTests.cs</tests>
public sealed class MaintenanceWorkerOptionsValidator : IValidateOptions<MaintenanceWorkerOptions> {
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, MaintenanceWorkerOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var failures = RetentionSettingsBridge.Validate(options.Retention);
    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }
}
