// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Observability;

/// <summary>
/// How a table's bloat ratio was obtained: estimated from the planner's statistics, or measured by reading the table.
/// </summary>
/// <remarks>
/// <para>
/// Every stored or reported ratio carries its method, so a reader can tell a guess from a measurement. The values are
/// stored and tagged by their stable names (<see cref="BloatMeasurementMethods.ToValue"/>), never by number.
/// </para>
/// <para>
/// The default is <see cref="Estimate"/>, which is what a ratio recorded before the method existed was.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/BloatMeasurementMethodTests.cs</tests>
public enum BloatMeasurementMethod {
  /// <summary>
  /// Estimated from the catalog (<c>pg_stats</c> row widths and the planner's row count) without reading the table.
  /// Stored as <c>estimate</c>.
  /// </summary>
  Estimate = 0,

  /// <summary>
  /// Measured with <c>pgstattuple_approx</c>, which reads the visibility map and scans only the pages that are not
  /// all-visible. Stored as <c>approx</c>.
  /// </summary>
  Approx = 1,

  /// <summary>
  /// Measured with <c>pgstattuple</c>, which scans the whole table. Never run automatically; the value exists so a
  /// measurement an operator takes can be recorded as what it is. Stored as <c>exact</c>.
  /// </summary>
  Exact = 2,
}

/// <summary>The stable names <see cref="BloatMeasurementMethod"/> is stored and tagged by.</summary>
/// <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/BloatMeasurementMethodTests.cs</tests>
public static class BloatMeasurementMethods {
#pragma warning disable CA1707
  /// <summary>The stored name of <see cref="BloatMeasurementMethod.Estimate"/>.</summary>
  public const string ESTIMATE = "estimate";

  /// <summary>The stored name of <see cref="BloatMeasurementMethod.Approx"/>.</summary>
  public const string APPROX = "approx";

  /// <summary>The stored name of <see cref="BloatMeasurementMethod.Exact"/>.</summary>
  public const string EXACT = "exact";
#pragma warning restore CA1707

  /// <summary>The stable name <paramref name="method"/> is stored and tagged by.</summary>
  /// <param name="method">The method.</param>
  /// <returns><c>estimate</c>, <c>approx</c> or <c>exact</c>.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="method"/> is not a defined value.</exception>
  public static string ToValue(this BloatMeasurementMethod method) => throw new NotImplementedException();


  /// <summary>The method a stored name stands for; a missing name is <see cref="BloatMeasurementMethod.Estimate"/>.</summary>
  /// <param name="value">The stored name, or null for a ratio recorded before the method was.</param>
  /// <returns>The method.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not one of the stable names.</exception>
  public static BloatMeasurementMethod Parse(string? value) => throw new NotImplementedException();

}

/// <summary>A table's bloat ratio and how it was obtained.</summary>
/// <param name="Ratio">Heap bytes per live row over the width those rows should need; about 1.0 is lean.</param>
/// <param name="Method">How the ratio was obtained.</param>
/// <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/BloatMeasurementMethodTests.cs</tests>
public readonly record struct BloatMeasurement(double Ratio, BloatMeasurementMethod Method);
