// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The duty that lets only one blue-green rebuild of a table run at a time, fleet-wide (#2301): one per table, won
/// through the <see cref="Whizbang.Core.Startup.IDutyElector"/> like any other duty. Its name is made here and nowhere
/// else.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#one-blue-green-rebuild-per-table</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRebuilderBlueGreenTests.cs</tests>
public static class PerspectiveRebuildDuty {
#pragma warning disable CA1707 // SCREAMING_CASE constants are the established convention here
  /// <summary>What every table's rebuild duty is named with, before the table's name.</summary>
  public const string DUTY_PREFIX = "perspective-rebuild:";
#pragma warning restore CA1707

  /// <summary>
  /// The duty a blue-green rebuild of <paramref name="table"/> holds. The table's bare name is enough: a service's role
  /// assignments are kept in its own schema, so two services' tables of one name never share a duty.
  /// </summary>
  /// <param name="table">The bare table name.</param>
  /// <returns>The duty name.</returns>
  public static string For(string table) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    return DUTY_PREFIX + table;
  }
}
