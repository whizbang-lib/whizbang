// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Makes <c>ExecuteUpdate</c> set a strongly-typed id inside a document in the form the fleet reads, the same as
/// SaveChanges: the object <c>{"Value": ...}</c> while <see cref="WhizbangIdDocumentForm.WritesUuidString"/> says no,
/// the UUID string once it says yes, and JSON <c>null</c> for no id (#2518).
/// </summary>
/// <remarks>
/// <para>
/// The provider sets a member of a document with <c>jsonb_set(data, '{OrderId}', to_jsonb(@id))</c>, which writes an id
/// as its UUID string whatever the fleet reads, and for no id passes SQL NULL, which makes <c>jsonb_set</c> return NULL
/// for the whole document. Each <c>to_jsonb</c> of an id in an update's setters becomes
/// <c>CASE WHEN @id IS NULL THEN 'null' WHEN @uuid_string THEN to_jsonb(@id) ELSE jsonb_build_object('Value', @id) END</c>.
/// </para>
/// <para>
/// <c>@uuid_string</c> is a runtime parameter, read from <see cref="WhizbangIdDocumentForm.WritesUuidString"/> every time
/// the statement runs: a compiled query is cached for the life of the process, and the write form switches during it.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdDocumentStorageTests.cs:Whizbang_ExecuteUpdateWhileTheGateIsClosed_SetsAnIdInTheObjectFormAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdDocumentStorageTests.cs:Whizbang_OneCompiledExecuteUpdate_FollowsTheGateWhenItRunsAsync</tests>
[SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
  Justification = "The setter's value is rewritten after the provider built it; its jsonb functions have no public builder.")]
internal sealed class StrongIdSetterFormRewriter(
    QueryCompilationContext compilation, RelationalTypeMapping text, RelationalTypeMapping jsonb, RelationalTypeMapping boolean)
    : ExpressionVisitor {
  /// <summary>The runtime parameter that carries the write form into the statement.</summary>
  internal const string UUID_STRING_PARAMETER = "__wh_strong_id_uuid_string";

  private const string VALUE = "Value";

  private SqlParameterExpression? _uuidString;

  /// <inheritdoc/>
  protected override Expression VisitExtension(Expression node) {
    ArgumentNullException.ThrowIfNull(node);

    return node switch {
      // A query reads; only an update sets.
      ShapedQueryExpression => node,
      UpdateExpression update => update.Update(
        update.SelectExpression,
        [.. update.ColumnValueSetters.Select(s => new ColumnValueSetter(s.Column, (SqlExpression)new Setter(this).Visit(s.Value)))]),
      _ => base.VisitExtension(node),
    };
  }

  /// <summary>The write form, registered with the compilation the first time a setter needs it.</summary>
  private SqlParameterExpression _uuidStringParameter() {
    if (_uuidString is null) {
      compilation.RegisterRuntimeParameter(
        UUID_STRING_PARAMETER,
        Expression.Lambda<Func<QueryContext, bool>>(
          Expression.Property(null, typeof(WhizbangIdDocumentForm), nameof(WhizbangIdDocumentForm.WritesUuidString)),
          QueryCompilationContext.QueryContextParameter));
      _uuidString = new SqlParameterExpression(
        UUID_STRING_PARAMETER, UUID_STRING_PARAMETER, typeof(bool), nullable: false, translationMode: null, boolean);
    }
    return _uuidString;
  }

  /// <summary>The id's value in the form the fleet reads, as jsonb.</summary>
  private SqlExpression _inForm(SqlFunctionExpression asString, SqlExpression id) {
    var none = new SqlConstantExpression("null", typeof(string), jsonb);
    if (id is SqlConstantExpression { Value: null }) {
      return none;
    }
    var asObject = new SqlFunctionExpression(
      "jsonb_build_object",
      [new SqlConstantExpression(VALUE, typeof(string), text), id],
      nullable: false,
      argumentsPropagateNullability: [false, false],
      typeof(string),
      jsonb);
    return new CaseExpression(
      [
        new CaseWhenClause(new SqlUnaryExpression(ExpressionType.Equal, id, typeof(bool), boolean), none),
        new CaseWhenClause(_uuidStringParameter(), asString),
      ],
      asObject);
  }

  /// <summary>Rewrites each <c>to_jsonb</c> of an id in one setter's value.</summary>
  private sealed class Setter(StrongIdSetterFormRewriter owner) : ExpressionVisitor {
    protected override Expression VisitExtension(Expression node) {
      ArgumentNullException.ThrowIfNull(node);

      return node is SqlFunctionExpression { Name: "to_jsonb", Arguments: [{ TypeMapping: WhizbangIdTypeMapping } id] } asString
        ? owner._inForm(asString, id)
        : base.VisitExtension(node);
    }
  }
}
