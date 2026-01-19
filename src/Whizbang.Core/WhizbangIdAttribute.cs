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
public sealed class WhizbangIdAttribute : Attribute {
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
  public string? Namespace { get; set; }

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
  /// Initializes a new instance of the <see cref="WhizbangIdAttribute"/> class.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/WhizbangIdAttributeTests.cs:WhizbangIdAttribute_DefaultConstructor_HasNullNamespaceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/WhizbangIdAttributeTests.cs:WhizbangIdAttribute_DefaultConstructor_HasFalseSuppressDuplicateWarningAsync</tests>
  public WhizbangIdAttribute() { }

  /// <summary>
  /// Initializes a new instance of the <see cref="WhizbangIdAttribute"/> class
  /// with the specified target namespace.
  /// </summary>
  /// <param name="targetNamespace">
  /// The namespace where the ID type should be generated.
  /// </param>
  /// <example>
  /// <code>
  /// [WhizbangId("MyApp.Domain.Ids")]
  /// public readonly partial struct ProductId;
  /// </code>
  /// </example>
  /// <tests>tests/Whizbang.Core.Tests/WhizbangIdAttributeTests.cs:WhizbangIdAttribute_ConstructorWithNamespace_SetsNamespacePropertyAsync</tests>
  public WhizbangIdAttribute(string? targetNamespace) {
    Namespace = targetNamespace;
  }
}
