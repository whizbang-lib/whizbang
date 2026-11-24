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
/// <para>
/// <strong>Grandfathered values:</strong>
/// Every generated id declares <c>static partial void IsGrandfathered(Guid value, ref bool grandfathered)</c>. An id
/// type whose stored data holds a few values that predate its version rule (hand-typed seed ids, well-known system
/// identities) implements it to name them, and its checked entry points accept them while every other value keeps
/// the rule. Left unimplemented, the compiler removes the call. <see cref="Guid.Empty"/> is never grandfathered, and
/// <c>New()</c> mints UUIDv7 as always:
/// <code>
/// [WhizbangId]
/// public readonly partial struct ProductCatalogId {
///   // Seeded before ids were UUIDv7. Frozen: new values are always minted.
///   private static readonly FrozenSet&lt;Guid&gt; _grandfathered = new[] {
///     new Guid("3f2504e0-4f89-41d3-9a0c-0305e82c3301"),
///   }.ToFrozenSet();
///
///   static partial void IsGrandfathered(Guid value, ref bool grandfathered) =>
///     grandfathered = _grandfathered.Contains(value);
/// }
/// </code>
/// The hook runs for every value that breaks the rule, so keep it a set lookup. It is a void method with a
/// <c>ref</c> result because only a void partial method may be left unimplemented.
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
  /// <tests>tests/Whizbang.Generators.Tests/WhizbangIdAcceptAnyVersionTests.cs</tests>
  /// <remarks>
  /// <para>
  /// By default (<c>false</c>) <c>From(Guid)</c>, <c>From(TrackedGuid)</c>, <c>Parse</c>, the explicit cast and the
  /// generated JSON converter throw <see cref="ArgumentException"/> unless the Guid is UUIDv7. Set this to <c>true</c>
  /// for identities that are not, and cannot become, v7: name-derived ids (v5, v8), hash-derived ids, and v4 ids
  /// issued by an external identity provider.
  /// </para>
  /// <para>
  /// With the option, the checked entry points (<c>From</c>, <c>Parse</c>, the explicit cast and the transport JSON
  /// reader) take any Guid except <see cref="Guid.Empty"/>, which throws <see cref="ArgumentException"/>.
  /// <c>New()</c> still produces a UUIDv7. The public constructor and the <c>Value</c> initializer stay unchecked on
  /// every id, with or without the option: they are the load path for EF Core materialization and the persistence
  /// converter, so a stored row whose id field was never set still loads.
  /// </para>
  /// <para>
  /// On an id with the option, values are no longer known to be UUIDv7: <c>CompareTo</c> and the <c>&lt;</c>,
  /// <c>&lt;=</c>, <c>&gt;</c> and <c>&gt;=</c> operators no longer order by creation time, and for a value that is
  /// not UUIDv7, <c>IsTimeOrdered</c> is <c>false</c> and <c>GetTimestamp()</c> returns no meaningful time. The id
  /// type also loses the index locality that time-ordered values give a database. The framework's ordering
  /// guarantees are unaffected: they rely on the message and event ids the framework mints, and a stream id is only
  /// hashed for partitioning and sorted for lock ordering, neither of which depends on its version.
  /// </para>
  /// <para>
  /// Set the option the same way on every <c>[WhizbangId]</c> for an id (the struct, a property, a parameter). When
  /// they disagree, the id keeps the stricter rule of the others (UUIDv7, or their <see cref="Versions"/>) and
  /// WHIZ029 is reported at each ignored opt-in.
  /// </para>
  /// <para>
  /// Prefer <see cref="Versions"/> when the versions the id must hold are known, for example v7 for the ids the
  /// application mints and v4 for ids imported from another system: the id then still refuses anything else, such as
  /// a raw hash. Keep this option for ids issued outside the application, whose version it does not control. Writing
  /// both is an error (WHIZ039).
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

  /// <summary>
  /// Gets or sets the UUID versions the generated ID accepts through its checked entry points. Defaults to
  /// <see cref="UuidVersions.V7"/>.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/WhizbangIdAttributeTests.cs:WhizbangIdAttribute_VersionsProperty_CanBeSetAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdVersionAcceptanceTests.cs</tests>
  /// <tests>tests/Whizbang.Generators.Tests/WhizbangIdVersionsTests.cs</tests>
  /// <remarks>
  /// <para>
  /// An allow-list between the UUIDv7-only default and <see cref="AcceptAnyVersion"/>, for an id that must also hold
  /// values the application did not make: ids that already exist in stored data, or that another system issues.
  /// <c>From(Guid)</c>, <c>From(TrackedGuid)</c>, <c>Parse</c>, the explicit cast, the transport JSON reader and the
  /// GraphQL scalar accept a Guid whose version is in the list and whose variant is RFC 9562's, and throw
  /// <see cref="ArgumentException"/> naming the accepted versions otherwise. <see cref="Guid.Empty"/> has no version and
  /// is always refused. The public constructor and the <c>Value</c> initializer stay unchecked, as on every id.
  /// </para>
  /// <para>
  /// It is not needed for ids the application makes. Mint a new id with <c>New()</c>, and derive one through the
  /// framework's derivations (<c>GeneratedStreamIdentity.Derive</c>, <c>EmissionIdentity.Derive</c>,
  /// <c>CompositeChildIdentity.ForChild</c>): all of them produce UUIDv7, which the default accepts.
  /// </para>
  /// <para>
  /// A value of another version costs what time-ordering gave. <c>IsTimeOrdered</c> is <c>false</c>,
  /// <c>GetTimestamp()</c> returns no meaningful time, and <c>CompareTo</c> and the ordering operators no longer follow
  /// creation order. Used as a stream id, it also loses the index locality time-ordered values give a database: new
  /// rows land across the index rather than at its end. The framework's ordering guarantees are unaffected, since they
  /// rely on the message and event ids it mints, and a stream id is only hashed for partitioning and sorted for lock
  /// ordering.
  /// </para>
  /// <para>
  /// When the <c>[WhizbangId]</c> declarations for one id (the struct, a property, a parameter) list different
  /// versions, the id accepts the versions they have in common and WHIZ038 is reported at each list that was
  /// narrowed. An empty list, or declarations with no version in common, is an error (WHIZ043), as is a bit that
  /// names no version, such as <c>(UuidVersions)1</c> (WHIZ044). Writing this together with
  /// <c>AcceptAnyVersion = true</c> is an error (WHIZ039).
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // New customers are minted here; customers imported from the previous CRM keep their v4 ids.
  /// [WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V4)]
  /// public readonly partial struct CustomerId;
  ///
  /// var minted = CustomerId.New();                                                     // v7: accepted
  /// var imported = CustomerId.From(Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301")); // v4: accepted
  /// var hashed = CustomerId.From(NameBasedGuid.V5(ns, "a"));                           // v5: ArgumentException
  /// </code>
  /// </example>
  public UuidVersions Versions { get; set; } = UuidVersions.V7;
}
