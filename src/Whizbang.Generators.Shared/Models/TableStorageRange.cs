// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// The range PostgreSQL accepts for one <c>[PerspectiveTableStorage]</c> property, and where the values that declare
/// nothing end. The schema generators read only a value inside the range, and the analyzer reports a declared value
/// outside it (WHIZ804), so a value the server would refuse never reaches the schema pass.
/// </summary>
/// <param name="Property">The attribute property's name.</param>
/// <param name="DeclaredFrom">The smallest value that declares a setting; anything below keeps the server's value.</param>
/// <param name="Minimum">The smallest value PostgreSQL accepts.</param>
/// <param name="Maximum">The largest value PostgreSQL accepts.</param>
/// <docs>operations/diagnostics/whiz804</docs>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/SharedHelpersBranchTests.cs:TableStorageRange_AnIntegerOutsideTheRange_IsReportedAsync</tests>
public sealed record TableStorageRange(string Property, double DeclaredFrom, double Minimum, double Maximum) {
  private const double SCALE_FACTOR_MAXIMUM = 100;

  /// <summary><c>fillfactor</c>: 10 to 100 percent.</summary>
  public static readonly TableStorageRange FillFactor = new("FillFactor", 0, 10, 100);

  /// <summary><c>toast_tuple_target</c>: 128 to 8160 bytes; 0 also keeps the server's value.</summary>
  public static readonly TableStorageRange ToastTupleTarget = new("ToastTupleTarget", 1, 128, 8160);

  /// <summary><c>autovacuum_vacuum_threshold</c>: any count from 0.</summary>
  public static readonly TableStorageRange AutovacuumVacuumThreshold = new("AutovacuumVacuumThreshold", 0, 0, int.MaxValue);

  /// <summary><c>autovacuum_vacuum_scale_factor</c>: 0 to 100.</summary>
  public static readonly TableStorageRange AutovacuumVacuumScaleFactor = new("AutovacuumVacuumScaleFactor", 0, 0, SCALE_FACTOR_MAXIMUM);

  /// <summary><c>autovacuum_vacuum_insert_threshold</c>: any count from 0.</summary>
  public static readonly TableStorageRange AutovacuumInsertThreshold = new("AutovacuumInsertThreshold", 0, 0, int.MaxValue);

  /// <summary><c>autovacuum_vacuum_insert_scale_factor</c>: 0 to 100.</summary>
  public static readonly TableStorageRange AutovacuumInsertScaleFactor = new("AutovacuumInsertScaleFactor", 0, 0, SCALE_FACTOR_MAXIMUM);

  /// <summary><c>toast.autovacuum_vacuum_threshold</c>: any count from 0.</summary>
  public static readonly TableStorageRange ToastAutovacuumVacuumThreshold = new("ToastAutovacuumVacuumThreshold", 0, 0, int.MaxValue);

  /// <summary><c>toast.autovacuum_vacuum_scale_factor</c>: 0 to 100.</summary>
  public static readonly TableStorageRange ToastAutovacuumVacuumScaleFactor = new("ToastAutovacuumVacuumScaleFactor", 0, 0, SCALE_FACTOR_MAXIMUM);

  /// <summary>Every ranged property, each once.</summary>
  public static readonly IReadOnlyList<TableStorageRange> All = [
    FillFactor, ToastTupleTarget, AutovacuumVacuumThreshold, AutovacuumVacuumScaleFactor, AutovacuumInsertThreshold,
    AutovacuumInsertScaleFactor, ToastAutovacuumVacuumThreshold, ToastAutovacuumVacuumScaleFactor,
  ];

  /// <summary>Whether a number declares a setting PostgreSQL accepts.</summary>
  /// <param name="value">The declared number.</param>
  /// <returns>True when it is within <see cref="Minimum"/> and <see cref="Maximum"/>.</returns>
  public bool Accepts(double value) => value >= Minimum && value <= Maximum;

  /// <summary>Whether an attribute argument declares a setting that PostgreSQL would refuse.</summary>
  /// <param name="value">The argument's value: an <c>int</c> or a <c>double</c>; any other type declares nothing.</param>
  /// <returns>True for a declared number outside the range.</returns>
  public bool IsOutOfRange(object? value) =>
    _number(value) is { } number && number >= DeclaredFrom && !Accepts(number);

  private static double? _number(object? value) => value switch {
    int whole => whole,
    double fraction => fraction,
    _ => null,
  };
}
