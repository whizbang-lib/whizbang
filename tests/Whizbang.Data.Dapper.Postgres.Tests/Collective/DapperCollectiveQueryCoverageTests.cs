using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Coverage for <see cref="DapperCollectiveQuery"/>'s <c>Of&lt;TOther&gt;()</c> — the inert marker the Dapper
/// filter compiler reads as an expression-tree node, never enumerated in production. Every
/// existing usage of <c>q.Of&lt;TOther&gt;()</c> in this suite lives inside an
/// <c>Expression&lt;Func&lt;...&gt;&gt;</c> that is only walked, never compiled and invoked, so the
/// method body itself has never actually run. A directly-invoked call is what a caller that
/// mistakenly enumerates the marker (rather than composing it into a Where expression) would do.
/// </summary>
[Category("Unit")]
[Category("CollectiveEvents")]
public class DapperCollectiveQueryCoverageTests {

  // Dapper has no LINQ provider for this type, so if Of<TOther>() were ever actually enumerated
  // (instead of only having its call node read), it must come back as a harmless empty sequence --
  // never null, never a queryable that reaches for a real connection. Returning null would NRE any
  // caller that (mistakenly) enumerates it instead of composing it into a Where expression; reaching
  // for a data source would attempt a query this class has no way to satisfy.
  [Test]
  public async Task Of_DirectlyEnumerated_ReturnsEmptySequenceAsync() {
    var query = new DapperCollectiveQuery(new Dictionary<Type, string>());

    var result = query.Of<_siblingModel>();

    await Assert.That(result).IsNotNull();
    await Assert.That(result.Count()).IsEqualTo(0)
      .Because("Of<TOther>() is an inert marker for expression-tree construction, not a real data source -- enumerating it directly must be harmless, not a crash or a live query");
  }

  private sealed class _siblingModel {
    public string Name { get; set; } = "";
  }
}
