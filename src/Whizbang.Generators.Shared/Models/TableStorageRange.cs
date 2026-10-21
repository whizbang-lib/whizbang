// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace Whizbang.Generators.Shared.Models;

/// <summary>STUB</summary>
public sealed record TableStorageRange(string Property, double DeclaredFrom, double Minimum, double Maximum) {
  /// <summary>STUB</summary>
  public static readonly TableStorageRange FillFactor = new("FillFactor", 0, 10, 100);
  /// <summary>STUB</summary>
  public static readonly TableStorageRange AutovacuumVacuumScaleFactor = new("AutovacuumVacuumScaleFactor", 0, 0, 100);
  /// <summary>STUB</summary>
  public static readonly IReadOnlyList<TableStorageRange> All = [];
  /// <summary>STUB</summary>
  public bool IsOutOfRange(object? value) => value is null && Minimum < 0;
}
