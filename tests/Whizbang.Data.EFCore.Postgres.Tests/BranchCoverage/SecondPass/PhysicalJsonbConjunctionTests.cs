// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.SecondPass;

/// <summary>
/// An element predicate over a jsonb list column is compiled to one containment test only when it is a
/// conjunction of equalities. Both outcomes in one class: a conjunction of equalities is claimed, and a
/// conjunction whose first term is anything else is left exactly as written. No database.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/QueryTranslation/PhysicalJsonbContainmentRewriter.cs</code-under-test>
[NotInParallel("EFCorePostgresTests")]
[Category("Unit")]
[Category("Shard2")]
[SuppressMessage("Readability", "RCS1118:Mark local variable as const",
  Justification = "Captured on purpose: a const local is inlined as a literal and the parameterized path would go untested.")]
public class PhysicalJsonbConjunctionTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=jsonbconjunction;Username=u;Password=p";

  [Test]
  public async Task ElementPredicate_ConjunctionOfEqualities_IsOneContainmentTestAsync() {
    var key = "team";
    var label = "red";

    var count = _containments(r => r.Data.Labels.Any(l => l.Key == key && l.Label == label));

    await Assert.That(count).IsEqualTo(1);
  }

  // A first term that is not an equality cannot be expressed as containment, so the whole predicate
  // stands down; claiming only the second term would match rows the filter excludes.
  [Test]
  public async Task ElementPredicate_ConjunctionStartingWithANonEquality_IsLeftAloneAsync() {
    var prefix = "te";
    var label = "red";

    var count = _containments(r => r.Data.Labels.Any(l => l.Key.StartsWith(prefix, StringComparison.Ordinal) && l.Label == label));

    await Assert.That(count).IsEqualTo(0);
  }

  private static int _containments(
      Expression<Func<PerspectiveRow<PhysicalJsonbContainmentSqlTests.JsonbColumnsModel>, bool>> filter) {
    PhysicalJsonbContainmentSqlTests.JsonbColumnsModel.Register();
    var options = new DbContextOptionsBuilder<PhysicalJsonbContainmentSqlTests.JsonbColumnsDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;
    using var context = new PhysicalJsonbContainmentSqlTests.JsonbColumnsDbContext(options);
    var query = context.Set<PerspectiveRow<PhysicalJsonbContainmentSqlTests.JsonbColumnsModel>>().Where(filter).Expression;
    var redirected = new PhysicalFieldExpressionVisitor().Visit(query);
    var rewritten = new PhysicalJsonbContainmentRewriter(context.Model).Visit(redirected);
    var counter = new ContainmentCounter();
    counter.Visit(rewritten);
    return counter.Count;
  }

  private sealed class ContainmentCounter : ExpressionVisitor {
    public int Count { get; private set; }

    protected override Expression VisitMethodCall(MethodCallExpression node) {
      if (node.Method.Name == nameof(NpgsqlJsonDbFunctionsExtensions.JsonContains)) {
        Count++;
      }

      return base.VisitMethodCall(node);
    }
  }
}
