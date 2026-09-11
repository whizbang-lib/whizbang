using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitModeProductionTests.cs:Where_PhysicalField_SqlUsesColumn_NotJsonbAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitModeProductionTests.cs:Sql_Where_GuidField_UsesPhysicalColumnAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitModeProductionTests.cs:GroupBy_PhysicalField_SqlNotClientEvalAsync</tests>
public class PhysicalFieldQueryInterceptor : IQueryExpressionInterceptor {
  private readonly PhysicalFieldExpressionVisitor _visitor = new();

  /// <summary>
  /// Called by EF Core to allow transformation of the query expression tree
  /// before compilation.
  /// </summary>
  public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData) {
    ArgumentNullException.ThrowIfNull(eventData);

    // First, redirect promoted properties to their real columns.
    var rewritten = _visitor.Visit(queryExpression);

    // Then compile what is left, which is genuinely JSON, into a containment test where that is
    // equivalent. Order matters: a promoted property is already an EF.Property call by now, so the
    // containment pass cannot see it and cannot claim it.
    return new JsonbContainmentRewriter(eventData.Context?.Model).Visit(rewritten);
  }
}
