using Microsoft.EntityFrameworkCore;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Extension methods for configuring Whizbang physical field query translation on DbContext.
/// </summary>
public static class WhizbangDbContextOptionsBuilderExtensions {
  // Singleton interceptor instances — reuse across all DbContext resolutions
  // to avoid creating new EF Core internal service providers (ManyServiceProvidersCreatedWarning)
  private static readonly PhysicalFieldQueryInterceptor _queryInterceptor = new();
  private static readonly PhysicalFieldMaterializationInterceptor _materializationInterceptor = new();
  /// <summary>
  /// Enables Whizbang physical field query translation.
  /// When enabled, queries using r.Data.PropertyName will automatically use
  /// physical columns for properties registered in <see cref="PhysicalFieldRegistry"/>.
  /// </summary>
  /// <param name="optionsBuilder">The options builder</param>
  /// <returns>The options builder for chaining</returns>
  /// <remarks>
  /// <para>
  /// Usage:
  /// <code>
  /// var optionsBuilder = new DbContextOptionsBuilder&lt;MyDbContext&gt;();
  /// optionsBuilder
  ///     .UseNpgsql(connectionString)
  ///     .UseWhizbangPhysicalFields();
  /// </code>
  /// </para>
  /// <para>
  /// Before using this extension, ensure physical fields are registered:
  /// <code>
  /// PhysicalFieldRegistry.Register&lt;ProductModel&gt;("Price", "price");
  /// </code>
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/perspectives/physical-fields</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnifiedQuerySyntaxTests.cs</tests>
  public static DbContextOptionsBuilder UseWhizbangPhysicalFields(
      this DbContextOptionsBuilder optionsBuilder) {

    ArgumentNullException.ThrowIfNull(optionsBuilder);

    // Add the query interceptor that transforms r.Data.PropertyName to EF.Property()
    // and the materialization interceptor that hydrates physical field values into Data after query
    optionsBuilder.AddInterceptors(_queryInterceptor, _materializationInterceptor);

    return optionsBuilder;
  }

  /// <summary>
  /// Enables Whizbang physical field query translation for typed DbContextOptionsBuilder.
  /// </summary>
  /// <typeparam name="TContext">The DbContext type</typeparam>
  /// <param name="optionsBuilder">The options builder</param>
  /// <returns>The options builder for chaining</returns>
  public static DbContextOptionsBuilder<TContext> UseWhizbangPhysicalFields<TContext>(
      this DbContextOptionsBuilder<TContext> optionsBuilder)
      where TContext : DbContext {

    ArgumentNullException.ThrowIfNull(optionsBuilder);

    ((DbContextOptionsBuilder)optionsBuilder).UseWhizbangPhysicalFields();

    return optionsBuilder;
  }
}
