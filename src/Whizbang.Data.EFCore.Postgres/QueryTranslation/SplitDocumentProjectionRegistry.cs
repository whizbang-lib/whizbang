// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Puts a <c>Split</c> model's promoted columns into its document when a query projects the document
/// (<c>Select(r =&gt; r.Data)</c>) rather than materializing the row. The generator registers, for each Split
/// model, the promoted columns in order and the copy that puts their values into the model.
/// </summary>
/// <remarks>
/// <para>
/// A whole-row read is hydrated by <see cref="SplitModeChangeTrackerHydrator"/>, which runs when the row
/// entity is tracked. A projected document never becomes a tracked entity, so it would arrive with every
/// promoted field empty, because the document of a Split model does not hold them (#2304).
/// <see cref="SplitDocumentProjectionRewriter"/> rewrites such a projection to read the columns beside the
/// document and pass both to <see cref="Hydrate{TModel}"/>, which EF Core calls on the client.
/// </para>
/// <para>
/// The copy follows the hydrators' rules: a record is copied with a <c>with</c> expression, a class with an
/// <c>init</c>-only promoted field is copied into a new instance, any other class is assigned in place, and a
/// null column of a nullable type keeps the value the document holds.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#reading-promoted-fields-back</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitModeProductionTests.cs</tests>
public static class SplitDocumentProjectionRegistry {
  private static readonly ConcurrentDictionary<Type, Registration> _registrations = new();

  /// <summary>One model's promoted columns, in the order its copy reads them, and the copy itself.</summary>
  /// <param name="Columns">The promoted columns' shadow property names, in order.</param>
  /// <param name="Copy">The copy, as a <c>Func&lt;TModel, object?[], TModel&gt;</c>.</param>
  public sealed record Registration(IReadOnlyList<string> Columns, Delegate Copy);

  /// <summary>Registers the promoted columns of <typeparamref name="TModel"/> and the copy that puts them into its document.</summary>
  /// <typeparam name="TModel">The Split perspective model.</typeparam>
  /// <param name="columns">The promoted columns' shadow property names, in the order <paramref name="copy"/> reads them.</param>
  /// <param name="copy">Returns the document with the column values put in; may return a new instance.</param>
  public static void Register<TModel>(IReadOnlyList<string> columns, Func<TModel, object?[], TModel> copy) where TModel : class {
    ArgumentNullException.ThrowIfNull(columns);
    ArgumentNullException.ThrowIfNull(copy);
    _registrations[typeof(TModel)] = new Registration(columns, copy);
  }

  /// <summary>The registration for <paramref name="modelType"/>, when it is a registered Split model.</summary>
  /// <param name="modelType">The perspective model type.</param>
  /// <param name="registration">The registration, when found.</param>
  /// <returns>True when the model is registered.</returns>
  public static bool TryGet(Type modelType, out Registration registration) =>
    _registrations.TryGetValue(modelType, out registration!);

  /// <summary>
  /// The document with its promoted columns put in. Called by EF Core on the client, in the final projection
  /// of a query rewritten by <see cref="SplitDocumentProjectionRewriter"/>.
  /// </summary>
  /// <typeparam name="TModel">The Split perspective model.</typeparam>
  /// <param name="data">The document as materialized from <c>data</c>.</param>
  /// <param name="columns">The promoted columns' values, in registration order.</param>
  /// <returns>The document with the columns in it.</returns>
  public static TModel Hydrate<TModel>(TModel data, object?[] columns) where TModel : class =>
    data is not null && _registrations.TryGetValue(typeof(TModel), out var registration)
      ? ((Func<TModel, object?[], TModel>)registration.Copy)(data, columns)
      : data!;
}
