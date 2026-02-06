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
/// <docs>perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnifiedQuerySyntaxTests.cs</tests>
public class PhysicalFieldQueryInterceptor : IQueryExpressionInterceptor {
  private readonly PhysicalFieldExpressionVisitor _visitor = new();

  /// <summary>
  /// Called by EF Core to allow transformation of the query expression tree
  /// before compilation.
  /// </summary>
  public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData) {
    // Apply our visitor to transform r.Data.PropertyName to EF.Property(r, "column")
    return _visitor.Visit(queryExpression);
  }
}
