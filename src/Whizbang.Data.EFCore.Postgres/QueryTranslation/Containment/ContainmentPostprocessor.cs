using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

/// <summary>
/// Runs the provider's own postprocessing and then reshapes equalities over JSON members into
/// containment tests.
/// </summary>
/// <remarks>
/// The ordering carries the argument. The base pass is where type mappings are inferred and assigned
/// and where a value converter has already been applied to both sides of a comparison, so a visitor
/// running afterwards sees a fully mapped tree in the stored form. Arranging that on the LINQ tree is
/// what the earlier attempt could not do.
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/ContainmentSqlRewriterTests.cs</tests>
public sealed class ContainmentPostprocessor(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies,
    RelationalQueryCompilationContext queryCompilationContext)
    : RelationalQueryTranslationPostprocessor(dependencies, relationalDependencies, queryCompilationContext) {
  /// <inheritdoc/>
  public override Expression Process(Expression query) {
    ArgumentNullException.ThrowIfNull(query);

    var translated = base.Process(query);

    // Asked per compilation rather than cached, so a deployment can change mechanism without a
    // release, and asked for this mechanism in particular so that only one of the two ever acts.
    if (!JsonbContainmentSwitch.ReshapesTranslatedTree || !ProviderCapabilities.ContainmentRewriteRequired) {
      return translated;
    }

    return new ContainmentSqlRewriter(JsonIndexRegistry.HasOrderedForTable).Visit(translated);
  }

}

/// <summary>
/// Supplies the containment postprocessor in place of the provider's own.
/// </summary>
/// <docs>contributors/perspective-query-pipeline</docs>
public sealed class ContainmentPostprocessorFactory(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies)
    : IQueryTranslationPostprocessorFactory {
  /// <inheritdoc/>
  public QueryTranslationPostprocessor Create(QueryCompilationContext queryCompilationContext) {
    ArgumentNullException.ThrowIfNull(queryCompilationContext);

    return new ContainmentPostprocessor(
      dependencies,
      relationalDependencies,
      (RelationalQueryCompilationContext)queryCompilationContext);
  }
}

/// <summary>
/// Registers the reshape on a context.
/// </summary>
/// <remarks>
/// Registering it does not put it in force: <see cref="JsonbContainmentSwitch"/> decides which
/// mechanism acts, and this one is not the default. Registering it unconditionally means a
/// deployment can switch mechanism through configuration rather than through a release.
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
public static class ContainmentPostprocessorExtensions {
  /// <summary>Registers the reshape, which acts only when the configured mode selects it.</summary>
  /// <param name="builder">The options being built.</param>
  /// <returns>The same builder.</returns>
  public static DbContextOptionsBuilder UseWhizbangContainmentReshape(
      this DbContextOptionsBuilder builder) {
    ArgumentNullException.ThrowIfNull(builder);

    return builder
      .ReplaceService<IQueryTranslationPostprocessorFactory, ContainmentPostprocessorFactory>();
  }

  /// <summary>Registers the reshape, which acts only when the configured mode selects it.</summary>
  /// <typeparam name="TContext">The context being configured.</typeparam>
  /// <param name="builder">The options being built.</param>
  /// <returns>The same builder, so its typed options remain reachable.</returns>
  public static DbContextOptionsBuilder<TContext> UseWhizbangContainmentReshape<TContext>(
      this DbContextOptionsBuilder<TContext> builder)
      where TContext : DbContext {
    ArgumentNullException.ThrowIfNull(builder);

    return builder
      .ReplaceService<IQueryTranslationPostprocessorFactory, ContainmentPostprocessorFactory>();
  }
}
