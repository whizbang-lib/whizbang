#pragma warning disable S3604 // Primary constructor field/property initializers are intentional

using System;

namespace Whizbang.Data.EFCore.Custom;

/// <summary>
/// Marks a DbContext class for Whizbang source generator discovery.
/// Opt-in attribute that enables automatic generation of:
/// - Partial DbContext class with DbSet&lt;PerspectiveRow&lt;TModel&gt;&gt; properties
/// - EnsureWhizbangTablesCreatedAsync() extension method
/// - Service registration metadata
/// </summary>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs</tests>
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
/// public class ProductPerspective : IPerspectiveFor&lt;ProductModel, ProductEvent&gt; { }
/// // ↑ Included in BOTH CatalogDbContext AND OrdersDbContext
///
/// [WhizbangPerspective("catalog")]
/// public class CatalogOnlyPerspective : IPerspectiveFor&lt;CatalogModel, CatalogEvent&gt; { }
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
public sealed class WhizbangDbContextAttribute(params string[]? keys) : Attribute {
  /// <summary>
  /// Gets the keys that identify which perspectives should be included in this DbContext.
  /// Multiple keys enable shared configuration across contexts.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDefaultKey_UsesEmptyStringKeyAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithSingleKey_DiscoversDbContextWithKeyAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithMultipleKeys_DiscoversDbContextWithAllKeysAsync</tests>
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
  public string[] Keys { get; } = keys?.Length > 0 ? keys : [""];

  /// <summary>
  /// Gets or sets the PostgreSQL schema name for this DbContext's tables.
  /// If not specified, schema is derived from the DbContext's namespace.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <strong>Schema Naming Convention:</strong>
  /// </para>
  /// <list type="bullet">
  /// <item>Should match the service name (e.g., "inventory" for InventoryWorker, "bff" for BFF)</item>
  /// <item>Use lowercase for consistency</item>
  /// <item>If not specified, generator derives schema from namespace (e.g., "ECommerce.InventoryWorker" → "inventory")</item>
  /// </list>
  /// </remarks>
  /// <example>
  /// <code>
  /// [WhizbangDbContext(Schema = "inventory")]
  /// public partial class InventoryDbContext : DbContext { }
  ///
  /// [WhizbangDbContext(Schema = "bff")]
  /// public partial class BffDbContext : DbContext { }
  /// </code>
  /// </example>
  public string? Schema { get; set; }

  /// <summary>
  /// Gets or sets the connection string name to use for this DbContext.
  /// If not specified, derived from the DbContext class name.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <strong>Connection String Naming Convention:</strong>
  /// </para>
  /// <list type="bullet">
  /// <item>Default: "{ContextName}-db" where ContextName is the class name minus "DbContext" suffix</item>
  /// <item>Example: ChatDbContext → "chat-db"</item>
  /// <item>Set this property to override the default (e.g., "chat-service-db")</item>
  /// </list>
  /// </remarks>
  /// <example>
  /// <code>
  /// [WhizbangDbContext(ConnectionStringName = "chat-service-db")]
  /// public partial class ChatDbContext : DbContext { }
  /// </code>
  /// </example>
  /// <docs>extending/features/vector-search#turnkey-setup</docs>
  public string? ConnectionStringName { get; set; }
}
