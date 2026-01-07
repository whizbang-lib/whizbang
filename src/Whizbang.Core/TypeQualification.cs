using System;

namespace Whizbang.Core;

/// <summary>
/// Flag enum controlling how type names are formatted.
/// Individual flags can be combined to create custom formatting options.
/// </summary>
/// <remarks>
/// This enum uses the [Flags] attribute, allowing individual components to be combined
/// using bitwise OR operations. Composite presets are provided for common combinations.
/// </remarks>
[Flags]
public enum TypeQualification {
  /// <summary>
  /// No formatting applied. Use this as a base for custom combinations.
  /// </summary>
  None = 0,

  // Component flags (individual bits)

  /// <summary>
  /// Include the type name (e.g., "ProductCreatedEvent").
  /// </summary>
  TypeName = 1 << 0,  // 1

  /// <summary>
  /// Include the namespace (e.g., "ECommerce.Contracts.Events").
  /// </summary>
  Namespace = 1 << 1,  // 2

  /// <summary>
  /// Include the assembly name (e.g., "ECommerce.Contracts").
  /// </summary>
  Assembly = 1 << 2,  // 4

  /// <summary>
  /// Include the assembly version (e.g., "Version=1.0.0.0").
  /// </summary>
  Version = 1 << 3,  // 8

  /// <summary>
  /// Include the assembly culture (e.g., "Culture=neutral").
  /// </summary>
  Culture = 1 << 4,  // 16

  /// <summary>
  /// Include the assembly public key token (e.g., "PublicKeyToken=null").
  /// </summary>
  PublicKeyToken = 1 << 5,  // 32

  /// <summary>
  /// Add "global::" prefix to the type name.
  /// </summary>
  GlobalPrefix = 1 << 6,  // 64

  // Composite presets (combinations)

  /// <summary>
  /// Simple type name only (e.g., "ProductCreatedEvent").
  /// Equivalent to: TypeName
  /// </summary>
  Simple = TypeName,

  /// <summary>
  /// Namespace-qualified type name (e.g., "ECommerce.Contracts.Events.ProductCreatedEvent").
  /// Equivalent to: Namespace | TypeName
  /// </summary>
  NamespaceQualified = Namespace | TypeName,

  /// <summary>
  /// Type name with assembly (e.g., "ProductCreatedEvent, ECommerce.Contracts").
  /// Equivalent to: TypeName | Assembly
  /// </summary>
  AssemblyQualified = TypeName | Assembly,

  /// <summary>
  /// Fully qualified type name with namespace and assembly
  /// (e.g., "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts").
  /// Equivalent to: Namespace | TypeName | Assembly
  /// </summary>
  FullyQualified = Namespace | TypeName | Assembly,

  /// <summary>
  /// Type name with global:: prefix (e.g., "global::ECommerce.Contracts.Events.ProductCreatedEvent").
  /// Equivalent to: GlobalPrefix | Namespace | TypeName
  /// </summary>
  GlobalQualified = GlobalPrefix | Namespace | TypeName,

  /// <summary>
  /// Fully qualified type name with all assembly information including version, culture, and token
  /// (e.g., "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null").
  /// Equivalent to: Namespace | TypeName | Assembly | Version | Culture | PublicKeyToken
  /// </summary>
  FullyQualifiedWithVersion = Namespace | TypeName | Assembly | Version | Culture | PublicKeyToken
}
