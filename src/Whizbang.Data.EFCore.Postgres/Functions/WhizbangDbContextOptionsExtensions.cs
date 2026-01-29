using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Whizbang.Data.EFCore.Postgres.Functions;

/// <summary>
/// Extension methods for registering Whizbang's custom PostgreSQL functions with EF Core.
/// </summary>
/// <docs>core-concepts/security#principal-filtering</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/Functions/WhizbangDbContextOptionsExtensionsTests.cs</tests>
public static class WhizbangDbContextOptionsExtensions {
  /// <summary>
  /// Adds Whizbang's custom PostgreSQL function translators to the Npgsql provider.
  /// Call this method when configuring Npgsql options to enable optimized principal filtering.
  /// </summary>
  /// <param name="optionsBuilder">The Npgsql DbContext options builder.</param>
  /// <returns>The same options builder for fluent chaining.</returns>
  /// <example>
  /// <code>
  /// protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
  /// {
  ///   optionsBuilder.UseNpgsql(connectionString, npgsqlOptions =>
  ///   {
  ///     npgsqlOptions.UseWhizbangFunctions();
  ///   });
  /// }
  /// </code>
  /// </example>
  /// <remarks>
  /// This registers the following custom function translators:
  /// <list type="bullet">
  ///   <item><see cref="JsonArrayContainsAnyTranslator"/> - Translates to PostgreSQL's ?| operator</item>
  /// </list>
  /// </remarks>
  public static NpgsqlDbContextOptionsBuilder UseWhizbangFunctions(
      this NpgsqlDbContextOptionsBuilder optionsBuilder) {
    // Add our custom method call translator plugin to the existing collection
    // This preserves Npgsql's built-in translators while adding our own
    var coreOptionsBuilder = ((IRelationalDbContextOptionsBuilderInfrastructure)optionsBuilder).OptionsBuilder;

    ((IDbContextOptionsBuilderInfrastructure)coreOptionsBuilder).AddOrUpdateExtension(
      new WhizbangOptionsExtension());

    return optionsBuilder;
  }
}

/// <summary>
/// EF Core options extension that registers Whizbang's custom translators.
/// </summary>
internal sealed class WhizbangOptionsExtension : IDbContextOptionsExtension {
  public DbContextOptionsExtensionInfo Info => new WhizbangOptionsExtensionInfo(this);

  public void ApplyServices(IServiceCollection services) {
    // Add our translator plugin to the collection (alongside Npgsql's built-in plugins)
    // Must be scoped because it depends on NpgsqlSqlExpressionFactory which is scoped
    services.AddScoped<IMethodCallTranslatorPlugin, WhizbangMethodCallTranslatorPlugin>();
  }

  public void Validate(IDbContextOptions options) {
    // No validation needed
  }
}

internal sealed class WhizbangOptionsExtensionInfo : DbContextOptionsExtensionInfo {
  public WhizbangOptionsExtensionInfo(IDbContextOptionsExtension extension) : base(extension) { }

  public override bool IsDatabaseProvider => false;

  public override string LogFragment => "WhizbangFunctions ";

  public override int GetServiceProviderHashCode() => 0;

  public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
    other is WhizbangOptionsExtensionInfo;

  public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) {
    debugInfo["Whizbang:Functions"] = "enabled";
  }
}
