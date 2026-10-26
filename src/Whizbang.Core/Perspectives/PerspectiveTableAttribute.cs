// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Names the table a perspective model is stored in, instead of the name generated from the model type.
/// Applied to the model class (not the perspective class), and honored by every driver.
/// </summary>
/// <remarks>
/// <para>
/// The name is used exactly as written: the <c>WhizbangStripTableNameSuffixes</c> and
/// <c>WhizbangTableNameSuffixesToStrip</c> build properties do not alter it. It must start with <c>wh_per_</c>, the
/// prefix the framework recognizes its perspective tables by, followed by lowercase ASCII letters, digits and
/// underscores, so the same unquoted name reaches every statement. It must fit the database's identifier limit
/// (63 bytes on PostgreSQL). A name that breaks either rule is a build error (WHIZ824, and WHIZ820 or WHIZ823).
/// </para>
/// <para>
/// Every perspective of the model shares the table. Two models resolving to the same table is a build error
/// (WHIZ825). Changing the name of an existing table renames it in place at the next start, keeping its rows.
/// </para>
/// </remarks>
/// <param name="name">The table name, such as <c>wh_per_orders</c>.</param>
/// <docs>fundamentals/perspectives/table-naming#explicit-table-names</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveTableAttributeTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveTableNameGoldenTests.cs:BothDrivers_WithSuffixStrippingOff_KeepTheManualNameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveTableNamingDiagnosticsTests.cs</tests>
/// <example>
/// <code>
/// [PerspectiveTable("wh_per_fulfillment_backlog")]
/// public record BacklogSnapshotModel(Guid Id);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class PerspectiveTableAttribute(string name) : Attribute {
  /// <summary>
  /// The table name.
  /// </summary>
  public string Name { get; } = name;
}
