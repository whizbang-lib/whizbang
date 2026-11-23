// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Reads a strongly-typed id out of a stored document in either of its forms: the UUID string a release since #2518
/// writes, or the object <c>{"Value": "&lt;uuid&gt;"}</c> an earlier one wrote.
/// </summary>
/// <remarks>
/// <para>
/// The stored-form rewrite converts the object form at startup, but a table it has not finished (its budget ran out),
/// or a row an instance of an earlier release writes during a rolling deploy after the rewrite settled, still holds it.
/// Entity Framework reads an id in SQL as <c>CAST(data -&gt;&gt; 'OrderId' AS uuid)</c>, which fails on such a row
/// with 22P02, and tests a collection of ids by containment, which skips it. So every place a query reads an id is
/// made to take either form, whatever state the rewrite is in:
/// </para>
/// <list type="bullet">
/// <item>An id read out of a document, in a predicate, an ordering or a projection: the text it holds, and for an
/// object (text starting with <c>{</c>) its <c>Value</c>, cast to uuid.</item>
/// <item>An id read out of a collection's elements (<c>jsonb_to_recordset</c>, for <c>Lines.Any(l =&gt; l.LineId == x)</c>):
/// the column is read as text and then the same way.</item>
/// <item>A collection of ids tested for an id: also tested for the object holding it.</item>
/// </list>
/// <para>
/// Containment over a whole document (<c>MatchOnAnyField</c>) takes the object form in
/// <see cref="QueryTranslation.JsonbContainment"/>. An id predicate was not answered by an index before this either
/// (the cast expression has none), so taking either form costs nothing an index gave.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdDocumentStorageTests.cs:Whizbang_EveryIdQueryFindsARowStillInTheObjectFormAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdDocumentStorageTests.cs:Whizbang_ContainsOverAListOfIdsFindsElementsStillInTheObjectFormAsync</tests>
[SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
  Justification = "Reading a stored id in either form means rewriting the expressions Entity Framework and the provider " +
    "produced: the record set function and the containment operator have no public expression types.")]
internal sealed class StrongIdToleranceRewriter(
    RelationalTypeMapping text, RelationalTypeMapping jsonb, RelationalTypeMapping boolean) : ExpressionVisitor {
  private const string VALUE = "Value";

  /// <summary>The record set columns, by table alias and name, whose ids this rewriter reads as text.</summary>
  private readonly Dictionary<(string Alias, string Name), WhizbangIdTypeMapping> _recordSetIds = [];

  /// <summary>Rewrites a translated query so every id it reads in SQL takes either stored form.</summary>
  /// <param name="query">The translated query.</param>
  /// <returns>The rewritten query.</returns>
  public Expression Rewrite(Expression query) {
    // The record set columns first: a projection that reads one can come before the table that declares it.
    new RecordSetIds(_recordSetIds).Visit(query);
    return Visit(query);
  }

  /// <inheritdoc/>
  protected override Expression VisitExtension(Expression node) {
    ArgumentNullException.ThrowIfNull(node);

    switch (node) {
      case ShapedQueryExpression shaped:
        // The shaper is client code, not SQL: only the query half is ours.
        return shaped.Update(Visit(shaped.QueryExpression), shaped.ShaperExpression);

      case JsonScalarExpression { TypeMapping: WhizbangIdTypeMapping mapping } json:
        return _eitherForm(
          new JsonScalarExpression(Visit(json.Json) as SqlExpression ?? json.Json, json.Path, typeof(string), text, json.IsNullable),
          json.Type,
          mapping);

      case PgTableValuedFunctionExpression recordSet when recordSet.ColumnInfos is { } columns
          && columns.Any(c => c.TypeMapping is WhizbangIdTypeMapping):
        return _readIdsAsText(recordSet, columns);

      case ColumnExpression column when _recordSetIds.TryGetValue((column.TableAlias, column.Name), out var mapping):
        return _eitherForm(
          new ColumnExpression(column.Name, column.TableAlias, typeof(string), text, column.IsNullable),
          column.Type,
          mapping);

      case PgBinaryExpression { OperatorType: PgExpressionType.Contains } contains
          when contains.Left.TypeMapping?.ElementTypeMapping is WhizbangIdTypeMapping
            && contains.Right is SqlFunctionExpression { Name: "to_jsonb", Arguments: [var id] }:
        return _eitherElementForm(contains, id);

      default:
        return base.VisitExtension(node);
    }
  }

  /// <summary>
  /// The id the text holds: the text itself, or for an object the text of its <c>Value</c>, cast to uuid.
  /// <c>CAST(CASE WHEN t LIKE '{%' THEN jsonb_extract_path_text(CAST(t AS jsonb), 'Value') ELSE t END AS uuid)</c>.
  /// </summary>
  private SqlUnaryExpression _eitherForm(SqlExpression stored, Type idType, WhizbangIdTypeMapping mapping) {
    var isObject = new LikeExpression(
      stored, new SqlConstantExpression("{%", typeof(string), text), escapeChar: null, boolean);
    var inner = new SqlFunctionExpression(
      "jsonb_extract_path_text",
      [new SqlUnaryExpression(ExpressionType.Convert, stored, typeof(string), jsonb),
        new SqlConstantExpression(VALUE, typeof(string), text)],
      nullable: true,
      argumentsPropagateNullability: [true, false],
      typeof(string),
      text);
    var guidText = new CaseExpression([new CaseWhenClause(isObject, inner)], stored);
    return new SqlUnaryExpression(ExpressionType.Convert, guidText, idType, mapping);
  }

  /// <summary>The record set with each id column read as text, remembered so the columns that read it follow.</summary>
  private PgTableValuedFunctionExpression _readIdsAsText(
      PgTableValuedFunctionExpression recordSet, IReadOnlyList<PgTableValuedFunctionExpression.ColumnInfo> columns) {
    var asText = new List<PgTableValuedFunctionExpression.ColumnInfo>(columns.Count);
    foreach (var column in columns) {
      if (column.TypeMapping is WhizbangIdTypeMapping) {
        asText.Add(new PgTableValuedFunctionExpression.ColumnInfo(column.Name, text));
      } else {
        asText.Add(column);
      }
    }
    return ((PgTableValuedFunctionExpression)base.VisitExtension(recordSet)).WithColumnInfos(asText);
  }

  /// <summary>
  /// <c>ids @&gt; to_jsonb(@id)</c> or the same test for the object holding it:
  /// <c>ids @&gt; jsonb_build_array(jsonb_build_object('Value', @id))</c>.
  /// </summary>
  private SqlBinaryExpression _eitherElementForm(PgBinaryExpression contains, SqlExpression id) {
    var left = (SqlExpression)Visit(contains.Left);
    var asObject = new SqlFunctionExpression(
      "jsonb_build_array",
      [new SqlFunctionExpression(
        "jsonb_build_object",
        [new SqlConstantExpression(VALUE, typeof(string), text), id],
        nullable: true,
        argumentsPropagateNullability: [false, false],
        typeof(string),
        jsonb)],
      nullable: true,
      argumentsPropagateNullability: [false],
      typeof(string),
      jsonb);
    return new SqlBinaryExpression(
      ExpressionType.OrElse,
      contains.Update(left, contains.Right),
      new PgBinaryExpression(PgExpressionType.Contains, left, asObject, typeof(bool), boolean),
      typeof(bool),
      boolean);
  }

  /// <summary>Finds the record set columns that hold an id.</summary>
  private sealed class RecordSetIds(Dictionary<(string Alias, string Name), WhizbangIdTypeMapping> found) : ExpressionVisitor {
    protected override Expression VisitExtension(Expression node) {
      switch (node) {
        case ShapedQueryExpression shaped:
          Visit(shaped.QueryExpression);
          return node;
        case PgTableValuedFunctionExpression { ColumnInfos: { } columns } recordSet:
          foreach (var column in columns) {
            if (column.TypeMapping is WhizbangIdTypeMapping mapping) {
              found[(recordSet.Alias, column.Name)] = mapping;
            }
          }
          break;
      }
      return base.VisitExtension(node);
    }
  }
}
