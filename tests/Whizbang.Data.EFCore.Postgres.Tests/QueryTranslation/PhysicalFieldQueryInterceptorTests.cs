// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// The interceptor's own seam: the event data Entity Framework hands it may carry no context, and then
/// the containment passes run without a model rather than failing the query's compilation.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/QueryTranslation/PhysicalFieldQueryInterceptor.cs</code-under-test>
[Category("Shard1")]
public class PhysicalFieldQueryInterceptorTests {
  [Test]
  public async Task QueryCompilationStarting_WithoutAContext_LeavesAPlainQueryUnchangedAsync() {
    var x = Expression.Parameter(typeof(int), "x");
    var query = Expression.Lambda<Func<int, bool>>(Expression.Equal(x, Expression.Constant(1)), x);
    var eventData = new QueryExpressionEventData(
      eventDefinition: null!,
      messageGenerator: static (_, _) => string.Empty,
      context: null,
      queryExpression: query,
      expressionPrinter: new ExpressionPrinter());

    var rewritten = new PhysicalFieldQueryInterceptor().QueryCompilationStarting(query, eventData);

    await Assert.That(rewritten).IsSameReferenceAs(query)
      .Because("with no context there is no model to find promoted fields or documents in, so nothing is rewritten");
  }
}
