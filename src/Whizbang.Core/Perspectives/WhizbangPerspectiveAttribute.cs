#pragma warning disable S3604 // Primary constructor field/property initializers are intentional

using System;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Marks a perspective class for DbContext grouping in multi-context scenarios.
/// Optional attribute that controls which DbContexts include this perspective's model.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Key-Based Matching:</strong>
/// </para>
/// <para>
/// This attribute enables flexible perspective-to-DbContext mappings:
/// <list type="bullet">
/// <item><strong>No attribute:</strong> Perspective matches default DbContext only (Key = "")</item>
/// <item><strong>[WhizbangPerspective()]:</strong> Matches default DbContext only (Key = "")</item>
/// <item><strong>[WhizbangPerspective("catalog")]:</strong> Matches DbContexts with "catalog" key</item>
/// <item><strong>[WhizbangPerspective("catalog", "orders")]:</strong> Matches DbContexts with either key</item>
/// </list>
/// </para>
/// <para>
/// <strong>Matching Rules:</strong>
/// </para>
/// <para>
/// A perspective matches a DbContext if ANY of the perspective's keys match ANY of the DbContext's keys.
/// This enables both isolated and shared configuration patterns.
/// </para>
/// <para>
/// <strong>When to Use:</strong>
/// </para>
/// <list type="bullet">
/// <item>Single DbContext project: Not needed (perspectives auto-match default DbContext)</item>
/// <item>Multiple DbContexts with domain separation: Use to route perspectives to specific contexts</item>
/// <item>Shared models across contexts: Use same key on multiple DbContexts</item>
/// </list>
/// </remarks>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/Discovery/PerspectiveDiscoveryTests.cs</tests>
/// <example>
/// <para><strong>Single DbContext (no attribute needed):</strong></para>
/// <code>
/// // No attribute = matches default DbContext
/// public class ProductCatalogPerspective : IPerspectiveFor&lt;ProductCatalogModel, ProductCreatedEvent&gt; {
///   // Will be included in the default DbContext
/// }
/// </code>
/// <para><strong>Multi-DbContext with distinct perspectives:</strong></para>
/// <code>
/// [WhizbangPerspective("catalog")]
/// public class ProductPerspective : IPerspectiveFor&lt;ProductModel, ProductEvent&gt; {
///   // Only included in DbContexts with "catalog" key
/// }
///
/// [WhizbangPerspective("orders")]
/// public class OrderPerspective : IPerspectiveFor&lt;OrderModel, OrderEvent&gt; {
///   // Only included in DbContexts with "orders" key
/// }
/// </code>
/// <para><strong>Shared perspective across multiple contexts:</strong></para>
/// <code>
/// [WhizbangPerspective("catalog", "orders")]
/// public class CustomerPerspective : IPerspectiveFor&lt;CustomerModel, CustomerEvent&gt; {
///   // Included in both "catalog" AND "orders" DbContexts
/// }
/// </code>
/// <para><strong>Perspective in specific + shared contexts:</strong></para>
/// <code>
/// // DbContexts
/// [WhizbangDbContext("catalog", "products")]
/// public partial class CatalogDbContext : DbContext { }
///
/// [WhizbangDbContext("warehouse", "products")]
/// public partial class WarehouseDbContext : DbContext { }
///
/// // Perspective
/// [WhizbangPerspective("products")]
/// public class ProductInventoryPerspective : IPerspectiveFor&lt;InventoryModel, InventoryEvent&gt; {
///   // Included in BOTH CatalogDbContext AND WarehouseDbContext
///   // (both have "products" in their keys)
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WhizbangPerspectiveAttribute(params string[]? keys) : Attribute {
  /// <summary>
  /// Gets the keys that identify which DbContexts should include this perspective.
  /// Multiple keys enable including the perspective in multiple contexts.
  /// </summary>
  /// <value>
  /// Array of key strings. Empty array matches default DbContext only (Key = "").
  /// </value>
  /// <remarks>
  /// <para>
  /// <strong>Matching Behavior:</strong>
  /// </para>
  /// <list type="bullet">
  /// <item>Empty array (no keys): Matches default DbContext only (Key = "")</item>
  /// <item>One or more keys: Matches any DbContext with ANY matching key</item>
  /// <item>Keys are case-sensitive - use consistent casing</item>
  /// </list>
  /// </remarks>
  /// <tests>tests/Whizbang.Generators.Tests/Models/PerspectiveInfoTests.cs</tests>
  public string[] Keys { get; } = keys ?? [];
}
