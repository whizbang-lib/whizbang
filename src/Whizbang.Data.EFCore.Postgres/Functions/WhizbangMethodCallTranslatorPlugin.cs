// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore.Query;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query;
using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres.Functions;

/// <summary>
/// Plugin that registers Whizbang's custom method call translators with Npgsql.
/// </summary>
/// <docs>fundamentals/security/security#principal-filtering</docs>
public class WhizbangMethodCallTranslatorPlugin : IMethodCallTranslatorPlugin {
  public WhizbangMethodCallTranslatorPlugin(ISqlExpressionFactory sqlExpressionFactory) {
    // At runtime when using Npgsql, the factory will be NpgsqlSqlExpressionFactory
    var npgsqlFactory = sqlExpressionFactory as NpgsqlSqlExpressionFactory
      ?? throw new InvalidOperationException(
          "WhizbangMethodCallTranslatorPlugin requires Npgsql provider. " +
          $"Expected NpgsqlSqlExpressionFactory but got {TypeNameFormatter.ForLog(sqlExpressionFactory.GetType())}");
    Translators = [
      new JsonArrayContainsAnyTranslator(npgsqlFactory),
      new FoldedContainsTranslator(npgsqlFactory),
    ];
  }

  public IEnumerable<IMethodCallTranslator> Translators { get; }
}
