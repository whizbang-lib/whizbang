using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Functions;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// That <c>EF.Functions.AllowedPrincipalsContainsAny</c> reaches its translator at all.
/// <para>
/// The translator has unit tests, but a translator EF never calls is a translator that does not
/// exist as far as a query is concerned. This is the test that would have caught the registration
/// defect: the plugin was registered at a lifetime the provider does not resolve, so the function
/// was offered to nothing and every query using documented public API failed to translate.
/// </para>
/// </summary>
/// <remarks>
/// Asserts on generated SQL rather than on rows. The point is that translation happens and emits
/// the indexable <c>?|</c> against the serialized key — a row-count assertion would pass just as
/// well against the per-row unnest fallback this function exists to avoid.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Functions/WhizbangDbContextOptionsExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Functions/JsonArrayContainsAnyTranslator.cs</code-under-test>
[Category("Shard4")]
public class PrincipalFilterQueryTranslationTests : EFCoreTestBase {

  private static readonly string[] _twoPrincipals = ["user-a", "group-b"];
  private static readonly string[] _onePrincipal = ["user-a"];

  [Test]
  [Timeout(60000)]
  public async Task APrincipalFilterQuery_TranslatesToTheIndexableOverlapOperatorAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();

    var sql = ctx.Set<PerspectiveRow<Order>>()
      .Where(r => EF.Functions.AllowedPrincipalsContainsAny(r.Scope.AllowedPrincipals, _twoPrincipals))
      .ToQueryString();

    await Assert.That(sql).Contains("?|")
      .Because("the whole point of the function is the GIN-indexable overlap operator; without it "
             + "the caller silently gets a per-row unnest no index can serve");
    await Assert.That(sql).Contains("'ap'")
      .Because("the traversal must use the serialized key, or the predicate matches no rows");
  }

  [Test]
  public async Task APrincipalFilterQuery_ActuallyRunsAgainstPostgresAsync(
      CancellationToken cancellationToken) {
    // Translating is necessary but not sufficient — the emitted SQL has to be valid PostgreSQL.
    await using var ctx = CreateDbContext();

    var count = await ctx.Set<PerspectiveRow<Order>>()
      .Where(r => EF.Functions.AllowedPrincipalsContainsAny(r.Scope.AllowedPrincipals, _onePrincipal))
      .CountAsync(cancellationToken);

    await Assert.That(count).IsGreaterThanOrEqualTo(0)
      .Because("the query must execute; the row count is beside the point");
  }
}
