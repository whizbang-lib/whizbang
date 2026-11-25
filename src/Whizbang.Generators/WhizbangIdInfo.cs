// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;

namespace Whizbang.Generators;

/// <summary>
/// Discovery source for a WhizbangId.
/// Indicates how the ID type was discovered during source generation.
/// </summary>
/// <tests>No tests found</tests>
public enum DiscoverySource {
  /// <summary>
  /// Discovered via explicit type declaration with [WhizbangId] attribute.
  /// Example: [WhizbangId] public readonly partial struct ProductId;
  /// </summary>
  /// <tests>No tests found</tests>
  ExplicitType,

  /// <summary>
  /// Discovered via property with [WhizbangId] attribute.
  /// Example: public class Foo { [WhizbangId] public ProductId Id { get; set; } }
  /// </summary>
  /// <tests>No tests found</tests>
  Property,

  /// <summary>
  /// Discovered via primary constructor parameter with [WhizbangId] attribute.
  /// Example: public record Msg([WhizbangId] ProductId Id);
  /// </summary>
  /// <tests>No tests found</tests>
  Parameter
}

/// <summary>
/// Value type containing information about a discovered WhizbangId.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="TypeName">Simple type name without namespace (e.g., "ProductId")</param>
/// <param name="Namespace">Namespace where the ID type should be generated (e.g., "MyApp.Domain")</param>
/// <param name="Source">How the ID was discovered (ExplicitType, Property, or Parameter)</param>
/// <param name="SuppressDuplicateWarning">True if WHIZ024 warning should be suppressed for this ID</param>
/// <param name="Location">Where the [WhizbangId] attribute is written; WHIZ024 is reported there. Not part of equality.</param>
/// <param name="AcceptAnyVersion">True if the generated id takes any non-empty Guid rather than UUIDv7 only (#2071)</param>
/// <param name="Versions">The UUID versions the id accepts, as a <see cref="UuidVersionMask"/> (#2516). Ignored when
/// <paramref name="AcceptAnyVersion"/> is true.</param>
/// <param name="DeclaresVersions">True if <c>Versions</c> is written on this declaration's attribute, so writing it
/// next to <c>AcceptAnyVersion = true</c> can be reported (WHIZ039).</param>
/// <param name="OutOfRangeVersionBits">The bits of the written <c>Versions</c> outside versions 1 to 8, which name no
/// version and are reported (WHIZ044); zero when there are none.</param>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdTypeInfoTests.cs</tests>
public sealed record WhizbangIdInfo(
  string TypeName,
  string Namespace,
  DiscoverySource Source,
  bool SuppressDuplicateWarning = false,
  Location? Location = null,
  bool AcceptAnyVersion = false,
  int Versions = UuidVersionMask.V7,
  bool DeclaresVersions = false,
  int OutOfRangeVersionBits = 0
) {
  /// <summary>
  /// Gets the fully qualified name of the ID type (Namespace.TypeName).
  /// Used for deduplication and collision detection.
  /// </summary>
  /// <tests>No tests found</tests>
  public string FullyQualifiedName => Namespace.Length == 0 ? TypeName : $"{Namespace}.{TypeName}";

  /// <summary>
  /// Value equality is used for deduplication.
  /// Two WhizbangIdInfo instances are equal if they have the same FullyQualifiedName and version rule.
  /// </summary>
  /// <remarks>
  /// AcceptAnyVersion and Versions change the generated code, and DeclaresVersions and OutOfRangeVersionBits the
  /// diagnostics, so they take
  /// part: an id whose option was toggled must not compare equal to the cached one, or the incremental pipeline
  /// would keep the old output.
  /// </remarks>
  /// <tests>tests/Whizbang.Generators.Tests/WhizbangIdInfoEqualityTests.cs</tests>
  public bool Equals(WhizbangIdInfo? other) =>
    other is not null
    && FullyQualifiedName == other.FullyQualifiedName
    && AcceptAnyVersion == other.AcceptAnyVersion
    && Versions == other.Versions
    && DeclaresVersions == other.DeclaresVersions
    && OutOfRangeVersionBits == other.OutOfRangeVersionBits;

  /// <summary>
  /// Hash code based on FullyQualifiedName and the version rule.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/WhizbangIdInfoEqualityTests.cs</tests>
  public override int GetHashCode() {
    var hash = (FullyQualifiedName.GetHashCode() * 397) ^ AcceptAnyVersion.GetHashCode();
    hash = (hash * 397) ^ Versions;
    hash = (hash * 397) ^ DeclaresVersions.GetHashCode();
    return (hash * 397) ^ OutOfRangeVersionBits;
  }
}
