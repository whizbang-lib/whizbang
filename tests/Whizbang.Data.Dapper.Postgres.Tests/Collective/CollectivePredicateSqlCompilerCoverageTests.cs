#pragma warning disable CA1707

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Unit tests (no database) for the one <see cref="CollectivePredicateSqlCompiler{TModel}"/> arm
/// not already locked by <see cref="CollectivePredicateSqlCompilerTests"/>: a boolean-valued
/// method call that is neither <c>Any</c> nor <c>Contains</c>. This compiler turns a collective
/// apply's predicate into SQL with no further validation downstream — a shape it does not
/// recognize must throw rather than fall through and emit no WHERE clause fragment at all
/// (which would UPDATE every row the caller never asked for).
/// </summary>
public class CollectivePredicateSqlCompilerCoverageTests {

  private sealed class _jobModel {
    public string Status { get; set; } = "";
  }

  [Test]
  public async Task Compile_UnsupportedBooleanMethodCall_ThrowsNotSupportedAsync() {
    // StartsWith is a method call returning bool, but it is neither the `Any` cross-perspective
    // shape nor the `Contains` IN-clause shape — it must fall through both recognized arms and
    // hit the compiler's catch-all throw, not silently compile to nothing.
    // Two characters deliberately: StartsWith("A") trips CA1866 (prefer the char overload), and
    // switching to the char overload would change which MethodInfo the compiler sees. A
    // multi-character prefix keeps the unsupported-string-method shape this test is about.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Data.Status.StartsWith("Ar", StringComparison.Ordinal);

    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }
}
