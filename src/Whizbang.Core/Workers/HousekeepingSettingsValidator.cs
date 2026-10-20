// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;

namespace Whizbang.Core.Workers;

/// <summary>
/// Rejects a zero or negative <see cref="HousekeepingCoordinator.Settings.MaxDeferral"/> where the settings are
/// read, naming the configuration key, instead of letting it switch the idle gate off.
/// </summary>
/// <remarks>
/// The ceiling is reached when the time since the first deferral is at least <c>MaxDeferral</c>. At zero or
/// below that is true on the first deferral, so every busy request would be forced through and the gate would
/// defer nothing. Registered with the coordinator and checked at host start.
/// </remarks>
/// <docs>operations/workers/housekeeping-arbitration#precedence-over-the-legacy-count</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/HousekeepingDeferralCeilingTests.cs:ANonPositiveMaxDeferral_IsRejectedByNameAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/HousekeepingDeferralCeilingTests.cs:TheTurnkeyPipeline_RejectsANonPositiveMaxDeferralAsync</tests>
public sealed class HousekeepingSettingsValidator : IValidateOptions<HousekeepingCoordinator.Settings> {
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, HousekeepingCoordinator.Settings options) {
    ArgumentNullException.ThrowIfNull(options);
    return options.MaxDeferral > TimeSpan.Zero
      ? ValidateOptionsResult.Success
      : ValidateOptionsResult.Fail(
        $"Whizbang:Housekeeping:MaxDeferral must be positive; it is {options.MaxDeferral}. "
        + "A ceiling of zero or less forces every deferred pass through, which switches the idle gate off.");
  }
}
