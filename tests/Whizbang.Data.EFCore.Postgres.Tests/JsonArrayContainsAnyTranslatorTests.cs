using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.Functions;

#pragma warning disable EF1001 // Internal EF Core API usage — asserting on the emitted expression requires naming its type.

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
/// What the translator is responsible for is now only the operator. The function takes the
/// allowed-principals MEMBER, so EF renders the traversal into the scope document itself — keyed
/// from <c>[JsonPropertyName("ap")]</c> — and hands it in already built. The translator applying
/// <c>?|</c> to exactly the expression it was given, rather than re-deriving a path, is what keeps
/// the emitted key correct by construction instead of by a literal that can drift from the model.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Functions/JsonArrayContainsAnyTranslator.cs</code-under-test>
[Category("Shard2")]
public class JsonArrayContainsAnyTranslatorTests : EFCoreTestBase {

  private static readonly MethodInfo _method =
    typeof(WhizbangJsonDbFunctions).GetMethod(
      nameof(WhizbangJsonDbFunctions.AllowedPrincipalsContainsAny),
      [typeof(DbFunctions), typeof(List<string>), typeof(string[])])!;

  private (JsonArrayContainsAnyTranslator Translator, NpgsqlSqlExpressionFactory Factory,
           IDiagnosticsLogger<DbLoggerCategory.Query> Logger) _build(WorkCoordinationDbContext ctx) {
    var services = ((IInfrastructure<IServiceProvider>)ctx).Instance;
    var factory = (NpgsqlSqlExpressionFactory)services.GetRequiredService<ISqlExpressionFactory>();
    var logger = services.GetRequiredService<IDiagnosticsLogger<DbLoggerCategory.Query>>();
    return (new JsonArrayContainsAnyTranslator(factory), factory, logger);
  }

  [Test]
  [Timeout(60000)]
  public async Task TheOperatorIsAppliedToTheExpressionItWasGivenAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var (translator, factory, logger) = _build(ctx);
    var principalsPath = factory.Constant("principals-path");

    var translated = translator.Translate(
      instance: null, _method, [factory.Constant("dbfunctions"), principalsPath, factory.Constant("user-a")], logger);

    await Assert.That(translated).IsNotNull()
      .Because("the method matches, so the translator owes the query pipeline an expression");
    var binary = translated as PgBinaryExpression;
    await Assert.That(binary).IsNotNull()
      .Because("the ?| overlap is a binary operator expression, and anything else means a different "
             + "predicate reached the database than the one this function promises");
    await Assert.That(binary!.OperatorType).IsEqualTo(PgExpressionType.JsonExistsAny)
      .Because("JsonExistsAny is the GIN-indexable operator; another one silently costs the index");
    // Not reference equality: MakePostgresBinary re-wraps its operands to apply type mappings. What
    // matters is that the left operand is whatever EF handed in, NOT a traversal this translator
    // built — one that rebuilt the path could key it differently from the model and match nothing.
    await Assert.That(binary.Left.GetType().Name).DoesNotContain("JsonTraversal")
      .Because("the traversal must come from EF, keyed off [JsonPropertyName] in the model, rather "
             + "than from a literal in this translator that can drift from it");
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
