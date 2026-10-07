// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Linq.Expressions;

namespace Whizbang.Transports.FastEndpoints;

/// <summary>
/// The pieces a REST lens endpoint shapes its query with: the parsed <c>sort</c> parameter, the
/// ordering it builds, and the typed values its <c>filter[...]</c> parameters are compared as.
/// </summary>
/// <remarks>
/// Generated endpoints call these with the model's own property selectors, which the generator
/// writes out per property at build time, so nothing here looks a member up by name and every
/// call is trimming- and AOT-safe. A value that cannot be read as its field's type, or a field the
/// endpoint does not offer, raises <see cref="InvalidLensRequestException"/>, which the endpoint
/// answers with 400 rather than returning results the caller did not ask for.
/// </remarks>
/// <docs>apis/rest/filtering</docs>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/LensQueryShapingTests.cs</tests>
public static class LensQueryShaping {
  /// <summary>
  /// Parses a sort parameter: comma-separated field names, each optionally prefixed with
  /// <c>-</c> for descending or <c>+</c> for ascending.
  /// </summary>
  /// <param name="sort">The sort parameter, for example <c>-createdAt,name</c>.</param>
  /// <returns>The sort fields in the order given; empty when there is no sort.</returns>
  public static IReadOnlyList<SortExpression> ParseSort(string? sort) {
    if (string.IsNullOrWhiteSpace(sort)) {
      return [];
    }

    var results = new List<SortExpression>();
    foreach (var entry in sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var descending = entry[0] == '-';
      var fieldName = entry[0] is '-' or '+' ? entry[1..].Trim() : entry;
      if (fieldName.Length > 0) {
        results.Add(new SortExpression(fieldName, descending));
      }
    }

    return results;
  }

  /// <summary>
  /// Orders by <paramref name="key"/>: the first key of the ordering when <paramref name="ordered"/>
  /// is null, and the next one after it otherwise.
  /// </summary>
  /// <typeparam name="TModel">The model the query returns.</typeparam>
  /// <typeparam name="TKey">The type of the key ordered by.</typeparam>
  /// <param name="query">The query being ordered.</param>
  /// <param name="ordered">The ordering so far, or null before the first key.</param>
  /// <param name="key">The property to order by.</param>
  /// <param name="descending">Whether this key orders descending.</param>
  /// <returns>The ordering with this key added.</returns>
  public static IOrderedQueryable<TModel> ThenOrderBy<TModel, TKey>(
      IQueryable<TModel> query,
      IOrderedQueryable<TModel>? ordered,
      Expression<Func<TModel, TKey>> key,
      bool descending) {
    ArgumentNullException.ThrowIfNull(query);
    ArgumentNullException.ThrowIfNull(key);

    return (ordered, descending) switch {
      (null, false) => query.OrderBy(key),
      (null, true) => query.OrderByDescending(key),
      ( { } then, false) => then.ThenBy(key),
      ( { } then, true) => then.ThenByDescending(key),
    };
  }

  /// <summary>
  /// Reads a filter value as the field's type, in the invariant culture.
  /// </summary>
  /// <typeparam name="T">The field's type, without any nullable wrapper.</typeparam>
  /// <param name="field">The field name, for the error.</param>
  /// <param name="value">The value from the request.</param>
  /// <returns>The typed value.</returns>
  /// <exception cref="InvalidLensRequestException">The value cannot be read as <typeparamref name="T"/>.</exception>
  public static T Parse<T>(string field, string? value) where T : IParsable<T> =>
    T.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
      ? parsed
      : throw InvalidLensRequestException.InvalidValue(field, value);

  /// <summary>
  /// Reads a filter value as one of an enumeration's defined members, by name (ignoring case) or by
  /// number.
  /// </summary>
  /// <typeparam name="TEnum">The field's enumeration type.</typeparam>
  /// <param name="field">The field name, for the error.</param>
  /// <param name="value">The value from the request.</param>
  /// <returns>The member.</returns>
  /// <exception cref="InvalidLensRequestException">The value names no defined member.</exception>
  public static TEnum ParseEnum<TEnum>(string field, string? value) where TEnum : struct, Enum =>
    Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
      ? parsed
      : throw InvalidLensRequestException.InvalidValue(field, value);
}
