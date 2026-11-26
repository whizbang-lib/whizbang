// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The names the perspective runner gives an event's type (#2469), through the shared naming code: the forms the
/// generated runners read off <see cref="Type"/> directly before the runner logic moved into Whizbang.Core, unchanged.
/// </summary>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveRunnerTypeNamesTests.cs</tests>
internal static class PerspectiveRunnerTypeNames {
  /// <summary>
  /// The event type a row's metadata records for its last applied event, and a completion reports a purge by: the CLR
  /// full name, or the simple name for a type that has none.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveRunnerTypeNamesTests.cs:Recorded_IsTheClrFullName_OrTheSimpleNameWithoutOneAsync</tests>
  public static string Recorded(Type type) =>
      TypeNameFormatter.TryFormatClrTypeName(type, out var clrTypeName) ? clrTypeName : TypeNameFormatter.SimpleName(type);

  /// <summary>
  /// The key the compile-time receptor registry is asked under: the CLR full name, or empty for a type that has none.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveRunnerTypeNamesTests.cs:ReceptorKey_IsTheClrFullName_OrEmptyWithoutOneAsync</tests>
  public static string ReceptorKey(Type type) =>
      TypeNameFormatter.TryFormatClrTypeName(type, out var clrTypeName) ? clrTypeName : string.Empty;

  /// <summary>The CLR full name an apply span is tagged with, or null for a type that has none.</summary>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveRunnerTypeNamesTests.cs:SpanTag_IsTheClrFullName_OrNullWithoutOneAsync</tests>
  public static string? SpanTag(Type type) =>
      TypeNameFormatter.TryFormatClrTypeName(type, out var clrTypeName) ? clrTypeName : null;
}
