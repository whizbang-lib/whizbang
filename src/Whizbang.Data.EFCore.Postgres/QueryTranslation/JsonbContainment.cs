using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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
/// The set of types covers values whose stored JSON number or string and whose form produced by
/// <c>jsonb_build_object</c> are the same value, which was established by writing rows through the
/// real mapping and reading back what landed rather than by assumption.
/// <c>ContainmentTypeEligibilityProbeTests</c> holds that measurement. The comparison is by value
/// and not by text, so a number written with different trailing digits still matches.
/// <para>
/// An enumeration resolves to its underlying numeric overload, because that is how it is stored.
/// Binary floating point is included, with the compared value cast to the member's store type so
/// PostgreSQL renders it the way the serializer did; see <c>_normalized</c> for why a literal needs
/// that and a parameter does not. It agreed for every awkward value tried, including a third, a very
/// large magnitude and a very small one.
/// </para>
/// <para>
/// A <c>DateTime</c> is included and is the one type whose value is rendered rather than passed
/// through, because a date is stored as a JSON string and strings compare character for character.
/// <see cref="_asStoredText"/> builds that rendering and explains each part;
/// <c>PerspectiveDateFormatLockTests</c> pins the format on both sides so the serializer and the
/// query cannot drift apart.
/// </para>
/// <para>
/// A <c>DateTimeOffset</c> is excluded, and not for want of effort. Its stored text preserves the
/// offset the row was written with, while equality compares instants, so one instant corresponds to
/// many stored texts that are all equal to it, and no rendering of that instant can produce them
/// all. The exclusion is a property of the comparison rather than of the formatting, which is what
/// separates it from <c>DateTime</c>.
/// </para>
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/JsonbContainmentSqlMatrixTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/GinContainmentIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/PerspectiveSqlShapeTests.cs</tests>
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

  /// <summary>Containment test for a <see cref="double"/> member.</summary>
  /// <param name="member">The JSON member being compared.</param>
  /// <param name="value">The value it is compared with.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool Matches(double member, double value) => throw _notCallable();

  /// <summary>Containment test for a <see cref="float"/> member.</summary>
  /// <param name="member">The JSON member being compared.</param>
  /// <param name="value">The value it is compared with.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool Matches(float member, float value) => throw _notCallable();

  /// <summary>Containment test for a <see cref="byte"/> member, which is how a small enum arrives.</summary>
  /// <param name="member">The JSON member being compared.</param>
  /// <param name="value">The value it is compared with.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool Matches(byte member, byte value) => throw _notCallable();

  /// <summary>Containment test for a <see cref="DateTime"/> member.</summary>
  /// <param name="member">The JSON member being compared.</param>
  /// <param name="value">The value it is compared with.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  /// <remarks>
  /// A date is stored as a JSON string, so unlike every other overload here the value has to be
  /// rendered into the exact text the serializer wrote rather than passed through.
  /// <see cref="_normalized"/> does that, and <c>PerspectiveDateFormatLockTests</c> pins the format
  /// on both sides so the two cannot drift apart.
  /// </remarks>
  public static bool Matches(DateTime member, DateTime value) => throw _notCallable();

  /// <summary>Set-membership test for a string member.</summary>
  /// <param name="member">The JSON member being tested.</param>
  /// <param name="values">The candidate values.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool MatchesAny(string member, string[] values) => throw _notCallable();

  /// <summary>Set-membership test for a <see cref="Guid"/> member.</summary>
  /// <param name="member">The JSON member being tested.</param>
  /// <param name="values">The candidate values.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool MatchesAny(Guid member, Guid[] values) => throw _notCallable();

  /// <summary>Set-membership test for an <see cref="int"/> member.</summary>
  /// <param name="member">The JSON member being tested.</param>
  /// <param name="values">The candidate values.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool MatchesAny(int member, int[] values) => throw _notCallable();

  /// <summary>Set-membership test for a <see cref="long"/> member.</summary>
  /// <param name="member">The JSON member being tested.</param>
  /// <param name="values">The candidate values.</param>
  /// <returns>Never returns; the call is translated to SQL.</returns>
  public static bool MatchesAny(long member, long[] values) => throw _notCallable();

  /// <inheritdoc cref="MatchesAny(string, string[])"/>
  public static bool MatchesAny(string member, List<string> values) => throw _notCallable();

  /// <inheritdoc cref="MatchesAny(string, string[])"/>
  public static bool MatchesAny(Guid member, List<Guid> values) => throw _notCallable();

  /// <inheritdoc cref="MatchesAny(string, string[])"/>
  public static bool MatchesAny(int member, List<int> values) => throw _notCallable();

  /// <inheritdoc cref="MatchesAny(string, string[])"/>
  public static bool MatchesAny(long member, List<long> values) => throw _notCallable();

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
  private static readonly MethodInfo _doubleOverload = ((Func<double, double, bool>)Matches).Method;
  private static readonly MethodInfo _floatOverload = ((Func<float, float, bool>)Matches).Method;
  private static readonly MethodInfo _byteOverload = ((Func<byte, byte, bool>)Matches).Method;
  private static readonly MethodInfo _dateTimeOverload = ((Func<DateTime, DateTime, bool>)Matches).Method;

  private static readonly MethodInfo _stringSet = ((Func<string, string[], bool>)MatchesAny).Method;
  private static readonly MethodInfo _guidSet = ((Func<Guid, Guid[], bool>)MatchesAny).Method;
  private static readonly MethodInfo _intSet = ((Func<int, int[], bool>)MatchesAny).Method;
  private static readonly MethodInfo _longSet = ((Func<long, long[], bool>)MatchesAny).Method;

  private static readonly MethodInfo _stringSetList = ((Func<string, List<string>, bool>)MatchesAny).Method;
  private static readonly MethodInfo _guidSetList = ((Func<Guid, List<Guid>, bool>)MatchesAny).Method;
  private static readonly MethodInfo _intSetList = ((Func<int, List<int>, bool>)MatchesAny).Method;
  private static readonly MethodInfo _longSetList = ((Func<long, List<long>, bool>)MatchesAny).Method;

  private static readonly MethodInfo[] _setOverloads = [
    _stringSet, _guidSet, _intSet, _longSet,
    _stringSetList, _guidSetList, _intSetList, _longSetList,
  ];

  /// <summary>Every set-membership overload, for registration.</summary>
  internal static IReadOnlyList<MethodInfo> SetOverloads => _setOverloads;

  /// <summary>
  /// The set-membership overload for a member type and the collection shape holding the candidates,
  /// or null when membership cannot be compiled to containment for that pair.
  /// </summary>
  /// <remarks>
  /// A narrower set than the equality overloads on purpose. The candidates arrive already
  /// parameterized, so the overload has to accept the collection exactly as written, and only the
  /// shapes Npgsql maps to a PostgreSQL array qualify.
  /// </remarks>
  /// <param name="memberType">The member's CLR type.</param>
  /// <param name="collectionType">The CLR type of the candidate collection.</param>
  /// <returns>The matching overload, or null.</returns>
  internal static MethodInfo? SetOverloadFor(Type memberType, Type collectionType) {
    var bare = Nullable.GetUnderlyingType(memberType) ?? memberType;

    foreach (var overload in _setOverloads) {
      var parameters = overload.GetParameters();
      if (parameters[0].ParameterType == bare && parameters[1].ParameterType == collectionType) {
        return overload;
      }
    }

    return null;
  }

  private static readonly MethodInfo[] _allOverloads = [
    _stringOverload, _guidOverload, _boolOverload,
    _shortOverload, _intOverload, _longOverload, _decimalOverload,
    _doubleOverload, _floatOverload, _byteOverload, _dateTimeOverload,
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

    // An enumeration is stored as its underlying number, so it resolves to that overload. A member
    // configured with a converter is stored as text instead, and the rewriter stands down for it
    // before ever asking here.
    if (bare.IsEnum) {
      bare = Enum.GetUnderlyingType(bare);
    }

    if (bare == typeof(string)) { return _stringOverload; }
    if (bare == typeof(Guid)) { return _guidOverload; }
    if (bare == typeof(bool)) { return _boolOverload; }
    if (bare == typeof(short)) { return _shortOverload; }
    if (bare == typeof(int)) { return _intOverload; }
    if (bare == typeof(long)) { return _longOverload; }
    if (bare == typeof(decimal)) { return _decimalOverload; }
    if (bare == typeof(double)) { return _doubleOverload; }
    if (bare == typeof(float)) { return _floatOverload; }
    if (bare == typeof(byte)) { return _byteOverload; }
    if (bare == typeof(DateTime)) { return _dateTimeOverload; }

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

    if (_withoutPlantedConversion(args[0]) is not JsonScalarExpression json || json.Json.TypeMapping is null
        || !_storedFormIsNatural(json)) {
      // Not the shape this rewrite understands, or a value converter has changed what the document
      // holds. Fall back to the comparison the query originally expressed, so the cost is an index
      // rather than an answer.
      return _equality(args[0], args[1]);
    }

    SqlExpression payload = _normalized(args[1], json, json.Json.TypeMapping);

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
  /// Builds <c>&lt;column&gt; @&gt; ANY(jsonb_containment_set('Key', &lt;values&gt;))</c>, which is the
  /// set-membership form a GIN index answers.
  /// </summary>
  /// <param name="args">The translated arguments: the JSON member, then the candidate array.</param>
  /// <returns>The membership expression.</returns>
  /// <remarks>
  /// Only a top-level member is expressible this way. The helper builds single-key documents, and a
  /// nested path would need one nested document per candidate, which cannot be produced without a
  /// subquery. The rewriter therefore only offers depth-one members; anything else would be a bug
  /// here rather than a user error, so it fails loudly instead of guessing.
  /// </remarks>
  [SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
    Justification = "Same seam and same reasoning as Emit: the containment operator has no public expression type.")]
  internal static SqlExpression EmitSet(IReadOnlyList<SqlExpression> args) {
    ArgumentNullException.ThrowIfNull(args);

    if (args.Count != 2 || args[0] is not JsonScalarExpression json || json.Json.TypeMapping is null) {
      throw new InvalidOperationException("A set-membership translation expects a JSON member and a candidate array.");
    }

    if (!_storedFormIsNatural(json)) {
      throw new InvalidOperationException(
        "Set membership cannot be compiled for a property whose stored form is changed by a value converter.");
    }

    if (json.Path.Count != 1 || json.Path[0].PropertyName is not { } key) {
      throw new InvalidOperationException(
        "Set membership is only compiled for a top-level member; the rewriter should not have offered this path.");
    }

    var documents = new SqlFunctionExpression(
      "jsonb_containment_set",
      [new SqlConstantExpression(key, typeof(string), StringTypeMapping.Default), args[1]],
      nullable: true,
      argumentsPropagateNullability: _argumentsPropagateNullability,
      typeof(string),
      json.Json.TypeMapping);

    // "x @> ANY (y)" needs its parentheses: they are part of the ANY grammar, not decoration, and the
    // arbitrary-operator expression renders its operands bare. Npgsql's own ANY expression cannot
    // carry containment (it knows only Equal, Like and ILike), so the parentheses are produced by a
    // nameless function call, which renders as "(argument)".
    var parenthesized = new SqlFunctionExpression(
      string.Empty,
      [documents],
      nullable: true,
      argumentsPropagateNullability: _oneArgumentKeepsNullability,
      typeof(string),
      json.Json.TypeMapping);

    return new PgUnknownBinaryExpression(json.Json, parenthesized, "@> ANY", typeof(bool), BoolTypeMapping.Default);
  }


  /// <summary>
  /// The compared value, cast to the member's own store type when that is what makes the built
  /// document agree with the stored one.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Binary floating point is the case that needs this, and a literal is where it bites. The
  /// serializer writes the shortest text that round-trips the value, so a double 0.1 is stored as
  /// <c>0.1</c>. Entity Framework renders the same value as a literal in seventeen digits,
  /// <c>0.10000000000000001</c>, which round-trips to the same double but is a different number, and
  /// jsonb compares numbers by value. Without the cast the document would be built from the longer
  /// number and match nothing.
  /// </para>
  /// <para>
  /// Casting to the member's store type hands the normalization to PostgreSQL: the longer text parses
  /// to the same binary value, and converting that value to a jsonb number produces the shortest form
  /// again, which is the form the row holds. This is not sensitive to
  /// <c>extra_float_digits</c>, which was measured across its range. A parameter already arrives as
  /// the store type, so the cast changes nothing there, and the cast is immutable so the planner can
  /// still match the index.
  /// </para>
  /// <para>
  /// A date needs more than a cast, because it is stored as a JSON string and strings compare
  /// character for character. <see cref="_asStoredText"/> renders it.
  /// </para>
  /// <para>
  /// No other type needs anything. Values compare by number rather than by text, so an integer or a
  /// decimal written with a different number of trailing zeros still matches.
  /// </para>
  /// </remarks>
  private static SqlExpression _normalized(
    SqlExpression value, JsonScalarExpression member, RelationalTypeMapping jsonb) {
    var clrType = Nullable.GetUnderlyingType(member.Type) ?? member.Type;

    if (clrType == typeof(DateTime)) {
      return _asStoredText(value, member, jsonb);
    }

    if (clrType != typeof(double) && clrType != typeof(float)) {
      return value;
    }

    return new SqlUnaryExpression(ExpressionType.Convert, value, clrType, member.TypeMapping);
  }

  /// <summary>
  /// The instant, rendered as the JSON string the serializer wrote for it.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Builds the expression below, which reproduces the stored text exactly. Each part answers
  /// something measured rather than assumed, and <c>PerspectiveDateFormatLockTests</c> holds every
  /// one of those measurements:
  /// </para>
  /// <code>
  /// CASE WHEN isfinite(v) THEN to_jsonb(concat(rtrim(rtrim(to_char(timezone('UTC', v),
  /// 'YYYY-MM-DD"T"HH24:MI:SS.US'), '0'), '.'), 'Z')) ELSE to_jsonb(v) END
  /// </code>
  /// <para>
  /// The zone is pinned to UTC rather than left to the session, because <c>to_char</c> on a
  /// <c>timestamptz</c> renders in whatever zone the session is set to, and the stored text is always
  /// UTC. Converting first makes the rendering independent of the connection.
  /// </para>
  /// <para>
  /// Microseconds are the finest part written, and the mapping truncates to the same precision on the
  /// way in, so no digit is lost on either side even for a value carrying finer .NET ticks.
  /// </para>
  /// <para>
  /// The two trims are what make the fraction variable-width: the serializer writes no trailing zeros
  /// and omits the decimal point entirely for a whole second, while <c>to_char</c> always writes six
  /// digits. Trimming the zeros and then the point it leaves behind produces the same text. The order
  /// matters and the point trim is safe, because the zeros of a whole second stop at the point.
  /// </para>
  /// <para>
  /// The infinities are the one pair of values that cannot be formatted: a <c>DateTime.MaxValue</c> or
  /// <c>MinValue</c> is stored as the word <c>infinity</c> or <c>-infinity</c>, and <c>to_char</c>
  /// yields nothing at all for them. They are also exactly what a "never expires" sentinel is written
  /// as, so leaving them to silently match nothing would not be an edge case. <c>to_jsonb</c> spells
  /// them the way the document holds them, so the second branch delegates rather than hard-coding the
  /// words.
  /// </para>
  /// <para>
  /// Both branches produce jsonb rather than text so the result can be handed straight to
  /// <c>jsonb_build_object</c>, which embeds it as the JSON string it already is. The whole expression
  /// is stable rather than immutable, because <c>to_char</c> is; that is enough for the planner to
  /// treat it as a run-time constant and still answer from the index, which
  /// <c>GinContainmentIntegrationTests</c> verifies on a table large enough for the choice to matter.
  /// </para>
  /// </remarks>
  private static CaseExpression _asStoredText(
    SqlExpression value, JsonScalarExpression member, RelationalTypeMapping jsonb) {
    var utc = new SqlFunctionExpression(
      "timezone",
      [new SqlConstantExpression("UTC", typeof(string), StringTypeMapping.Default), value],
      nullable: true,
      argumentsPropagateNullability: _argumentsPropagateNullability,
      typeof(DateTime),
      member.TypeMapping);

    var formatted = new SqlFunctionExpression(
      "to_char",
      [utc, new SqlConstantExpression(STORED_DATE_FORMAT, typeof(string), StringTypeMapping.Default)],
      nullable: true,
      argumentsPropagateNullability: _argumentsPropagateNullability,
      typeof(string),
      StringTypeMapping.Default);

    SqlExpression trimmed = formatted;
    foreach (var trailing in _trimmedInOrder) {
      trimmed = new SqlFunctionExpression(
        "rtrim",
        [trimmed, new SqlConstantExpression(trailing, typeof(string), StringTypeMapping.Default)],
        nullable: true,
        argumentsPropagateNullability: _argumentsPropagateNullability,
        typeof(string),
        StringTypeMapping.Default);
    }

    // concat is used rather than the concatenation operator because it never returns null: the
    // trimmed rendering is not null on this branch, and a suffix that vanished would silently produce
    // a text that matches nothing.
    var withZone = new SqlFunctionExpression(
      "concat",
      [trimmed, new SqlConstantExpression("Z", typeof(string), StringTypeMapping.Default)],
      nullable: false,
      argumentsPropagateNullability: _argumentsPropagateNullability,
      typeof(string),
      StringTypeMapping.Default);

    return new CaseExpression(
      [new CaseWhenClause(_isFinite(value), _asJsonb(withZone, jsonb))],
      _asJsonb(value, jsonb));
  }

  /// <summary>Whether the instant is a real one rather than one of the two infinities.</summary>
  private static SqlFunctionExpression _isFinite(SqlExpression value) =>
    new(
      "isfinite",
      [value],
      nullable: true,
      argumentsPropagateNullability: _oneArgumentKeepsNullability,
      typeof(bool),
      BoolTypeMapping.Default);

  /// <summary>The value as a jsonb scalar, which is what a containment document is assembled from.</summary>
  private static SqlFunctionExpression _asJsonb(SqlExpression value, RelationalTypeMapping jsonb) =>
    new(
      "to_jsonb",
      [value],
      nullable: true,
      argumentsPropagateNullability: _oneArgumentKeepsNullability,
      typeof(string),
      jsonb);

  /// <summary>
  /// The widest fraction PostgreSQL can render, which is also the precision the mapping writes.
  /// Locked by <c>PerspectiveDateFormatLockTests</c>.
  /// </summary>
  private const string STORED_DATE_FORMAT = "YYYY-MM-DD\"T\"HH24:MI:SS.US";

  /// <summary>Trailing zeros first, then the decimal point they leave behind. The order matters.</summary>
  private static readonly string[] _trimmedInOrder = ["0", "."];

  /// <summary>
  /// The JSON member underneath the conversion the rewriter plants when a member's CLR type is not
  /// the overload's parameter type.
  /// </summary>
  /// <remarks>
  /// <para>
  /// An overload exists per stored type, not per model type, so an enumeration is compared through
  /// the overload for its underlying number and the rewriter adds a <c>Convert</c> to reach it. Most
  /// such conversions cost nothing at the store: a nullable's underlying type has the same store
  /// type, and Entity Framework drops the node. An enumeration's does not, because a value converter
  /// already sits between the model type and the store type, so the conversion survives translation
  /// as a cast wrapped around the member. Left in place it hides the column and the path, and the
  /// filter falls back to the extraction form it was rewritten to avoid.
  /// </para>
  /// <para>
  /// Only a conversion directly around a JSON member is removed, and only the one layer. The
  /// rewriter is the only thing that plants these markers and it converts nothing but a member to its
  /// own stored type, so there is no widening or narrowing here to preserve. Anything else is left
  /// alone and falls back.
  /// </para>
  /// </remarks>
  private static SqlExpression _withoutPlantedConversion(SqlExpression argument) =>
    argument is SqlUnaryExpression { OperatorType: ExpressionType.Convert, Operand: JsonScalarExpression member }
      ? member
      : argument;

  /// <summary>
  /// Whether the member's stored form is the natural form for its CLR type, or whether a value
  /// converter has changed it.
  /// </summary>
  /// <remarks>
  /// This is the difference between a lost index and a wrong answer. A property configured with a
  /// converter is written in the converter's form: an <c>int</c> with a string conversion lands in the
  /// document as <c>"7"</c>, not <c>7</c>. An extraction still matches it, because <c>-&gt;&gt;</c>
  /// renders a JSON string and a JSON number as the same text. A containment test does not, because it
  /// compares documents and a string is not a number, so the filter would return nothing at all and
  /// look fast while doing it.
  /// </remarks>
  private static bool _storedFormIsNatural(JsonScalarExpression json) {
    // The mapping carries the converter when one is configured, and comparing CLR types does not
    // reveal it: the mapping's ClrType stays the model type either way. The marker's own parameter is
    // typed by the CLR type, so the document would be built in the unconverted form while the row
    // holds the converted one.
    return json.TypeMapping is { } mapping && StoredFormIsNatural(mapping.Converter, mapping.ClrType);
  }

  /// <summary>
  /// Whether a property with this converter and CLR type is stored in the natural form for its type,
  /// so a containment document built from the unconverted value matches what the row holds.
  /// </summary>
  /// <param name="converter">The property's value converter, or null when it has none.</param>
  /// <param name="clrType">The property's model CLR type.</param>
  /// <returns>True when the stored form is the natural one.</returns>
  /// <remarks>
  /// <para>
  /// The rewriter asks this of the model before planting a marker and the translation asks it of the
  /// type mapping before emitting containment. Both have to answer the same way, because a
  /// disagreement is how a filter ends up compiled into a test that matches nothing: the rewriter
  /// would offer a property whose document the emission cannot build correctly.
  /// </para>
  /// <para>
  /// One conversion is not a hazard. An enumeration to its own underlying number is how every
  /// enumeration is stored, and the overload chosen for it is the underlying type's, so the document
  /// is built in exactly the stored form. Treating that as converted would exclude every enumeration
  /// for the wrong reason. An enumeration stored as its name is a real conversion and is excluded,
  /// because the document would be built with a number against a stored string.
  /// </para>
  /// </remarks>
  internal static bool StoredFormIsNatural(ValueConverter? converter, Type clrType) {
    ArgumentNullException.ThrowIfNull(clrType);

    if (converter is null) {
      return true;
    }

    var model = Nullable.GetUnderlyingType(clrType) ?? clrType;
    if (!model.IsEnum) {
      return false;
    }

    var provider = Nullable.GetUnderlyingType(converter.ProviderClrType) ?? converter.ProviderClrType;
    return provider == Enum.GetUnderlyingType(model);
  }

  /// <summary>
  /// The comparison the query originally expressed, used when a tree turns out not to be the shape
  /// the rewrite understands. Losing an index is acceptable; changing an answer is not.
  /// </summary>
  private static SqlBinaryExpression _equality(SqlExpression left, SqlExpression right) =>
    new(System.Linq.Expressions.ExpressionType.Equal, left, right, typeof(bool), BoolTypeMapping.Default);

  private static readonly bool[] _argumentsPropagateNullability = [false, false];
  private static readonly bool[] _oneArgumentKeepsNullability = [false];
}

/// <summary>
/// Registers the containment translations on a model.
/// </summary>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
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

    foreach (var overload in JsonbContainment.SetOverloads) {
      modelBuilder.HasDbFunction(overload).HasTranslation(JsonbContainment.EmitSet);
    }

    return modelBuilder;
  }
}
