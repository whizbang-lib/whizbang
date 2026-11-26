// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;

namespace Whizbang.Core.Startup;

#pragma warning disable S2325, CA1822, IDE0060 // RED stub, replaced by the implementation

/// <summary>
/// The fleet-wide cap on concurrent heavy starts (#2596), bound from <c>Whizbang:Database:StartupWork</c>. A host that
/// configures nothing is capped at <see cref="DEFAULT_MAX_CONCURRENT_HEAVY_STARTS"/>.
/// </summary>
/// <docs>operations/configuration/configuration-reference#startup-work</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Startup/StartupWorkSessionTests.cs</tests>
public sealed class StartupWorkOptions {
#pragma warning disable CA1707 // SCREAMING_CASE constants are the established convention here
  /// <summary>The cap when none is configured.</summary>
  public const int DEFAULT_MAX_CONCURRENT_HEAVY_STARTS = 2;
#pragma warning restore CA1707

  /// <summary>
  /// How many instances of one schema may run heavy startup work at once. An instance past the cap waits in the Converge
  /// stage for a permit. Default 2; must be at least 1.
  /// </summary>
  public int MaxConcurrentHeavyStarts { get; set; } = DEFAULT_MAX_CONCURRENT_HEAVY_STARTS;

  /// <summary>How often a waiting instance asks for a permit again. Default 1 second.</summary>
  public TimeSpan PermitPollInterval { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// How long an instance waits for a permit before it runs its heavy work without one, at Warning, so a fleet whose
  /// permit holders are stuck never keeps a new instance from starting. Default 5 minutes.
  /// </summary>
  public TimeSpan PermitWaitLimit { get; set; } = TimeSpan.FromMinutes(5);

  /// <summary>Throws when the options describe a configuration that cannot work.</summary>
  /// <exception cref="ArgumentOutOfRangeException">A cap below 1, or a non-positive interval or limit.</exception>
  public void Validate() => throw new NotImplementedException();
}
