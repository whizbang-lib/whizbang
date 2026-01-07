using System;

namespace Whizbang.Core;

/// <summary>
/// Flag enum controlling how type names are matched during fuzzy matching operations.
/// Individual flags can be combined to create custom matching behavior.
/// </summary>
/// <remarks>
/// This enum uses the [Flags] attribute, allowing individual matching options to be combined
/// using bitwise OR operations. Composite presets are provided for common combinations.
/// Each flag instructs the matcher to ignore a specific aspect of the type name during comparison.
/// </remarks>
[Flags]
public enum MatchStrictness {
  /// <summary>
  /// Exact matching required - all components must match exactly.
  /// This is the default behavior when no flags are set.
  /// </summary>
  None = 0,

  // Individual flags (can be combined)

  /// <summary>
  /// Perform case-insensitive comparison.
  /// "ProductCreatedEvent" matches "productcreatedevent".
  /// </summary>
  IgnoreCase = 1 << 0,  // 1

  /// <summary>
  /// Ignore assembly version, culture, and public key token during matching.
  /// "Type, Assembly, Version=1.0.0" matches "Type, Assembly, Version=2.0.0".
  /// </summary>
  IgnoreVersion = 1 << 1,  // 2

  /// <summary>
  /// Ignore the assembly name entirely during matching.
  /// "Namespace.Type, Assembly1" matches "Namespace.Type, Assembly2".
  /// </summary>
  IgnoreAssembly = 1 << 2,  // 4

  /// <summary>
  /// Ignore the namespace, keeping only the simple type name for matching.
  /// "Namespace.Type" matches "Type".
  /// When this flag is set, IgnoreAssembly is effectively implied.
  /// </summary>
  IgnoreNamespace = 1 << 3,  // 8

  // Composite presets (for convenience)

  /// <summary>
  /// Exact matching - all components must match exactly.
  /// Equivalent to: None
  /// </summary>
  Exact = None,

  /// <summary>
  /// Case-insensitive matching only.
  /// Equivalent to: IgnoreCase
  /// </summary>
  CaseInsensitive = IgnoreCase,

  /// <summary>
  /// Ignore version/culture/token information.
  /// Equivalent to: IgnoreVersion
  /// </summary>
  WithoutVersionInfo = IgnoreVersion,

  /// <summary>
  /// Match namespace and type name, ignoring assembly information.
  /// Equivalent to: IgnoreAssembly | IgnoreVersion
  /// Example: "Namespace.Type, Assembly1" matches "Namespace.Type, Assembly2"
  /// </summary>
  WithoutAssembly = IgnoreAssembly | IgnoreVersion,

  /// <summary>
  /// Match only the simple type name, ignoring all namespace and assembly information.
  /// Equivalent to: IgnoreNamespace | IgnoreAssembly | IgnoreVersion
  /// Example: "Any.Namespace.Type, Assembly" matches "Type"
  /// </summary>
  SimpleName = IgnoreNamespace | IgnoreAssembly | IgnoreVersion,

  /// <summary>
  /// Match simple type name with case-insensitive comparison.
  /// Equivalent to: SimpleName | IgnoreCase
  /// Example: "Any.Namespace.ProductCreatedEvent, Assembly" matches "productcreatedevent"
  /// </summary>
  SimpleNameCaseInsensitive = SimpleName | IgnoreCase
}
