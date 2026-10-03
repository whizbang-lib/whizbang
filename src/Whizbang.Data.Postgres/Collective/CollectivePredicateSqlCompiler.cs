using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// A jsonb column path a compiled predicate filters a VALUE against (equality, <c>&lt;&gt;</c>, <c>IN</c>, or an
/// ordering comparison) — e.g. <c>(wh_per_draft_job, "scope-&gt;&gt;'t'")</c>,
/// <c>(wh_per_status, "data-&gt;&gt;'Status'")</c> or, for an ordering, the numeric read it compares,
/// <c>(wh_per_job, "(data-&gt;&gt;'Ordinal')::numeric")</c>. Each is a candidate for a btree expression index
/// <c>CREATE INDEX … ON &lt;Table&gt; ((&lt;ColumnExpression&gt;))</c>; a plain <c>gin(data)</c>/<c>gin(scope)</c>
/// index cannot serve <c>-&gt;&gt;</c> equality, so without these every apply seq-scans. <c>ColumnExpression</c>
/// is UNqualified (no table/alias prefix) so it drops straight into the index DDL. The top-level <c>id</c>
/// correlation column is never recorded — it is already the primary key. Driver-neutral and non-generic so the
/// EF Core index ensurer can consume the paths a <see cref="CollectivePredicateSqlCompiler{TModel}"/> emitted.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveUnitTests.cs:ReferencedJsonPaths_ScopeAndData_RecordsBothColumnsForOuterTableAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveUnitTests.cs:ReferencedJsonPaths_CrossPerspectiveAny_RecordsSiblingTableColumnAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveUnitTests.cs:ReferencedJsonPaths_ContainsInClause_RecordsTheColumnAsync</tests>
public sealed record ReferencedJsonPath(string Table, string ColumnExpression);

/// <summary>
/// Shared (driver-neutral) compiler that translates a collective apply's composed <c>WHERE</c> predicate
/// (<see cref="Expression{TDelegate}"/> of <c>Func&lt;PerspectiveRow&lt;TModel&gt;, bool&gt;</c>) into a
/// Postgres SQL <c>WHERE</c> fragment over the <c>scope</c> and <c>data</c> jsonb columns + a parameter
/// dictionary. The predicate is the output of <see cref="CollectiveWhereComposer"/>: a resolver scope
/// envelope (over <c>row.Scope</c>) AND-ed with the handler's per-model projection (over <c>row.Data</c>,
/// and/or a cross-perspective <c>q.Of&lt;TOther&gt;().Any(...)</c>).
/// </summary>
/// <remarks>
/// <para>
/// Both drivers use this so there is one predicate-translation code path: the Dapper applier (which has no
/// LINQ provider) and the EF Core raw jsonb_set path (which cannot use <c>ExecuteUpdateAsync</c> for scalar/
/// polymorphic jsonb or null-valued setters) both emit <c>UPDATE … WHERE &lt;compiled&gt;</c> — no id
/// materialization, no <c>SELECT id</c> round-trip.
/// </para>
/// <para>
/// <strong>Supported:</strong> equality over a single jsonb-column field — <c>row.Scope.PropName == value</c>
/// (→ <c>scope-&gt;&gt;'PropName' = @param</c>) or <c>row.Data.PropName == value</c> (→
/// <c>data-&gt;&gt;'PropName' = @param</c>); the top-level <c>row.Id</c> (→ <c>id</c>, for correlation);
/// <c>&amp;&amp;</c>-chained conjunctions; <c>&lt;values&gt;.Contains(row.Data.X)</c> (→ <c>IN</c>); ordering
/// comparisons (<c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>) over a numeric or temporal member, a jsonb path
/// read as a number and a physical column compared as itself, with <c>??</c> coalescing; and
/// cross-perspective cohorts <c>q.Of&lt;TOther&gt;().Any(s =&gt; s.Id == r.Id &amp;&amp; …)</c> (→ a correlated
/// <c>EXISTS (SELECT 1 FROM &lt;TOther table&gt; s WHERE …)</c>, the table resolved via
/// <see cref="ICollectiveSiblingTableSource"/> read off the <c>q.Of&lt;TOther&gt;()</c> node).
/// </para>
/// <para>
/// <strong>Ordering.</strong> A jsonb member is compared as <c>(data-&gt;&gt;'X')::numeric</c>, because <c>-&gt;&gt;</c> is
/// text and text orders <c>'10'</c> before <c>'9'</c>. A temporal member is stored as its microsecond count
/// (<see cref="CanonicalTemporalFormat"/>), so the value is bound as the same count and the comparison is exact to the
/// microsecond; a key still holding a rendering makes Postgres refuse the statement rather than answer it wrongly. A
/// missing key, or a JSON null, reads as SQL <c>NULL</c> and compares as null: the comparison is false, as a lifted
/// comparison with a null operand is in C#, and under <c>!</c> it is made false before the negation
/// (<c>NOT (COALESCE(a &lt; b, FALSE))</c>) so that <c>!(null &lt; 5)</c> is true in SQL exactly as in the in-memory
/// replay. To have a missing key count as a value, declare the member nullable and coalesce it:
/// <c>(r.Data.X ?? 0) &lt; e.Y</c> becomes <c>COALESCE((data-&gt;&gt;'X')::numeric, @p) &lt; @q</c>. The row id cannot be
/// ordered: Postgres orders a uuid by its bytes and .NET orders a Guid by its fields, so the two paths would disagree.
/// </para>
/// <para>
/// <strong>Unsupported (throws <see cref="NotSupportedException"/>):</strong> disjunctions, arbitrary
/// top-level/system columns (e.g. <c>row.Version</c>), nested <c>EXISTS</c>, ordering over the row id or over a
/// member that is neither numeric nor temporal, or comparisons not rooted at a known row parameter.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The perspective model whose <see cref="PerspectiveRow{TModel}"/> the filter ranges over.</typeparam>
/// <docs>fundamentals/messaging/collective-events</docs>
[SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Compiles captured-value sub-expressions; values come from compile-time selector metadata, not runtime type scanning.")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Compiles captured-value sub-expressions; values come from compile-time selector metadata, not runtime type scanning.")]
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Matches the established Whizbang.Data.Postgres pattern of generic-over-TModel static compilers.")]
public static class CollectivePredicateSqlCompiler<TModel> where TModel : class {
  /// <summary>Compiled <c>WHERE</c> fragment + the named parameters it binds + the jsonb column paths it
  /// filters on (§7 — btree expression-index candidates).</summary>
  public sealed record CompiledWhereClause(
    string SqlFragment,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<ReferencedJsonPath> ReferencedJsonPaths);

  /// <summary>
  /// Compile a collective-apply WHERE predicate to a SQL fragment over the <c>scope</c>/<c>data</c> jsonb
  /// columns — including correlated <c>EXISTS</c> subqueries for cross-perspective cohorts
  /// (<c>q.Of&lt;TOther&gt;().Any(...)</c>). Parameter names are namespaced by
  /// <paramref name="parameterPrefix"/> to avoid collisions with the SET-clause parameters.
  /// </summary>
  /// <param name="filter">The composed predicate (scope envelope, handler Where, and/or sibling-cohort Any).</param>
  /// <param name="parameterPrefix">Namespaces emitted parameter names.</param>
  /// <param name="outerTableName">
  /// The table being UPDATEd. Required to qualify the outer row inside a correlated <c>EXISTS</c>; pass it
  /// whenever the predicate may reference a sibling perspective.
  /// </param>
  public static CompiledWhereClause Compile(
      Expression<Func<PerspectiveRow<TModel>, bool>> filter,
      string parameterPrefix = "where",
      string? outerTableName = null) {
    ArgumentNullException.ThrowIfNull(filter);
    ArgumentException.ThrowIfNullOrWhiteSpace(parameterPrefix);

    var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
    var refs = new List<ReferencedJsonPath>();
    var sql = new StringBuilder();
    var ctx = new Ctx(filter.Parameters[0], OuterQualifier: "", InnerParam: null, InnerAlias: null,
      OuterTableName: outerTableName, InnerTableName: null);
    _compilePredicate(filter.Body, ctx, parameterPrefix, sql, parameters, refs);
    // Distinct so a column referenced twice (e.g. two comparisons on data->>'Status') yields one index candidate.
    var distinctRefs = refs.Distinct().ToList();
    return new CompiledWhereClause(sql.ToString(), parameters, distinctRefs);
  }

  // How to qualify a member access rooted at the outer row vs. an EXISTS inner row.
  // Negated: the node sits under a NOT, where SQL's three-valued logic and C#'s two-valued logic part ways.
  private sealed record Ctx(
    ParameterExpression OuterParam, string OuterQualifier,
    ParameterExpression? InnerParam, string? InnerAlias,
    string? OuterTableName, string? InnerTableName,
    bool Negated = false);

  private static void _compilePredicate(
      Expression node, Ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters,
      List<ReferencedJsonPath> refs) {
    if (node is BinaryExpression { NodeType: ExpressionType.AndAlso } and) {
      sql.Append('(');
      _compilePredicate(and.Left, ctx, prefix, sql, parameters, refs);
      sql.Append(" AND ");
      _compilePredicate(and.Right, ctx, prefix, sql, parameters, refs);
      sql.Append(')');
      return;
    }

    if (node is UnaryExpression { NodeType: ExpressionType.Not } not) {
      // `!q.Of<T>().Any(...)` (not-in-cohort) → NOT EXISTS; `!(pred)` → NOT (pred).
      sql.Append("NOT (");
      _compilePredicate(not.Operand, ctx with { Negated = true }, prefix, sql, parameters, refs);
      sql.Append(')');
      return;
    }

    if (node is BinaryExpression { NodeType: ExpressionType.Equal } eq) {
      _compileComparison(eq, "=", ctx, prefix, sql, parameters, refs);
      return;
    }

    if (node is BinaryExpression { NodeType: ExpressionType.NotEqual } neq) {
      _compileComparison(neq, "<>", ctx, prefix, sql, parameters, refs);
      return;
    }

    if (node is BinaryExpression {
      NodeType: ExpressionType.LessThan or ExpressionType.LessThanOrEqual
        or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
    } ordering) {
      _compileOrdering(ordering, ctx, prefix, sql, parameters, refs);
      return;
    }

    if (node is MethodCallExpression mc) {
      if (mc.Method.Name == "Any" && mc.Arguments.Count == 2) {
        _compileExists(mc, ctx, prefix, sql, parameters, refs);
        return;
      }
      if (mc.Method.Name == "Contains") {
        _compileContains(mc, ctx, prefix, sql, parameters, refs);
        return;
      }
    }

    throw new NotSupportedException(
      $"CollectivePredicateSqlCompiler<{typeof(TModel).Name}> supports equality over scope/data fields and id, " +
      "ordering (<, <=, >, >=) over numeric and temporal fields, &&-chains, Contains (→ IN), and " +
      "q.Of<TOther>().Any(...) cross-perspective cohorts (→ EXISTS). " +
      $"Got expression node kind '{node.NodeType}'. Provide a raw-SQL form for richer predicates.");
  }

  private static void _compileComparison(
      BinaryExpression cmp, string op, Ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters,
      List<ReferencedJsonPath> refs) {
    var left = _tryColumn(cmp.Left, ctx, refs);
    var right = _tryColumn(cmp.Right, ctx, refs);

    if (left is { } correlatedLeft && right is { } correlatedRight) {
      // Column <op> column → a correlation (e.g. s.id = wh_per_job.id). No parameter.
      sql.Append(correlatedLeft.Sql).Append(' ').Append(op).Append(' ').Append(correlatedRight.Sql);
      return;
    }
    if (left is { } leftColumn) {
      _appendColumnCompareValue(leftColumn, op, cmp.Right, prefix, sql, parameters);
      return;
    }
    if (right is { } rightColumn) {
      _appendColumnCompareValue(rightColumn, op, cmp.Left, prefix, sql, parameters);
      return;
    }
    throw new NotSupportedException(
      "CollectivePredicateSqlCompiler comparison requires at least one side to be a scope/data field or id " +
      "(row.Scope.X / row.Data.X / row.Id). Neither side matched.");
  }

  private static void _appendColumnCompareValue(
      in ResolvedColumn column, string op, Expression valueExpr, string prefix,
      StringBuilder sql, Dictionary<string, object?> parameters) {
    var value = _evaluateValue(valueExpr);
    var paramName = _uniqueName(parameters, $"{prefix}_{column.PropName.ToLowerInvariant()}");
    parameters[paramName] = _bind(value, column);
    sql.Append(_withDeclaredDefault(column, prefix, parameters)).Append(' ').Append(op).Append(" @").Append(paramName);
  }

  // An ordering comparison: each side is an ordered column (optionally coalesced) or a value; at least one is a
  // column. The operands keep their order, so `5 < r.Data.X` stays `@p < (data->>'X')::numeric`.
  private static void _compileOrdering(
      BinaryExpression cmp, Ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters,
      List<ReferencedJsonPath> refs) {
    var op = cmp.NodeType switch {
      ExpressionType.LessThan => "<",
      ExpressionType.LessThanOrEqual => "<=",
      ExpressionType.GreaterThan => ">",
      _ => ">=",
    };
    var left = _tryOrderedOperand(cmp.Left, ctx, prefix, parameters, refs);
    var right = _tryOrderedOperand(cmp.Right, ctx, prefix, parameters, refs);

    string leftSql;
    string rightSql;
    if (left is { } l && right is { } r) {
      _ensureSameScale(l, r);
      (leftSql, rightSql) = (l.Sql, r.Sql);
    } else if (left is { } column) {
      (leftSql, rightSql) = (column.Sql, _bindOrderedValue(column, cmp.Right, "", prefix, parameters));
    } else if (right is { } rightColumn) {
      (leftSql, rightSql) = (_bindOrderedValue(rightColumn, cmp.Left, "", prefix, parameters), rightColumn.Sql);
    } else {
      throw new NotSupportedException(
        "CollectivePredicateSqlCompiler ordering comparison requires at least one side to be a numeric or temporal " +
        "scope/data field (row.Scope.X / row.Data.X, optionally coalesced with ??). Neither side matched.");
    }

    // Under a NOT a null comparison must be false before it is negated: C#'s !(null < 5) is true, SQL's NOT (NULL)
    // is NULL and drops the row. Outside a NOT the plain form keeps the comparison usable by an index.
    var comparison = leftSql + " " + op + " " + rightSql;
    sql.Append(ctx.Negated ? "COALESCE(" + comparison + ", FALSE)" : comparison);
  }

  /// <summary>An ordering operand that is a column: its SQL, and what it is, which decides how a value binds.</summary>
  private readonly record struct OrderedColumn(string Sql, ResolvedColumn Column, OrderingScale Scale);

  /// <summary>
  /// What a compared number means, because two operands can be compared only on the same scale: a jsonb temporal
  /// is a microsecond count and a physical temporal column is a timestamp, a date, a time or an interval.
  /// </summary>
  private enum OrderingScale {
    /// <summary>A plain number, in a document or in a column.</summary>
    Number,

    /// <summary>A temporal stored in a document, as its microsecond count.</summary>
    StoredMicroseconds,

    /// <summary>A temporal in a physical column of its own type.</summary>
    ColumnTemporal,
  }

  // A column for an ordering comparison, or null when the operand is a value. A `??` over a column becomes
  // COALESCE(column, @default), the default bound on the column's terms.
  private static OrderedColumn? _tryOrderedOperand(
      Expression e, Ctx ctx, string prefix, Dictionary<string, object?> parameters, List<ReferencedJsonPath> refs) {
    while (e is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
      e = convert.Operand;
    }

    if (e is BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce) {
      if (_tryOrderedOperand(coalesce.Left, ctx, prefix, parameters, refs) is not { } inner) {
        // A value coalesced with a value (e.Y ?? 3) is a value; a value coalesced with a field has no SQL form.
        if (_tryOrderedOperand(coalesce.Right, ctx, prefix, parameters, refs) is null) {
          return null;
        }
        throw new NotSupportedException(
          "A ?? in a collective ordering comparison must coalesce a scope/data field: (row.Data.X ?? 0) < value.");
      }
      var fallback = _bindOrderedValue(inner, coalesce.Right, "_else", prefix, parameters);
      return inner with { Sql = "COALESCE(" + inner.Sql + ", " + fallback + ")" };
    }

    if (_tryColumn(e, ctx, refs, numeric: true) is not { } column) {
      return null;
    }
    if (column.Kind == ColumnKind.Uuid) {
      throw new NotSupportedException(
        "A collective predicate cannot order the row id. Postgres orders a uuid by its bytes and .NET orders a Guid by " +
        "its fields, so the SQL apply and the in-memory replay would disagree. Compare the id for equality, or order " +
        "on a numeric or temporal field.");
    }

    var type = Nullable.GetUnderlyingType(column.MemberType!) ?? column.MemberType!;
    var temporal = _isTemporal(type);
    if (!temporal && !_isNumeric(type)) {
      throw new NotSupportedException(
        $"{typeof(TModel).Name}.{column.PropName} is {type.Name}. A collective ordering comparison needs a numeric, " +
        "enumeration or temporal (DateTime, DateTimeOffset, DateOnly, TimeOnly, TimeSpan) member.");
    }
    var scale = OrderingScale.Number;
    if (temporal) {
      scale = column.Kind == ColumnKind.Physical ? OrderingScale.ColumnTemporal : OrderingScale.StoredMicroseconds;
    }
    return new OrderedColumn(column.Sql, column, scale);
  }

  private static void _ensureSameScale(in OrderedColumn left, in OrderedColumn right) {
    if (left.Scale != right.Scale) {
      throw new NotSupportedException(
        $"A collective ordering comparison between {left.Column.PropName} and {right.Column.PropName} compares a " +
        "temporal stored in the document (a microsecond count) with one in a physical column (a timestamp). Compare " +
        "each against a value instead.");
    }
  }

  // Binds the value an ordered column is compared with (or coalesced to) and returns its placeholder.
  private static string _bindOrderedValue(
      in OrderedColumn column, Expression valueExpr, string suffix, string prefix, Dictionary<string, object?> parameters) {
    var value = _evaluateValue(valueExpr);
    var name = _uniqueName(parameters, $"{prefix}_{column.Column.PropName.ToLowerInvariant()}{suffix}");
    parameters[name] = column.Column.Physical is { } field
      ? _toColumnInstant(CollectivePhysicalColumns.ColumnValue(field, value))
      : _toStoredNumber(value);
    return "@" + name;
  }

  // Npgsql writes a DateTimeOffset to timestamptz only at offset zero. The instant is the same either way.
  private static object? _toColumnInstant(object? value) =>
    value is DateTimeOffset offset ? offset.ToUniversalTime() : value;

  // The number a document stores for <paramref name="value"/>: a temporal as its microsecond count, an enumeration as
  // its underlying number, an integer as a long (a ulong as a decimal, since no Postgres integer holds every one), and
  // a fractional number as itself.
  private static object? _toStoredNumber(object? value) => value switch {
    null => null,
    DateTimeOffset offset => CanonicalTemporalFormat.ToEpochMicroseconds(offset),
    DateTime instant => CanonicalTemporalFormat.ToEpochMicroseconds(instant),
    DateOnly day => CanonicalTemporalFormat.ToEpochMicroseconds(day),
    TimeOnly time => CanonicalTemporalFormat.ToMicrosecondsOfDay(time),
    TimeSpan duration => CanonicalTemporalFormat.ToMicroseconds(duration),
    ulong or Enum when Convert.ToDecimal(value, CultureInfo.InvariantCulture) > long.MaxValue =>
      Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    decimal or double or float => value,
    _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
  };

  private static bool _isTemporal(Type type) =>
    type == typeof(DateTimeOffset) || type == typeof(DateTime) || type == typeof(DateOnly)
    || type == typeof(TimeOnly) || type == typeof(TimeSpan);

  private static bool _isNumeric(Type type) =>
    type.IsEnum || Type.GetTypeCode(type) is TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16
      or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
      or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

  // Two conditions on one property name (an outer row and an EXISTS inner row, or a repeated comparison) must bind
  // two parameters: reusing the name let the second value overwrite the first.
  private static string _uniqueName(Dictionary<string, object?> parameters, string name) {
    var candidate = name;
    var n = 0;
    while (parameters.ContainsKey(candidate)) {
      n++;
      candidate = name + "_" + n.ToString(CultureInfo.InvariantCulture);
    }
    return candidate;
  }

  /// <summary>
  /// A member access the compiler recognized as a column: the SQL that reads it, the property name the
  /// bound parameter is named after, what the column is, and for a physical column its declaration. They travel
  /// together because a value is bound against all of them at once, and a comparison that split them would bind
  /// text at a uuid.
  /// </summary>
  private readonly record struct ResolvedColumn(
    string Sql, string PropName, ColumnKind Kind, PerspectivePhysicalField? Physical = null, Type? MemberType = null);

  /// <summary>What a resolved column is, because it decides how a value bound against it is typed.</summary>
  private enum ColumnKind {
    /// <summary>A jsonb <c>-&gt;&gt;</c> extraction. Text, so the value is compared as text.</summary>
    JsonText,

    /// <summary>The row's <c>id</c>, a real uuid column.</summary>
    Uuid,

    /// <summary>A <c>[PhysicalField]</c> column, so the value binds as the scalar the column stores.</summary>
    Physical,
  }

  // The value to bind against <paramref name="column"/>. A jsonb extraction is text and takes the text conversion
  // below. A physical column binds the scalar its column stores (an enum as its number). The id
  // column is a real uuid: Postgres refuses `uuid = text` outright (42883), so the guid goes through as itself
  // and the driver types the parameter. A guid arriving as text is parsed rather than passed along, because a
  // caller comparing an id to a string means the id.
  /// <summary>
  /// The column's SQL, reading an absent document key as the member's declared default so that equality and
  /// <c>IN</c> select the same rows the in-memory replay does (#1044). A document written before the member existed
  /// has no key for it: deserialization gives the replay the default, while the raw document gives SQL
  /// <c>NULL</c>, and <c>NULL &lt;&gt; 'x'</c>, <c>NULL = 'x'</c> and <c>NULL IN (…)</c> are all <c>NULL</c>, so
  /// every such row silently drops out of the cohort. Left alone where there is nothing to disagree about: a
  /// physical column is filled when it is added (#1021), the row id always exists, and a member with no declared
  /// default already reads as null in both places.
  /// </summary>
  private static string _withDeclaredDefault(
      in ResolvedColumn column, string prefix, Dictionary<string, object?> parameters) {
    if (column.Kind != ColumnKind.JsonText || column.Physical is not null
        || !PerspectiveMemberDefaultRegistry.TryResolve(typeof(TModel), column.PropName, out var declared)) {
      return column.Sql;
    }

    var paramName = _uniqueName(parameters, $"{prefix}_{column.PropName.ToLowerInvariant()}_else");
    parameters[paramName] = _bind(declared, column);
    return $"COALESCE({column.Sql}, @{paramName})";
  }

  private static object? _bind(object? value, in ResolvedColumn column) => column switch {
    { Physical: { } field } => CollectivePhysicalColumns.ColumnValue(field, value),
    { Kind: ColumnKind.Uuid } => value switch {
      null => null,
      Guid g => g,
      string text when Guid.TryParse(text, out var parsed) => parsed,
      _ => throw new NotSupportedException(
        $"A predicate on the row id compares it with '{value.GetType().Name}'. The id column is a uuid, so the " +
        "value has to be a Guid, or a string that parses as one."),
    },
    _ => _toJsonbText(value),
  };

  // The text a jsonb `->>` extraction yields for <paramref name="value"/>, so the bound parameter compares
  // equal to `data->>'X'` / `scope->>'X'`. Only enums need special handling: EF's ComplexProperty().ToJson()
  // stores a plain enum as its underlying NUMBER (the default mapping), so its `->>` text is e.g. "1" — while
  // `enumValue.ToString()` is the NAME ("Draft") and would never match. Strings, Guids (stored as a JSON
  // string → `->>` gives the guid text), and numeric/bool scalars already round-trip through ToString().
  // (A perspective that maps an enum to text via .HasConversion&lt;string&gt;() would need SpecKind = RawSql.)
  private static string? _toJsonbText(object? value) => value switch {
    null => null,
    Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
    _ => value.ToString(),
  };

  // <values>.Contains(row.Data.X) → row.data->>'X' IN (@p0, @p1, …).
  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Contains arrives in three shapes (the two-argument and three-argument static forms and the instance form) and only the three-argument form with a null comparer is translatable. The shapes are the overload set.")]
  private static void _compileContains(
      MethodCallExpression mc, Ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters,
      List<ReferencedJsonPath> refs) {
    Expression sourceExpr;
    Expression itemExpr;
    if (mc.Object is null && mc.Arguments.Count == 2) {           // Enumerable.Contains(source, item)
      sourceExpr = mc.Arguments[0];
      itemExpr = mc.Arguments[1];
    } else if (mc.Object is null && mc.Arguments.Count == 3) {
      // MemoryExtensions.Contains(ReadOnlySpan<T> span, T item, IEqualityComparer<T>? comparer) — the span
      // overload the C# binder resolves `array.Contains(x)` to for some element types (notably value-type
      // enums; reference types like string land on the 2-arg form). The source array reaches arg[0] via an
      // array→span op_Implicit that _evaluateValue already unwraps. Only the default (null) comparer preserves
      // plain equality semantics — a custom comparer would change matching and isn't translatable to SQL.
      if (mc.Arguments[2] is not (ConstantExpression { Value: null } or DefaultExpression)) {
        throw new NotSupportedException(
          "Contains with a custom IEqualityComparer is not supported in a collective scope filter — it can't be translated to SQL IN.");
      }
      sourceExpr = mc.Arguments[0];
      itemExpr = mc.Arguments[1];
    } else if (mc.Object is not null && mc.Arguments.Count == 1) { // list.Contains(item)
      sourceExpr = mc.Object;
      itemExpr = mc.Arguments[0];
    } else {
      var arg0 = mc.Arguments.Count > 0 ? mc.Arguments[0].Type.Name : "-";
      var arg1 = mc.Arguments.Count > 1 ? mc.Arguments[1].Type.Name : "-";
      throw new NotSupportedException(
        $"Unsupported Contains shape in collective scope filter. Method={mc.Method.DeclaringType?.Name}.{mc.Method.Name}, " +
        $"Object={(mc.Object is null ? "null" : mc.Object.Type.Name)}, Args={mc.Arguments.Count} [{arg0}, {arg1}].");
    }

    if (_tryColumn(itemExpr, ctx, refs) is not { } item) {
      throw new NotSupportedException(
        "Contains is only supported as <values>.Contains(row.Data.X / row.Scope.X) — the item must be a column field.");
    }

    var values = _evaluateValue(sourceExpr) as System.Collections.IEnumerable
      ?? throw new NotSupportedException("Contains source must evaluate to a captured collection of values.");

    var names = new List<string>();
    var i = 0;
    foreach (var v in values) {
      var name = _uniqueName(parameters, $"{prefix}_{item.PropName.ToLowerInvariant()}_{i}");
      parameters[name] = _bind(v, item);
      names.Add("@" + name);
      i++;
    }

    sql.Append(_withDeclaredDefault(item, prefix, parameters))
      .Append(" IN (").Append(names.Count == 0 ? "NULL" : string.Join(", ", names)).Append(')');
  }

  // q.Of<TOther>().Any(s => s.Id == r.Id && …) → EXISTS (SELECT 1 FROM <TOther table> s WHERE …).
  private static void _compileExists(
      MethodCallExpression anyCall, Ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters,
      List<ReferencedJsonPath> refs) {
    if (ctx.InnerParam is not null) {
      throw new NotSupportedException("Nested cross-perspective cohorts (EXISTS within EXISTS) are not supported.");
    }
    if (ctx.OuterTableName is null) {
      throw new NotSupportedException(
        "A cross-perspective cohort (q.Of<TOther>().Any(...)) needs the outer table name to qualify the correlation. " +
        "Pass outerTableName to Compile (the applier supplies it).");
    }

    if (anyCall.Arguments[0] is not MethodCallExpression { Method.IsGenericMethod: true } ofCall
        || ofCall.Method.Name != "Of") {
      throw new NotSupportedException(
        "The Any source must be query.Of<TOther>() — a sibling-perspective queryable from the ICollectiveQuery context.");
    }
    var otherModel = ofCall.Method.GetGenericArguments()[0];
    var queryInstance = _evaluateValue(ofCall.Object
        ?? throw new NotSupportedException("query.Of<TOther>() must be called on an ICollectiveQuery instance."));
    // Driver-neutral table resolution: the query binding (EF/Dapper) implements ICollectiveSiblingTableSource,
    // read straight off the q.Of<TOther>() node — no driver-specific cast in the shared compiler.
    if (queryInstance is not ICollectiveSiblingTableSource tableSource) {
      throw new NotSupportedException(
        $"The cross-perspective query context must implement {nameof(ICollectiveSiblingTableSource)} so the " +
        "compiler can resolve the sibling table for the EXISTS correlation.");
    }
    var innerTable = tableSource.TableFor(otherModel);

    var predicate = anyCall.Arguments[1];
    while (predicate is UnaryExpression { NodeType: ExpressionType.Quote } quote) {
      predicate = quote.Operand;
    }
    if (predicate is not LambdaExpression lambda) {
      throw new NotSupportedException("The Any predicate must be a lambda.");
    }

    const char alias = 's';
    var innerCtx = ctx with {
      OuterQualifier = ctx.OuterTableName + ".",
      InnerParam = lambda.Parameters[0],
      InnerAlias = alias.ToString(),
      InnerTableName = innerTable,
    };

    sql.Append("EXISTS (SELECT 1 FROM ").Append(innerTable).Append(' ').Append(alias).Append(" WHERE ");
    _compilePredicate(lambda.Body, innerCtx, prefix, sql, parameters, refs);
    sql.Append(')');
  }

  // row.Scope.X → scope->>'X', row.Data.X → data->>'X' (or the column itself when X is a [PhysicalField]),
  // row.Id → id — qualified per context (outer/inner). When the match is a jsonb path, also records the UNqualified
  // path against its table in <paramref name="refs"/> as an expression-index candidate (§7); a physical column is
  // indexed by its own declaration, so it is not recorded.
  private static ResolvedColumn? _tryColumn(Expression e, Ctx ctx, List<ReferencedJsonPath> refs, bool numeric = false) {
    while (e is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
      e = convert.Operand;
    }

    if (e is MemberExpression { Member: PropertyInfo dprop, Expression: MemberExpression { Member.Name: "Data", Expression: ParameterExpression dp } data }
        && _qualifierFor(dp, ctx) is { } dq
        && CollectivePhysicalColumns.Resolve(data.Type, dprop.Name) is { } field) {
      CollectivePhysicalColumns.EnsureComparable(data.Type, field);
      return new ResolvedColumn(
        dq + CollectivePhysicalColumns.Quote(field.ColumnName), dprop.Name, ColumnKind.Physical, field, dprop.PropertyType);
    }

    if (e is MemberExpression { Member: PropertyInfo jprop, Expression: MemberExpression { Member.Name: var container, Expression: ParameterExpression jp } }
        && _qualifierFor(jp, ctx) is { } jq
        && _jsonbColumnFor(container) is { } col) {
      return _jsonColumn(jprop, jp, jq, col, ctx, refs, numeric);
    }

    if (e is MemberExpression { Member: PropertyInfo { Name: "Id" }, Expression: ParameterExpression ip }
        && _qualifierFor(ip, ctx) is { } iq) {
      return new ResolvedColumn($"{iq}id", "id", ColumnKind.Uuid);
    }

    return null;
  }

  // A jsonb key of a row's Scope or Data, qualified for its context, recorded as an index candidate on its table.
  private static ResolvedColumn _jsonColumn(
      PropertyInfo jprop, ParameterExpression jp, string jq, string col, Ctx ctx, List<ReferencedJsonPath> refs, bool numeric) {
    // The jsonb KEY is the serialized name, which honors [JsonPropertyName] — e.g. PerspectiveScope.TenantId
    // is [JsonPropertyName("t")], so it persists as scope->>'t', NOT scope->>'TenantId'. Emit the short key
    // (matches EF's own translation for the native path). The PARAMETER name stays the property name.
    var jsonKey = jprop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? jprop.Name;
    // An ordering comparison reads the key as a number, and it is that expression an index would have to carry.
    var unqualified = numeric ? $"({col}->>'{jsonKey}')::numeric" : $"{col}->>'{jsonKey}'";
    var columnSql = numeric ? $"({jq}{col}->>'{jsonKey}')::numeric" : $"{jq}{unqualified}";
    // Attribute the path to its table (outer vs. EXISTS-inner) so the index lands on the right relation.
    // Null table (Compile called without an outer table name) → skip: can't build the DDL, so no candidate.
    if (_tableOf(jp, ctx) is { } table) {
      refs.Add(new ReferencedJsonPath(table, unqualified));
    }
    return new ResolvedColumn(columnSql, jprop.Name, ColumnKind.JsonText, MemberType: jprop.PropertyType);
  }

  // The table a row param reads, or null when its context has no table name.
  private static string? _tableOf(ParameterExpression p, Ctx ctx) {
    if (ReferenceEquals(p, ctx.OuterParam)) {
      return ctx.OuterTableName;
    }
    return ctx.InnerParam is not null && ReferenceEquals(p, ctx.InnerParam) ? ctx.InnerTableName : null;
  }

  // The SQL qualifier ("" / "{outerTable}." / "{alias}.") for a row param, or null if it isn't a known one.
  private static string? _qualifierFor(ParameterExpression p, Ctx ctx) {
    if (ReferenceEquals(p, ctx.OuterParam)) {
      return ctx.OuterQualifier;
    }
    if (ctx.InnerParam is not null && ReferenceEquals(p, ctx.InnerParam)) {
      return ctx.InnerAlias + ".";
    }
    return null;
  }

  private static string? _jsonbColumnFor(string container) => container switch {
    "Scope" => "scope",
    "Data" => "data",
    _ => null,
  };

  // Resolve a captured value (literal, captured local/field, or a member chain ending at one) by reading
  // members directly rather than IL-compiling + invoking a lambda — compiling a fresh lambda over a value
  // captured in an async test method is fragile (InvalidProgramException / reflection-invoke NotSupported).
  private static object? _evaluateValue(Expression valueExpr) {
    // Strip framework conversions: Convert nodes (boxing/reference) and user-conversion operators —
    // notably the array → ReadOnlySpan op_Implicit the C# binder inserts when `.Contains` resolves to the
    // span-based MemoryExtensions overload. The underlying captured value is the operand.
    while (true) {
      if (valueExpr is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
        valueExpr = convert.Operand;
        continue;
      }
      if (valueExpr is MethodCallExpression { Method.Name: "op_Implicit" or "op_Explicit", Arguments: [var operand] }) {
        valueExpr = operand;
        continue;
      }
      break;
    }
    if (valueExpr is ConstantExpression c) {
      return c.Value;
    }
    if (valueExpr is MemberExpression m) {
      return _readMember(m);
    }
    if (valueExpr is BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce) {
      return _evaluateValue(coalesce.Left) ?? _evaluateValue(coalesce.Right);
    }

    throw new NotSupportedException(
      $"CollectivePredicateSqlCompiler cannot resolve a value of node kind {valueExpr.NodeType} " +
      "(supported: constant literal, captured local/field).");
  }

  private static object? _readMember(MemberExpression m) {
    var instance = m.Expression is null ? null : _evaluateValue(m.Expression);
    if (m.Member is FieldInfo f) {
      return f.GetValue(instance);
    }
    // Not a field, so it is a property: the expression factory validates that a member access is
    // built over a FieldInfo or a PropertyInfo and rejects anything else, so the cast cannot fail.
    // Writing it as a cast rather than a third switch arm keeps the compiler from demanding a
    // fallback arm no caller can reach.
    return ((PropertyInfo)m.Member).GetValue(instance);
  }
}
