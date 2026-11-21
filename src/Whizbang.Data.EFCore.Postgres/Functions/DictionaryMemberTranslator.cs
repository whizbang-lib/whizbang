// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using Whizbang.Data.EFCore.Postgres.Perspectives;

#pragma warning disable EF1001 // Internal EF Core API usage - the JSON traversal expression is Npgsql's own, used as its translators use it

namespace Whizbang.Data.EFCore.Postgres.Functions;

/// <summary>
/// Translates a key lookup on a dictionary member of a perspective document to SQL over its stored form, an array of
/// key/value objects (#1911).
/// </summary>
/// <remarks>
/// <para>
/// <c>ContainsKey(k)</c> is a containment test, <c>@&gt; [{"Key": k}]</c>. When the dictionary sits at a fixed path in
/// the document it is asked of the document column itself (<c>data @&gt; {"Counts": [{"Key": k}]}</c>), which the
/// document's GIN index can answer; inside a collection element it is asked of the element's member.
/// </para>
/// <para>
/// An indexer, <c>m.Counts[k]</c>, is the value of the pair whose key is <c>k</c>:
/// <c>jsonb_path_query_first(member, '$[*] ? (@.Key == $k)', {"k": k}) -&gt;&gt; 'Value'</c>, cast to the value's
/// type. A missing key is SQL NULL, so a comparison with it is false rather than a thrown
/// <see cref="KeyNotFoundException"/>: a filter excludes the row.
/// </para>
/// <para>
/// The key is sent as the JSON the stored form writes for it, through the mapping's own key converter, so a key of any
/// type compares as it is stored. A key that is not a constant or a parameter, a value of a temporal or structured
/// type, or any other dictionary method is left untranslated, and Entity Framework refuses the query naming it. Nothing
/// is evaluated on the client.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryMemberStorageTests.cs:AKeyLookup_IsTranslatedToSqlAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryMemberStorageTests.cs:TheKeyLookupIsInThePlanAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryMemberTypeMappingTests.cs:ALookupTheTranslatorCannotAnswer_IsRefused_NeverEvaluatedOnTheClientAsync</tests>
public sealed class DictionaryMemberTranslator(
    NpgsqlSqlExpressionFactory sqlExpressionFactory,
    IRelationalTypeMappingSource typeMappingSource) : IMethodCallTranslator {
  private const string CONTAINS_KEY = "ContainsKey";
  private const string INDEXER = "get_Item";

  private readonly NpgsqlSqlExpressionFactory _sql = sqlExpressionFactory;
  private readonly IRelationalTypeMappingSource _mappings = typeMappingSource;

  /// <inheritdoc/>
  public SqlExpression? Translate(
      SqlExpression? instance,
      MethodInfo method,
      IReadOnlyList<SqlExpression> arguments,
      IDiagnosticsLogger<DbLoggerCategory.Query> logger) {
    if (instance?.TypeMapping is not DictionaryMemberTypeMapping mapping
        || arguments.Count != 1
        || arguments[0] is not (SqlConstantExpression or SqlParameterExpression)) {
      return null;
    }

    return method.Name switch {
      CONTAINS_KEY => _containsKey(instance, _keyJson(arguments[0], mapping)),
      INDEXER => _valueOf(instance, _keyJson(arguments[0], mapping), method.ReturnType),
      _ => null,
    };
  }

  private RelationalTypeMapping _jsonb => _mappings.FindMapping("jsonb")!;

  private RelationalTypeMapping _text => _mappings.FindMapping(typeof(string))!;

  /// <summary>The key as jsonb: the parameter or constant sent as its stored JSON text, cast.</summary>
  private SqlExpression _keyJson(SqlExpression key, DictionaryMemberTypeMapping mapping) {
    var asJsonText = _sql.ApplyTypeMapping(key, (RelationalTypeMapping)_text.WithComposedConverter(mapping.KeyToJson));
    return _sql.Convert(asJsonText, typeof(string), _jsonb);
  }

  private SqlExpression _buildObject(string name, SqlExpression value) =>
    _sql.Function("jsonb_build_object", [_sql.Constant(name, _text), value], nullable: true,
      argumentsPropagateNullability: [false, false], typeof(string), _jsonb);

  private SqlExpression _containsKey(SqlExpression instance, SqlExpression keyJson) {
    var probe = _sql.Function("jsonb_build_array", [_buildObject("Key", keyJson)], nullable: true,
      argumentsPropagateNullability: [false], typeof(string), _jsonb);

    // A dictionary at a fixed path asks the document column, so the document's GIN index can answer.
    if (instance is JsonScalarExpression { Json: var document } scalar
        && scalar.Path.All(segment => segment.PropertyName is not null)) {
      for (var i = scalar.Path.Count - 1; i >= 0; i--) {
        probe = _buildObject(scalar.Path[i].PropertyName!, probe);
      }
      return _sql.MakePostgresBinary(PgExpressionType.Contains, document, probe);
    }

    return _sql.MakePostgresBinary(PgExpressionType.Contains, instance, probe);
  }

  private SqlExpression? _valueOf(SqlExpression instance, SqlExpression keyJson, Type valueType) {
    var valueMapping = _scalarMapping(valueType);
    if (valueMapping is null) {
      return null;
    }

    var pair = _sql.Function(
      "jsonb_path_query_first",
      [instance, _sql.Fragment("'$[*] ? (@.Key == $k)'::jsonpath"), _buildObject("k", keyJson)],
      nullable: true,
      argumentsPropagateNullability: [true, false, false],
      typeof(string),
      _jsonb);
    var text = new PgJsonTraversalExpression(pair, [_sql.Constant("Value", _text)], returnsText: true, typeof(string), _text);

    return valueType == typeof(string) ? text : _sql.Convert(text, valueType, valueMapping);
  }

  /// <summary>
  /// The mapping a value is cast to from its JSON text, or null for a value that text cannot be cast to faithfully: a
  /// temporal (stored as a count of microseconds) or a type with no scalar mapping (an object, a collection).
  /// </summary>
  private RelationalTypeMapping? _scalarMapping(Type valueType) =>
    CanonicalTemporalConvention.KindOf(valueType) is null && !_isStructured(valueType)
      ? _mappings.FindMapping(valueType)
      : null;

  private static bool _isStructured(Type valueType) =>
    valueType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(valueType);
}

/// <summary>Registers <see cref="DictionaryMemberTranslator"/> with the provider.</summary>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryMemberStorageTests.cs:AKeyLookup_IsTranslatedToSqlAsync</tests>
public sealed class DictionaryMemberTranslatorPlugin(
    ISqlExpressionFactory sqlExpressionFactory,
    IRelationalTypeMappingSource typeMappingSource) : IMethodCallTranslatorPlugin {
  /// <inheritdoc/>
  public IEnumerable<IMethodCallTranslator> Translators { get; } = [
    new DictionaryMemberTranslator(
      sqlExpressionFactory as NpgsqlSqlExpressionFactory
        ?? throw new InvalidOperationException("Dictionary member queries are translated for the Npgsql provider."),
      typeMappingSource),
  ];
}
