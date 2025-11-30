using System;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Marks a DbContext class for Whizbang source generator discovery.
/// Opt-in attribute that enables automatic generation of:
/// - Partial DbContext class with DbSet&lt;PerspectiveRow&lt;TModel&gt;&gt; properties
/// - EnsureWhizbangTablesCreatedAsync() extension method
/// - Service registration metadata
/// </summary>
/// <remarks>
/// <para>
/// <strong>Key-Based Grouping:</strong>
/// </para>
/// <para>
/// Keys enable flexible perspective-to-DbContext mappings:
/// <list type="bullet">
/// <item><strong>No keys (default):</strong> Uses unnamed key ("") - matches perspectives with no [WhizbangPerspective] attribute</item>
/// <item><strong>Single key:</strong> Matches perspectives with that specific key</item>
/// <item><strong>Multiple keys:</strong> Enables shared configuration - matches perspectives with any of the specified keys</item>
/// </list>
/// </para>
/// <para>
/// <strong>Multi-DbContext Scenarios:</strong>
/// </para>
/// <para>
/// Multiple DbContexts can share perspectives using common keys. For example:
/// </para>
/// <code>
/// [WhizbangDbContext("catalog", "products")]
/// public partial class CatalogDbContext : DbContext { }
///
/// [WhizbangDbContext("orders", "products")]
/// public partial class OrdersDbContext : DbContext { }
///
/// [WhizbangPerspective("products")]
/// public class ProductPerspective : IPerspectiveOf&lt;ProductEvent&gt; { }
/// // ↑ Included in BOTH CatalogDbContext AND OrdersDbContext
///
/// [WhizbangPerspective("catalog")]
/// public class CatalogOnlyPerspective : IPerspectiveOf&lt;CatalogEvent&gt; { }
/// // ↑ Only in CatalogDbContext
/// </code>
/// </remarks>
/// <example>
/// <para><strong>Single DbContext (default):</strong></para>
/// <code>
/// [WhizbangDbContext]
/// public partial class BffDbContext : DbContext {
///   public BffDbContext(DbContextOptions&lt;BffDbContext&gt; options) : base(options) { }
///
///   // DbSet properties auto-generated from discovered perspectives
///
///   protected override void OnModelCreating(ModelBuilder modelBuilder) {
///     modelBuilder.ConfigureWhizbang();
///   }
/// }
/// </code>
/// <para><strong>Multi-DbContext with distinct keys:</strong></para>
/// <code>
/// [WhizbangDbContext("catalog")]
/// public partial class CatalogDbContext : DbContext { }
///
/// [WhizbangDbContext("orders")]
/// public partial class OrdersDbContext : DbContext { }
/// </code>
/// <para><strong>Multi-DbContext with shared keys:</strong></para>
/// <code>
/// [WhizbangDbContext("catalog", "shared")]
/// public partial class CatalogDbContext : DbContext { }
///
/// [WhizbangDbContext("orders", "shared")]
/// public partial class OrdersDbContext : DbContext { }
///
/// // Perspectives with "shared" key will be in both contexts
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WhizbangDbContextAttribute : Attribute {
  /// <summary>
  /// Gets the keys that identify which perspectives should be included in this DbContext.
  /// Multiple keys enable shared configuration across contexts.
  /// </summary>
  /// <value>
  /// Array of key strings. Default: [""] (unnamed/default key) if no keys provided.
  /// </value>
  /// <remarks>
  /// <para>
  /// <strong>Matching Rules:</strong>
  /// </para>
  /// <list type="bullet">
  /// <item>A perspective matches this DbContext if any of the perspective's keys match any of this DbContext's keys</item>
  /// <item>Perspectives with no [WhizbangPerspective] attribute match the default key ("") only</item>
  /// <item>Empty or null keys parameter defaults to [""] (unnamed/default key)</item>
  /// </list>
  /// </remarks>
  public string[] Keys { get; }

  /// <summary>
  /// Initializes a new instance of the <see cref="WhizbangDbContextAttribute"/> class
  /// with the default unnamed key.
  /// </summary>
  /// <remarks>
  /// Equivalent to <c>[WhizbangDbContext("")]</c>. Matches perspectives with no [WhizbangPerspective] attribute.
  /// </remarks>
  /// <example>
  /// <code>
  /// [WhizbangDbContext]  // Keys = [""]
  /// public partial class BffDbContext : DbContext { }
  /// </code>
  /// </example>
  public WhizbangDbContextAttribute() {
    Keys = new[] { "" };
  }

  /// <summary>
  /// Initializes a new instance of the <see cref="WhizbangDbContextAttribute"/> class
  /// with the specified keys.
  /// </summary>
  /// <param name="keys">
  /// One or more keys that identify which perspectives should be included in this DbContext.
  /// Pass empty or null to use the default unnamed key ("").
  /// </param>
  /// <remarks>
  /// <para>
  /// <strong>Key Naming Conventions:</strong>
  /// </para>
  /// <list type="bullet">
  /// <item>Use lowercase for consistency (e.g., "catalog", "orders", "products")</item>
  /// <item>Use descriptive names that reflect domain boundaries or aggregates</item>
  /// <item>Reserved key: "" (empty string) is the default/unnamed key</item>
  /// </list>
  /// </remarks>
  /// <example>
  /// <para><strong>Single key:</strong></para>
  /// <code>
  /// [WhizbangDbContext("catalog")]
  /// public partial class CatalogDbContext : DbContext { }
  /// </code>
  /// <para><strong>Multiple keys (shared configuration):</strong></para>
  /// <code>
  /// [WhizbangDbContext("catalog", "products", "inventory")]
  /// public partial class CatalogDbContext : DbContext { }
  /// </code>
  /// </example>
  public WhizbangDbContextAttribute(params string[] keys) {
    Keys = keys?.Length > 0 ? keys : new[] { "" };
  }
}
