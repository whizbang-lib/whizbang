// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Storage.Internal.Mapping;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Marker methods that build a jsonb document in SQL, so a filter on a promoted jsonb column can be
/// compiled into a containment test whose right-hand side is assembled from the query's own values.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PhysicalJsonbContainmentRewriter"/> replaces a filter such as
/// <c>row.Data.GridFilter[key].Contains(value)</c> with
/// <c>EF.Functions.JsonContains(column, Member(key, Array(Value(value))))</c>, which compiles to
/// <c>grid_filter @&gt; jsonb_build_object(@key, jsonb_build_array(to_jsonb(@value)))</c>. The
/// document is built in SQL rather than in .NET because by the time the rewrite runs Entity Framework
/// has lifted every captured variable out of the tree: the values are parameters with nothing attached.
/// </para>
/// <para>
/// The methods are never called. Each returns <see cref="string"/> as a stand-in for jsonb, which is
/// a type Entity Framework can map for a function's result; the translations produce jsonb. They are
/// registered by <see cref="JsonbContainmentModelExtensions.UseWhizbangJsonbContainment"/>, which every
/// generated context already calls.
/// </para>
/// <para>
/// <strong>Trimming and native AOT.</strong> Every <see cref="MethodInfo"/> is captured from a
/// delegate over a statically referenced method, never looked up by name.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-columns</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/PhysicalJsonbContainmentSqlTests.cs</tests>
public static class JsonbDocument {
  /// <summary>A text value as a jsonb scalar.</summary>
  /// <param name="value">The value.</param>
  /// <returns>Never returns; a query marker.</returns>
  public static string Value(string? value) => throw _notCallable();

  /// <summary>An identifier as a jsonb string.</summary>
  /// <param name="value">The value.</param>
  /// <returns>Never returns; a query marker.</returns>
  public static string Value(Guid? value) => throw _notCallable();

  /// <summary>A boolean as a jsonb boolean.</summary>
  /// <param name="value">The value.</param>
  /// <returns>Never returns; a query marker.</returns>
  public static string Value(bool? value) => throw _notCallable();

  /// <summary>A number as a jsonb number.</summary>
  /// <param name="value">The value.</param>
  /// <returns>Never returns; a query marker.</returns>
  public static string Value(byte? value) => throw _notCallable();

  /// <summary>A number as a jsonb number.</summary>
  /// <param name="value">The value.</param>
  /// <returns>Never returns; a query marker.</returns>
  public static string Value(short? value) => throw _notCallable();

  /// <summary>A number as a jsonb number.</summary>
  /// <param name="value">The value.</param>
  /// <returns>Never returns; a query marker.</returns>
  public static string Value(int? value) => throw _notCallable();

  /// <summary>A number as a jsonb number.</summary>
  /// <param name="value">The value.</param>
  /// <returns>Never returns; a query marker.</returns>
  public static string Value(long? value) => throw _notCallable();

  /// <summary>A number as a jsonb number.</summary>
  /// <param name="value">The value.</param>
  /// <returns>Never returns; a query marker.</returns>
  public static string Value(decimal? value) => throw _notCallable();

  /// <summary>A one-member object, <c>{key: value}</c>.</summary>
  /// <param name="key">The member's name.</param>
  /// <param name="value">The member's value, itself a jsonb document.</param>
  /// <returns>Never returns; a query marker.</returns>
  public static string Member(string key, string value) => throw _notCallable();

  /// <summary>A one-element array, <c>[element]</c>.</summary>
  /// <param name="element">The element, itself a jsonb document.</param>
  /// <returns>Never returns; a query marker.</returns>
  public static string Array(string element) => throw _notCallable();

  /// <summary>Two objects with no member in common, merged into one.</summary>
  /// <param name="left">The first object.</param>
  /// <param name="right">The second object.</param>
  /// <returns>Never returns; a query marker.</returns>
  public static string Merge(string left, string right) => throw _notCallable();

  private static NotSupportedException _notCallable() =>
    new("JsonbDocument is a query marker and is only valid inside a LINQ query over a perspective.");

  private static readonly MethodInfo _stringValue = ((Func<string?, string>)Value).Method;
  private static readonly MethodInfo _guidValue = ((Func<Guid?, string>)Value).Method;
  private static readonly MethodInfo _boolValue = ((Func<bool?, string>)Value).Method;
  private static readonly MethodInfo _byteValue = ((Func<byte?, string>)Value).Method;
  private static readonly MethodInfo _shortValue = ((Func<short?, string>)Value).Method;
  private static readonly MethodInfo _intValue = ((Func<int?, string>)Value).Method;
  private static readonly MethodInfo _longValue = ((Func<long?, string>)Value).Method;
  private static readonly MethodInfo _decimalValue = ((Func<decimal?, string>)Value).Method;

  /// <summary>The <see cref="Member"/> marker.</summary>
  internal static readonly MethodInfo MemberMethod = ((Func<string, string, string>)Member).Method;

  /// <summary>The <see cref="Array"/> marker.</summary>
  internal static readonly MethodInfo ArrayMethod = ((Func<string, string>)Array).Method;

  /// <summary>The <see cref="Merge"/> marker.</summary>
  internal static readonly MethodInfo MergeMethod = ((Func<string, string, string>)Merge).Method;

  private static readonly MethodInfo[] _values = [
    _stringValue, _guidValue, _boolValue, _byteValue, _shortValue, _intValue, _longValue, _decimalValue,
  ];

  /// <summary>
  /// The <see cref="Value(string)"/> overload for a value of this type, or null when the type's stored
  /// form and PostgreSQL's <c>to_jsonb</c> rendering of it are not guaranteed to agree.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Text, identifiers, booleans and integers render identically in both. A decimal is a JSON number on
  /// both sides and jsonb compares numbers by value, so <c>1.50</c> contains <c>1.5</c>.
  /// </para>
  /// <para>
  /// Left out on purpose: dates and times (stored as a canonical number that <c>to_jsonb</c> of a
  /// timestamp is not), enumerations (whose stored form depends on converters), and binary floating
  /// point (whose shortest round-trip text the two sides need not share). A filter over one of those
  /// stays as written.
  /// </para>
  /// </remarks>
  /// <param name="type">The value's type, nullable or not.</param>
  /// <returns>The overload, or null.</returns>
  internal static MethodInfo? ValueOverloadFor(Type type) {
    var bare = Nullable.GetUnderlyingType(type) ?? type;

    foreach (var overload in _values) {
      var parameter = overload.GetParameters()[0].ParameterType;
      if ((Nullable.GetUnderlyingType(parameter) ?? parameter) == bare) {
        return overload;
      }
    }

    return null;
  }

  /// <summary>
  /// The type every built document carries: jsonb. Typed here so the containment operator takes the document
  /// as it is, without a cast, and so a concatenation of two documents has a type to render with.
  /// </summary>
  [SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
    Justification = "The provider's jsonb mapping is the only one a built document can carry; it has no public " +
      "constructor surface, and ProviderCapabilities pins the provider versions this was validated against.")]
  private static readonly RelationalTypeMapping _jsonb = new NpgsqlJsonTypeMapping("jsonb", typeof(string), null);

  /// <summary><c>to_jsonb(value)</c>, with a literal cast to its own type.</summary>
  /// <remarks>
  /// A literal reaches SQL without a type, and <c>to_jsonb</c> is polymorphic: <c>to_jsonb('north')</c> is
  /// refused because the type cannot be decided. A parameter is already typed and is passed as it is.
  /// </remarks>
  internal static SqlExpression EmitValue(IReadOnlyList<SqlExpression> args) {
    var value = args[0] is SqlConstantExpression { TypeMapping: { } mapping } constant
      ? new SqlUnaryExpression(ExpressionType.Convert, constant, constant.Type, mapping)
      : args[0];

    return new SqlFunctionExpression(
      "to_jsonb", [value], nullable: true, argumentsPropagateNullability: _one, typeof(string), _jsonb);
  }

  /// <summary><c>jsonb_build_object(key, value)</c>.</summary>
  internal static SqlExpression EmitMember(IReadOnlyList<SqlExpression> args) =>
    new SqlFunctionExpression(
      "jsonb_build_object", [args[0], args[1]], nullable: true, argumentsPropagateNullability: _two, typeof(string),
      _jsonb);

  /// <summary><c>jsonb_build_array(element)</c>.</summary>
  internal static SqlExpression EmitArray(IReadOnlyList<SqlExpression> args) =>
    new SqlFunctionExpression(
      "jsonb_build_array", [args[0]], nullable: true, argumentsPropagateNullability: _one, typeof(string), _jsonb);

  /// <summary><c>left || right</c>.</summary>
  [SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
    Justification = "PgUnknownBinaryExpression is the provider's only seam for an arbitrary operator, the same one " +
      "the containment translation already relies on; jsonb concatenation has no public expression type.")]
  internal static SqlExpression EmitMerge(IReadOnlyList<SqlExpression> args) =>
    new PgUnknownBinaryExpression(args[0], args[1], "||", typeof(string), _jsonb);

  /// <summary>Registers every marker's translation.</summary>
  /// <param name="modelBuilder">The model being built.</param>
  internal static void Register(ModelBuilder modelBuilder) {
    foreach (var overload in _values) {
      modelBuilder.HasDbFunction(overload).HasTranslation(EmitValue);
    }

    modelBuilder.HasDbFunction(MemberMethod).HasTranslation(EmitMember);
    modelBuilder.HasDbFunction(ArrayMethod).HasTranslation(EmitArray);
    modelBuilder.HasDbFunction(MergeMethod).HasTranslation(EmitMerge);
  }

  private static readonly bool[] _one = [false];
  private static readonly bool[] _two = [false, false];
}
