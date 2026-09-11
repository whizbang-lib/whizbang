using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Marker methods whose translation compiles a perspective filter into a jsonb containment test,
/// which is the only shape the GIN index on the data column can answer.
/// </summary>
/// <remarks>
/// <para>
/// A perspective stores its model as a JSON document, and a property comparison would otherwise
/// compile to a text extraction with a cast, for example
/// <c>CAST(data -&gt;&gt; 'TenantId' AS uuid) = @p</c>. PostgreSQL cannot answer that from the GIN
/// index the table already carries, because GIN with the default operator class answers containment
/// and existence and nothing else. Rewriting the same filter as
/// <c>data @&gt; jsonb_build_object('TenantId', @p)</c> puts the bare column on the left of the
/// containment operator, which is what the index matches.
/// </para>
/// <para>
/// These methods are never called. They exist so a rewritten expression tree has something for EF
/// Core to translate, and each is registered with <c>HasDbFunction(...).HasTranslation(...)</c> by
/// <see cref="JsonbContainmentModelExtensions.UseWhizbangJsonbContainment"/>. One overload per CLR
/// type is required because EF Core needs a closed method with mappable parameter types; an open
/// generic cannot be registered.
/// </para>
/// <para>
/// <strong>Trimming and native AOT.</strong> Every <see cref="MethodInfo"/> here is captured from a
/// delegate over a statically referenced method, never looked up by name, so the trimmer keeps what
/// is used and nothing resolves dynamically at run time. The type switch in
/// <see cref="OverloadFor"/> is likewise static.
/// </para>
/// <para>
/// The set of types is deliberately narrow. It covers values whose JSON form written by the
/// serializer and whose JSON form produced by <c>jsonb_build_object</c> are the same text. Dates,
/// times and enumerations are excluded because those two forms differ and a containment test would
/// silently match nothing. <c>double</c> and <c>float</c> are excluded for the same reason, since
/// their shortest round-trip text is not guaranteed to agree with PostgreSQL's.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#index-advisories</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/JsonbContainmentSqlMatrixTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/GinContainmentIntegrationTests.cs</tests>
public static class JsonbContainment {
  /// <summary>Containment test for a string member.</summary>
  /// <param name="member">The JSON member being compared.</param>
  /// <param name="value">The value it is compared with.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool Matches(string member, string value) => throw _notCallable();

  /// <summary>Containment test for a <see cref="Guid"/> member.</summary>
  /// <param name="member">The JSON member being compared.</param>
  /// <param name="value">The value it is compared with.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool Matches(Guid member, Guid value) => throw _notCallable();

  /// <summary>Containment test for a <see cref="bool"/> member.</summary>
  /// <param name="member">The JSON member being compared.</param>
  /// <param name="value">The value it is compared with.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool Matches(bool member, bool value) => throw _notCallable();

  /// <summary>Containment test for a <see cref="short"/> member.</summary>
  /// <param name="member">The JSON member being compared.</param>
  /// <param name="value">The value it is compared with.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool Matches(short member, short value) => throw _notCallable();

  /// <summary>Containment test for an <see cref="int"/> member.</summary>
  /// <param name="member">The JSON member being compared.</param>
  /// <param name="value">The value it is compared with.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool Matches(int member, int value) => throw _notCallable();

  /// <summary>Containment test for a <see cref="long"/> member.</summary>
  /// <param name="member">The JSON member being compared.</param>
  /// <param name="value">The value it is compared with.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool Matches(long member, long value) => throw _notCallable();

  /// <summary>Containment test for a <see cref="decimal"/> member.</summary>
  /// <param name="member">The JSON member being compared.</param>
  /// <param name="value">The value it is compared with.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool Matches(decimal member, decimal value) => throw _notCallable();

  private static NotSupportedException _notCallable() =>
    new("JsonbContainment.Matches is a query marker and is only valid inside a LINQ query over a perspective.");

  // Captured from delegates over statically referenced methods: no name lookups, nothing the
  // trimmer can fail to see, and nothing resolved at run time.
  private static readonly MethodInfo _stringOverload = ((Func<string, string, bool>)Matches).Method;
  private static readonly MethodInfo _guidOverload = ((Func<Guid, Guid, bool>)Matches).Method;
  private static readonly MethodInfo _boolOverload = ((Func<bool, bool, bool>)Matches).Method;
  private static readonly MethodInfo _shortOverload = ((Func<short, short, bool>)Matches).Method;
  private static readonly MethodInfo _intOverload = ((Func<int, int, bool>)Matches).Method;
  private static readonly MethodInfo _longOverload = ((Func<long, long, bool>)Matches).Method;
  private static readonly MethodInfo _decimalOverload = ((Func<decimal, decimal, bool>)Matches).Method;

  private static readonly MethodInfo[] _allOverloads = [
    _stringOverload, _guidOverload, _boolOverload,
    _shortOverload, _intOverload, _longOverload, _decimalOverload,
  ];

  /// <summary>Every overload, for registration.</summary>
  internal static IReadOnlyList<MethodInfo> Overloads => _allOverloads;

  /// <summary>
  /// The overload that accepts <paramref name="type"/>, or null when containment is not safe for it.
  /// A nullable member resolves to its underlying overload, because a rewrite only happens when the
  /// compared value is not null.
  /// </summary>
  /// <param name="type">The member's CLR type.</param>
  /// <returns>The matching overload, or null.</returns>
  internal static MethodInfo? OverloadFor(Type type) {
    var bare = Nullable.GetUnderlyingType(type) ?? type;

    if (bare == typeof(string)) { return _stringOverload; }
    if (bare == typeof(Guid)) { return _guidOverload; }
    if (bare == typeof(bool)) { return _boolOverload; }
    if (bare == typeof(short)) { return _shortOverload; }
    if (bare == typeof(int)) { return _intOverload; }
    if (bare == typeof(long)) { return _longOverload; }
    if (bare == typeof(decimal)) { return _decimalOverload; }

    return null;
  }

  /// <summary>
  /// Builds <c>&lt;column&gt; @&gt; jsonb_build_object('Key', &lt;value&gt;)</c> from the translated
  /// arguments, nesting one object per path segment so a nested member works the same way.
  /// </summary>
  /// <param name="args">The translated arguments: the JSON member, then the value.</param>
  /// <returns>The containment expression, or the untouched member when the shape is unexpected.</returns>
  [SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
    Justification = "PgUnknownBinaryExpression is the provider's only seam for emitting an arbitrary " +
      "operator, and containment is the one operator a GIN index answers. There is no public equivalent: " +
      "SqlBinaryExpression's ExpressionType has no containment member, and the function form " +
      "jsonb_contains() is not matched to the index by the planner. ProviderCapabilities pins the provider " +
      "versions this was validated against, so a bump fails a test rather than silently degrading queries.")]
  internal static SqlExpression Emit(IReadOnlyList<SqlExpression> args) {
    ArgumentNullException.ThrowIfNull(args);

    if (args.Count != 2) {
      throw new ArgumentException("A containment translation takes exactly two arguments.", nameof(args));
    }

    if (args[0] is not JsonScalarExpression json || json.Json.TypeMapping is null) {
      // Not the shape this rewrite understands. Fall back to the comparison the query originally
      // expressed, so an unexpected tree costs an index rather than a wrong answer.
      return _equality(args[0], args[1]);
    }

    SqlExpression payload = args[1];

    // Build the containment document from the inside out, so a.b.c becomes {"a":{"b":{"c":value}}}.
    for (var i = json.Path.Count - 1; i >= 0; i--) {
      var name = json.Path[i].PropertyName;
      if (name is null) {
        // An array index cannot be expressed as a containment key.
        return _equality(args[0], args[1]);
      }

      payload = new SqlFunctionExpression(
        "jsonb_build_object",
        [new SqlConstantExpression(name, typeof(string), StringTypeMapping.Default), payload],
        nullable: true,
        argumentsPropagateNullability: _argumentsPropagateNullability,
        typeof(string),
        json.Json.TypeMapping);
    }

    return new PgUnknownBinaryExpression(json.Json, payload, "@>", typeof(bool), BoolTypeMapping.Default);
  }

  /// <summary>
  /// The comparison the query originally expressed, used when a tree turns out not to be the shape
  /// the rewrite understands. Losing an index is acceptable; changing an answer is not.
  /// </summary>
  private static SqlBinaryExpression _equality(SqlExpression left, SqlExpression right) =>
    new SqlBinaryExpression(
      System.Linq.Expressions.ExpressionType.Equal, left, right, typeof(bool), BoolTypeMapping.Default);

  private static readonly bool[] _argumentsPropagateNullability = [false, false];
}

/// <summary>
/// Registers the containment translations on a model.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#index-advisories</docs>
public static class JsonbContainmentModelExtensions {
  /// <summary>
  /// Registers every <see cref="JsonbContainment"/> overload with its SQL translation. Without this
  /// the rewriter stands down, so a model that has not opted in keeps the extraction form.
  /// </summary>
  /// <param name="modelBuilder">The model being configured.</param>
  /// <returns>The same builder, for chaining.</returns>
  public static ModelBuilder UseWhizbangJsonbContainment(this ModelBuilder modelBuilder) {
    ArgumentNullException.ThrowIfNull(modelBuilder);

    foreach (var overload in JsonbContainment.Overloads) {
      modelBuilder.HasDbFunction(overload).HasTranslation(JsonbContainment.Emit);
    }

    return modelBuilder;
  }
}
