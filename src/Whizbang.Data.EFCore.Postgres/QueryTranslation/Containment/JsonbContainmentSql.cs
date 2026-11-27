// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

/// <summary>
/// Builds the containment test itself: the operator, and the document the stored value is compared
/// against.
/// </summary>
/// <remarks>
/// <para>
/// Both mechanisms call this, which is the point of it existing. They differ in where they act and in
/// what they can see, but the document they build has to be identical, and two mechanisms each
/// building their own is two chances to build a different one. A divergence there would not be an
/// error: it would be a filter that quietly matches nothing.
/// </para>
/// <para>
/// Nothing here decides <em>whether</em> to compile a containment test. That judgement belongs to
/// each mechanism, because it is made against a different tree in each. This only knows how.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/JsonbContainmentSqlMatrixTests.cs</tests>
public static class JsonbContainmentSql {
  /// <summary>Neither argument of the document builder makes the result null on its own.</summary>
  private static readonly bool[] _argumentsPropagateNullability = [false, false];

  /// <summary>
  /// The store types whose jsonb rendering does not match what the serializer wrote.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the one piece of per-type knowledge in the reshape, and it is an exclusion rather than a
  /// dispatch: a short list of types to leave alone, not a table of how to handle each. A date is
  /// stored as the serializer's rendering with a trailing <c>Z</c>, while PostgreSQL renders the same
  /// instant with an explicit offset, so a document built from a timestamp parameter would compare a
  /// different string and match nothing.
  /// </para>
  /// <para>
  /// It disappears when the stored form becomes a number, at which point a date needs nothing special
  /// from either mechanism. Until then this is what keeps the reshape from being wrong about dates
  /// rather than merely unhelpful.
  /// </para>
  /// </remarks>
  public static bool StoredFormNeedsRendering(Type clrType) {
    ArgumentNullException.ThrowIfNull(clrType);

    var bare = Nullable.GetUnderlyingType(clrType) ?? clrType;

    return bare == typeof(DateTime)
        || bare == typeof(DateTimeOffset)
        || bare == typeof(DateOnly)
        || bare == typeof(TimeOnly)
        || bare == typeof(TimeSpan);
  }

  /// <summary>
  /// <c>&lt;column&gt; @&gt; jsonb_build_object('Key', &lt;value&gt;)</c>, nested one object per path
  /// segment so a member at any depth compares against the right place.
  /// </summary>
  /// <param name="member">The translated JSON member, which carries both the column and the path.</param>
  /// <param name="value">The value to compare against, already translated.</param>
  /// <returns>
  /// The containment test, or null when the member's shape cannot be expressed as one. For a strong id, the test for
  /// either stored form: the UUID string, or the object <c>{"Value": ...}</c> an earlier release wrote.
  /// </returns>
  /// <remarks>
  /// Returns null rather than throwing for a shape it cannot express, because every caller's correct
  /// response is the same: leave the comparison alone. An array index is the case that arises, since
  /// it is a position rather than a key and containment has no way to say so.
  /// </remarks>
  [SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
    Justification = "PgUnknownBinaryExpression is the provider's only seam for an arbitrary operator, " +
      "and containment is the one operator a GIN index answers. There is no public equivalent: " +
      "SqlBinaryExpression has no containment member, and the function form jsonb_contains() is not " +
      "matched to the index by the planner. ProviderCapabilities pins the versions this was validated " +
      "against, so a bump fails a test rather than quietly degrading every perspective query.")]
  public static SqlExpression? TryBuild(JsonScalarExpression member, SqlExpression value) {
    ArgumentNullException.ThrowIfNull(member);
    ArgumentNullException.ThrowIfNull(value);

    if (member.Json.TypeMapping is not { } jsonb || _document(member, value, jsonb) is not { } document) {
      return null;
    }

    var contains = new PgUnknownBinaryExpression(member.Json, document, "@>", typeof(bool), BoolTypeMapping.Default);
    if (member.TypeMapping is not WhizbangIdTypeMapping) {
      return contains;
    }

    // A strong id an earlier release stored as {"Value": ...} (#2518): until the stored-form rewrite reaches the row,
    // or for a row an instance of that release writes, the id is matched in that form too. Both arms are containment,
    // so the document index answers each.
    var asObject = _document(member, _object(STRONG_ID_VALUE, value, jsonb), jsonb)!;
    return new SqlBinaryExpression(
      ExpressionType.OrElse,
      contains,
      new PgUnknownBinaryExpression(member.Json, asObject, "@>", typeof(bool), BoolTypeMapping.Default),
      typeof(bool),
      BoolTypeMapping.Default);
  }

  /// <summary>The member an id's object form keeps its value under.</summary>
  private const string STRONG_ID_VALUE = "Value";

  /// <summary>The value nested one object per path segment, so a.b.c becomes {"a":{"b":{"c":value}}}.</summary>
  /// <returns>The document, or null when a segment is a position rather than a key.</returns>
  private static SqlExpression? _document(JsonScalarExpression member, SqlExpression value, RelationalTypeMapping jsonb) {
    var payload = value;

    // Built from the inside out.
    for (var i = member.Path.Count - 1; i >= 0; i--) {
      if (member.Path[i].PropertyName is not { } name) {
        return null;
      }

      payload = _object(name, payload, jsonb);
    }

    return payload;
  }

  private static SqlFunctionExpression _object(string key, SqlExpression value, RelationalTypeMapping jsonb) =>
    new(
      "jsonb_build_object",
      [new SqlConstantExpression(key, typeof(string), StringTypeMapping.Default), value],
      nullable: true,
      argumentsPropagateNullability: _argumentsPropagateNullability,
      typeof(string),
      jsonb);
}
