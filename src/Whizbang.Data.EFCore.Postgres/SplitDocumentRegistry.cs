// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Linq.Expressions;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The projected document of each Split model (#2304): what a query whose result is <c>Select(r =&gt; r.Data)</c> reads in
/// place of the document alone, so the promoted fields, which in Split mode live only in their columns, come back with
/// their column values as they do on a whole-row read.
/// </summary>
/// <remarks>
/// <para>
/// A whole <c>PerspectiveRow&lt;TModel&gt;</c> is hydrated by <see cref="SplitModeChangeTrackerHydrator"/> once EF Core has
/// materialized it. A query that projects the document materializes only the <c>Data</c> complex property, so no
/// hydrator runs and the promoted fields keep the document's empty values. Generated code registers, for each model with
/// a Split field, a projection that reads the document and every promoted column the copy fills
/// (<c>r =&gt; WithColumns(r.Data, new object?[] { EF.Property&lt;T1&gt;(r, "c1"), ... })</c>) and the copy that puts the
/// columns into the document, by the hydrators' copy rules. The query interceptor substitutes the projection when the
/// document projection is the query's last and only paging or an element operator follows it; EF Core reads the columns
/// and runs the copy on each result.
/// </para>
/// <para><strong>Zero reflection, AOT-safe</strong>: the projection is an expression the compiler built in generated code,
/// and the copy a compiled delegate; lookups are keyed by the model type.</para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#a-projected-document-carries-its-promoted-fields</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/InitOnlyPhysicalFieldHydrationTests.ProjectedDocument.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SplitDocumentRegistryTests.cs</tests>
public static class SplitDocumentRegistry {
  private static readonly ConcurrentDictionary<Type, (LambdaExpression Projection, Delegate Copy)> _registrations = new();

  /// <summary>Registers a Split model's projected document. Called by generated code at startup.</summary>
  /// <typeparam name="TModel">The perspective model.</typeparam>
  /// <param name="projection">The row's document read with its promoted columns, through <see cref="WithColumns{TModel}"/>.</param>
  /// <param name="withColumns">The copy of the columns, in the projection's order, into the document; returns the
  /// document to keep (a new instance for a model that is copied rather than assigned).</param>
  public static void Register<TModel>(
      Expression<Func<PerspectiveRow<TModel>, TModel>> projection, Func<TModel, object?[], TModel> withColumns)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(projection);
    ArgumentNullException.ThrowIfNull(withColumns);
    _registrations[typeof(TModel)] = (projection, withColumns);
  }

  /// <summary>
  /// The document with its promoted columns copied in: the call a registered projection makes, which EF Core runs on each
  /// result once it has read the document and the columns. A null document, or a model with no registration, is returned
  /// as it is.
  /// </summary>
  /// <typeparam name="TModel">The perspective model.</typeparam>
  /// <param name="data">The document as stored.</param>
  /// <param name="columns">The promoted columns, in the order the projection reads them.</param>
  /// <returns>The document with the columns in it.</returns>
  [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(data))]
  public static TModel? WithColumns<TModel>(TModel? data, object?[] columns) where TModel : class =>
    data is not null && _registrations.TryGetValue(typeof(TModel), out var registration)
      ? ((Func<TModel, object?[], TModel>)registration.Copy)(data, columns)
      : data;

  /// <summary>The registered projection of <paramref name="modelType"/>, if it is a Split model.</summary>
  /// <param name="modelType">The perspective model type.</param>
  /// <param name="projection">The projection, when registered.</param>
  /// <returns>Whether one is registered.</returns>
  internal static bool TryGetProjection(Type modelType, out LambdaExpression projection) {
    var found = _registrations.TryGetValue(modelType, out var registration);
    projection = registration.Projection;
    return found;
  }

  /// <summary>
  /// The document a generated copy fills: it starts from the stored document and ends holding the one to return, so the
  /// copy reads as the hydrators' do (<c>row.Data</c>).
  /// </summary>
  /// <typeparam name="TModel">The perspective model.</typeparam>
  /// <param name="data">The stored document.</param>
  public sealed class Document<TModel>(TModel data) where TModel : class {
    /// <summary>The document: the stored one, then the one with the columns in it.</summary>
    public TModel Data { get; set; } = data;
  }
}
