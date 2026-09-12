using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Whizbang.Data.EFCore.Postgres.QueryTranslation.Compatibility;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Query expression interceptor that transforms r.Data.PropertyName access
/// to shadow property access for registered physical fields.
/// </summary>
/// <remarks>
/// <para>
/// This interceptor integrates <see cref="PhysicalFieldExpressionVisitor"/> into
/// EF Core's query pipeline using the IQueryExpressionInterceptor interface
/// (available in EF Core 7.0+).
/// </para>
/// <para>
/// Register this interceptor when configuring DbContext:
/// <code>
/// optionsBuilder.AddInterceptors(new PhysicalFieldQueryInterceptor());
/// </code>
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/PerspectiveSqlShapeTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/JsonbContainmentSqlMatrixTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitModeProductionTests.cs:Where_PhysicalField_SqlUsesColumn_NotJsonbAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitModeProductionTests.cs:Sql_Where_GuidField_UsesPhysicalColumnAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitModeProductionTests.cs:GroupBy_PhysicalField_SqlNotClientEvalAsync</tests>
public class PhysicalFieldQueryInterceptor : IQueryExpressionInterceptor {
  private readonly PhysicalFieldExpressionVisitor _visitor = new();
  private readonly OrdinalEqualsRewriter _ordinalEquals = new();

  /// <summary>
  /// Called by EF Core to allow transformation of the query expression tree
  /// before compilation.
  /// </summary>
  public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData) {
    ArgumentNullException.ThrowIfNull(eventData);

    // First, redirect promoted properties to their real columns.
    var rewritten = _visitor.Visit(queryExpression);

    // Then normalize the one shape Entity Framework refuses to translate at all. Unconditional,
    // because it is a compatibility fix rather than an optimization: turning the containment rewrite
    // off must not take a working translation with it, and the pass that reshapes translated SQL
    // cannot do this itself, since by then the refusal has already been raised.
    rewritten = _ordinalEquals.Visit(rewritten);

    // Then compile what is left, which is genuinely JSON, into a containment test where that is
    // equivalent. Order matters twice over: a promoted property is already an EF.Property call by
    // now, so the containment pass cannot claim it, and an ordinal Equals is already an equality, so
    // the containment pass needs no knowledge of StringComparison.
    return new JsonbContainmentRewriter(eventData.Context?.Model).Visit(rewritten);
  }
}
