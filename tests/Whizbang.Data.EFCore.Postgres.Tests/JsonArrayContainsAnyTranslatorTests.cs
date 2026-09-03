using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.Functions;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The principal-filter translator, exercised directly rather than through a LINQ query.
/// <para>
/// It is called directly on purpose. EF never reaches this translator today — the plugin is
/// registered with a lifetime the provider does not consult — so a test written as a query would
/// assert nothing about this code, and would keep asserting nothing after the routing is fixed and
/// the SQL it emits starts mattering. Calling <c>Translate</c> is the only way to pin the emitted
/// expression while that defect is outstanding.
/// </para>
/// </summary>
/// <remarks>
/// The traversal key is the point. <see cref="PerspectiveScope.AllowedPrincipals"/> carries
/// <c>[JsonPropertyName("ap")]</c>, so a translator that traverses the CLR name reads a key that is
/// not in the document: the traversal yields SQL NULL, <c>?|</c> against NULL is NULL, and every row
/// is filtered out. That failure mode is silent — a security predicate that matches nothing looks
/// exactly like a predicate whose subject has no matching rows.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Functions/JsonArrayContainsAnyTranslator.cs</code-under-test>
[Category("Shard2")]
public class JsonArrayContainsAnyTranslatorTests : EFCoreTestBase {

  private static readonly MethodInfo _method =
    typeof(WhizbangJsonDbFunctions).GetMethod(
      nameof(WhizbangJsonDbFunctions.AllowedPrincipalsContainsAny),
      [typeof(DbFunctions), typeof(PerspectiveScope), typeof(string[])])!;

  private (JsonArrayContainsAnyTranslator Translator, NpgsqlSqlExpressionFactory Factory,
           IDiagnosticsLogger<DbLoggerCategory.Query> Logger) _build(WorkCoordinationDbContext ctx) {
    var services = ((IInfrastructure<IServiceProvider>)ctx).Instance;
    var factory = (NpgsqlSqlExpressionFactory)services.GetRequiredService<ISqlExpressionFactory>();
    var logger = services.GetRequiredService<IDiagnosticsLogger<DbLoggerCategory.Query>>();
    return (new JsonArrayContainsAnyTranslator(factory), factory, logger);
  }

  [Test]
  [Timeout(60000)]
  public async Task TheEmittedTraversal_UsesTheSerializedKeyNotTheClrNameAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var (translator, factory, logger) = _build(ctx);

    var translated = translator.Translate(
      instance: null,
      _method,
      [factory.Constant("dbfunctions"), factory.Constant("{}"), factory.Constant("user-a")],
      logger);

    await Assert.That(translated).IsNotNull()
      .Because("the method matches, so the translator owes the query pipeline an expression");
    var rendered = ExpressionPrinter.Print(translated!);
    await Assert.That(rendered).Contains("ap")
      .Because("the document has no AllowedPrincipals key — traversing it yields NULL, and `?|` "
             + "against NULL filters out every row instead of failing");
    await Assert.That(rendered).DoesNotContain("AllowedPrincipals")
      .Because("a security predicate that silently matches nothing is indistinguishable from one "
             + "whose subject genuinely has no rows");
  }

  [Test]
  [Timeout(60000)]
  public async Task AMethodItDoesNotOwn_IsDeclinedRatherThanMistranslatedAsync(
      CancellationToken cancellationToken) {
    // The guard exists because every registered translator is offered every method call in the
    // tree. Returning an expression for someone else's method would rewrite an unrelated query
    // into a principal filter.
    await using var ctx = CreateDbContext();
    var (translator, factory, logger) = _build(ctx);
    var someoneElsesMethod = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;

    var translated = translator.Translate(
      instance: factory.Constant("x"), someoneElsesMethod, [factory.Constant("y")], logger);

    await Assert.That(translated).IsNull()
      .Because("a translator that claims a method it does not own rewrites unrelated queries");
  }
}
