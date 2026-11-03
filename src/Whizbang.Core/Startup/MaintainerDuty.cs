// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Startup;

/// <summary>
/// Whether fleet-wide background work (the maintenance sweep, claim pruning, partition maintenance, stream-integrity
/// work) is this instance's to do.
/// </summary>
/// <remarks>
/// <para>
/// When role assignment manages the <see cref="StartupDuties.MAINTAINER"/> duty, only the instance holding it does the
/// work, so N instances do it once instead of N times against the same shared tables (#1377, #2001). When role
/// assignment does not manage it (no duty loop registered, or a loop without that role), every instance does the work,
/// as before: there is no holder to defer to, and a gate that waited for one would disable the work it gates.
/// </para>
/// <para>
/// One helper, so every caller asks the same question the same way. The rule used to be written out in each caller,
/// and four copies of a three-clause condition are four chances for one to drift.
/// </para>
/// </remarks>
/// <docs>operations/startup/capabilities-and-duties</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/MaintainerDutyTests.cs</tests>
public static class MaintainerDuty {
  /// <summary>
  /// True when this instance holds the maintainer duty, or when role assignment does not manage that duty.
  /// </summary>
  /// <param name="services">The provider the duty loop (<see cref="DutyHolderWorker"/>) is registered in, if any.</param>
  /// <returns>Whether this instance should do the maintainer's work now.</returns>
  /// <docs>operations/startup/capabilities-and-duties</docs>
  /// <tests>tests/Whizbang.Core.Tests/Startup/MaintainerDutyTests.cs:WithTheMaintainerDutyAssigned_OnlyItsHolderHasATurnAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Startup/MaintainerDutyTests.cs:WithoutRoleAssignment_EveryInstanceHasATurnAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Startup/MaintainerDutyTests.cs:WithADutyLoopThatDoesNotManageTheMaintainerDuty_EveryInstanceHasATurnAsync</tests>
  public static bool IsThisInstancesTurn(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return services.GetService<DutyHolderWorker>() is not { } holder
      || !holder.Roles.Contains(StartupDuties.MAINTAINER, StringComparer.Ordinal)
      || holder.Holds(StartupDuties.MAINTAINER);
  }
}
