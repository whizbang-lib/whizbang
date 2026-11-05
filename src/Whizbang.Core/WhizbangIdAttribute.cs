// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;

namespace Whizbang.Core;

/// <summary>
/// Marks a struct, property, or parameter as a strongly-typed Whizbang ID.
/// The source generator will create a complete value object implementation with
/// UUIDv7 support, equality comparisons, JSON serialization, and auto-registration.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/WhizbangIdAttributeTests.cs</tests>
/// <remarks>
/// <para>
/// This attribute supports three discovery patterns:
/// </para>
/// <para>
/// <strong>1. Type-based (Explicit Struct):</strong>
/// <code>
/// [WhizbangId]
/// public readonly partial struct ProductId;
/// </code>
/// </para>
/// <para>
/// <strong>2. Property-based (Inferred from Property):</strong>
/// <code>
/// public class CreateProductCommand {
///   [WhizbangId]
///   public ProductId Id { get; set; }
/// }
/// </code>
/// </para>
/// <para>
/// <strong>3. Parameter-based (Primary Constructor):</strong>
/// <code>
/// public record CreateProductCommand(
///   [WhizbangId] ProductId Id,
///   string Name
/// );
/// </code>
/// </para>
/// <para>
/// The generator creates a readonly struct with:
/// <list type="bullet">
/// <item>Guid-backed storage with UUIDv7 generation</item>
/// <item>Value equality and comparison operators</item>
/// <item>JSON serialization support (System.Text.Json)</item>
/// <item>ToString() implementation</item>
/// <item>Implicit/explicit conversions to/from Guid</item>
/// <item>Static factory methods: New() and From(Guid)</item>
/// </list>
/// </para>
/// <para>
/// <strong>Namespace Control:</strong>
/// By default, IDs are generated in the same namespace as the declaring type.
/// Override using the Namespace property:
/// <code>
/// [WhizbangId(Namespace = "MyApp.Domain.Ids")]
/// public readonly partial struct ProductId;
/// </code>
/// </para>
/// <para>
/// <strong>Collision Handling:</strong>
/// If the same ID type name exists in different namespaces, a warning (WHIZ024) is emitted.
/// Suppress this warning per-ID using:
/// <code>
/// [WhizbangId(SuppressDuplicateWarning = true)]
/// public readonly partial struct ProductId;
/// </code>
/// </para>
/// </remarks>
/// <example>
/// Complete example with all three patterns:
/// <code>
/// // 1. Explicit type definition
/// [WhizbangId]
/// public readonly partial struct ProductId;
///
/// // 2. Property inference
/// public class ProductCreatedEvent {
///   [WhizbangId]
///   public ProductId Id { get; init; }
///   public string Name { get; init; }
/// }
///
/// // 3. Primary constructor parameter
/// public record CreateProductCommand(
///   [WhizbangId] ProductId Id,
///   string Name
/// );
///
/// // Usage
/// var productId = ProductId.New(); // UUIDv7
/// var command = new CreateProductCommand(productId, "Widget");
/// </code>
/// </example>
[AttributeUsage(
  AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Parameter,
  AllowMultiple = false,
  Inherited = false)]
public sealed class WhizbangIdAttribute(string? targetNamespace = null) : Attribute {
  /// <summary>
  /// Gets or sets the namespace where the ID type should be generated.
  /// If not specified, the ID is generated in the same namespace as the declaring type.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/WhizbangIdAttributeTests.cs:WhizbangIdAttribute_NamespaceProperty_CanBeSetAsync</tests>
  /// <remarks>
  /// This is useful when you want to consolidate all domain IDs in a single namespace,
  /// or when using property/parameter-based discovery and need to control placement.
  /// </remarks>
  /// <example>
  /// <code>
  /// [WhizbangId(Namespace = "MyApp.Domain.Ids")]
  /// public readonly partial struct ProductId;
  /// </code>
  /// </example>
  public string? Namespace { get; set; } = targetNamespace;

  /// <summary>
  /// Gets or sets whether to suppress the WHIZ024 warning when multiple ID types
  /// with the same name exist in different namespaces.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/WhizbangIdAttributeTests.cs:WhizbangIdAttribute_SuppressDuplicateWarningProperty_CanBeSetAsync</tests>
  /// <remarks>
  /// By default, the generator warns when it detects ID types with identical names
  /// in different namespaces to help identify potential confusion. Set this to true
  /// if the duplicate names are intentional.
  /// </remarks>
  /// <example>
  /// <code>
  /// // In Namespace A
  /// [WhizbangId(SuppressDuplicateWarning = true)]
  /// public readonly partial struct ProductId;
  ///
  /// // In Namespace B (no warning)
  /// [WhizbangId]
  /// public readonly partial struct ProductId;
  /// </code>
  /// </example>
  public bool SuppressDuplicateWarning { get; set; }

  /// <summary>
  /// Gets or sets whether the generated ID takes a Guid of any version, rather than UUIDv7 only.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/WhizbangIdAttributeTests.cs:WhizbangIdAttribute_AcceptAnyVersionProperty_CanBeSetAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdVersionAcceptanceTests.cs</tests>
  /// <remarks>
  /// <para>
  /// By default (<c>false</c>) <c>From(Guid)</c>, <c>From(TrackedGuid)</c>, <c>Parse</c>, the explicit cast and the
  /// generated JSON converter throw <see cref="ArgumentException"/> unless the Guid is UUIDv7. Set this to <c>true</c>
  /// for identities that are not, and cannot become, v7: name-derived ids (v5, v8), hash-derived ids, and v4 ids
  /// issued by an external identity provider.
  /// </para>
  /// <para>
  /// With the option, every way in (<c>From</c>, <c>Parse</c>, the cast, JSON in every profile, the public
  /// constructor and the <c>Value</c> initializer) takes any Guid except <see cref="Guid.Empty"/>, which throws
  /// <see cref="ArgumentException"/>. <c>New()</c> still produces a UUIDv7.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// [WhizbangId(AcceptAnyVersion = true)]
  /// public readonly partial struct ExternalUserId;
  ///
  /// var id = ExternalUserId.From(Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301")); // v4: accepted
  /// </code>
  /// </example>
  public bool AcceptAnyVersion { get; set; }
}
