using Microsoft.EntityFrameworkCore.Query;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query;

namespace Whizbang.Data.EFCore.Postgres.Functions;

/// <summary>
/// Plugin that registers Whizbang's custom method call translators with Npgsql.
/// </summary>
/// <docs>core-concepts/security#principal-filtering</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/Functions/WhizbangMethodCallTranslatorPluginTests.cs</tests>
public class WhizbangMethodCallTranslatorPlugin : IMethodCallTranslatorPlugin {
  public WhizbangMethodCallTranslatorPlugin(ISqlExpressionFactory sqlExpressionFactory) {
    // At runtime when using Npgsql, the factory will be NpgsqlSqlExpressionFactory
    var npgsqlFactory = sqlExpressionFactory as NpgsqlSqlExpressionFactory
      ?? throw new InvalidOperationException(
          "WhizbangMethodCallTranslatorPlugin requires Npgsql provider. " +
          $"Expected NpgsqlSqlExpressionFactory but got {sqlExpressionFactory.GetType().Name}");
    Translators = [new JsonArrayContainsAnyTranslator(npgsqlFactory)];
  }

  public IEnumerable<IMethodCallTranslator> Translators { get; }
}
