// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Rewrites a query whose result is the projected document of a Split model (#2304), <c>Select(r =&gt; r.Data)</c>, to the
/// model's registered projection (<see cref="SplitDocumentRegistry"/>), which reads the promoted columns beside the
/// document and copies them in. A Split document no longer holds its promoted fields, so without this they came back
/// empty; a whole-row read is hydrated by <see cref="SplitModeChangeTrackerHydrator"/> and needs nothing here.
/// </summary>
/// <remarks>
/// <para>
/// The registered projection ends in a method EF Core runs on each result once the query has read the document and the
/// columns, which it allows only in the query's last projection. So only that projection is rewritten, and only when
/// what follows it is paging or an element operator without a predicate (<c>Skip</c>, <c>Take</c>, <c>First</c>,
/// <c>Single</c>, <c>Last</c> and their <c>OrDefault</c> forms), all of which EF Core applies in the database before the
/// copy runs. Any other operator after the projection (a filter, a sort, another projection, an aggregate such as a
/// count) leaves the query exactly as it was.
/// </para>
/// <para>
/// The projection is recognized as the identity read of the document on a row: a one-parameter lambda whose body is
/// that parameter's <c>Data</c>, over the registered model's <c>PerspectiveRow</c>. A model with no registration (an
/// Extracted or JSON-only model, whose document holds the fields) is not rewritten.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#a-projected-document-carries-its-promoted-fields</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SplitDocumentRegistryTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/InitOnlyPhysicalFieldHydrationTests.ProjectedDocument.cs</tests>
internal static class SplitDocumentProjectionRewriter {
  /// <summary>The query with its last projection of a Split document rewritten, or the query as it was.</summary>
  /// <param name="query">The query expression EF Core is about to compile.</param>
  /// <returns>The rewritten query, or <paramref name="query"/> itself.</returns>
  internal static Expression Rewrite(Expression query) {
    if (query is not MethodCallExpression call || call.Method.DeclaringType != typeof(Queryable)) {
      return query;
    }
    if (_passesThrough(call)) {
      var source = Rewrite(call.Arguments[0]);
      return ReferenceEquals(source, call.Arguments[0]) ? call : call.Update(null, [source, .. call.Arguments.Skip(1)]);
    }
    return _projection(call) is { } projection ? call.Update(null, [call.Arguments[0], Expression.Quote(projection)]) : call;
  }

  // Paging, which EF Core applies in the database before the copy runs.
  private static readonly HashSet<string> _paging = new(StringComparer.Ordinal) { nameof(Queryable.Skip), nameof(Queryable.Take) };

  // The element operators, applied in the database too when they take no predicate.
  private static readonly HashSet<string> _elementOperators = new(StringComparer.Ordinal) {
    nameof(Queryable.First), nameof(Queryable.FirstOrDefault),
    nameof(Queryable.Single), nameof(Queryable.SingleOrDefault),
    nameof(Queryable.Last), nameof(Queryable.LastOrDefault),
  };

  // Paging and the element operators without a predicate pass through to the projection beneath them. Sets rather than
  // a switch on the name: a string switch compiles to a hash table whose "hash matched, name differs" outcomes no
  // method name can take, which left decisions the tests could never reach.
  private static bool _passesThrough(MethodCallExpression call) =>
    _paging.Contains(call.Method.Name) || (_elementOperators.Contains(call.Method.Name) && call.Arguments.Count == 1);

  // The registered projection of a Select that reads a row's document, r => r.Data, or null for any other call.
  private static LambdaExpression? _projection(MethodCallExpression call) =>
    call.Method.Name == nameof(Queryable.Select)
      && call.Arguments[1] is UnaryExpression { Operand: LambdaExpression { Parameters: [var row] } selector }
      && selector.Body is MemberExpression { Member.Name: "Data" } data
      && data.Expression == row
      && SplitDocumentRegistry.TryGetProjection(selector.ReturnType, out var projection)
      && projection.Parameters[0].Type == row.Type
        ? projection
        : null;
}
