using System;

namespace Whizbang.Core.Generated;

/// <summary>
/// Categories for diagnostic information from source generators.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Generated/DiagnosticCategoryTests.cs:DiagnosticCategory_None_HasValueZeroAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/DiagnosticCategoryTests.cs:DiagnosticCategory_All_CombinesAllCategoriesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/DiagnosticCategoryTests.cs:DiagnosticCategory_SupportsFlags_BitwiseOperationsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/DiagnosticCategoryTests.cs:DiagnosticCategory_IndividualFlags_HaveUniqueValuesAsync</tests>
[Flags]
public enum DiagnosticCategory {
  /// <summary>No category</summary>
  None = 0,
  /// <summary>Receptor discovery and registration diagnostics</summary>
  ReceptorDiscovery = 1 << 0,
  /// <summary>Dispatcher generation diagnostics</summary>
  Dispatcher = 1 << 1,
  /// <summary>Event handling diagnostics</summary>
  EventHandling = 1 << 2,
  /// <summary>All diagnostic categories</summary>
  All = ReceptorDiscovery | Dispatcher | EventHandling
}
