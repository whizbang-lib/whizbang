// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Runtime registry of each perspective model's <c>[StreamId]</c> property, so a filter on it can also
/// be answered by the row's primary key. Source generators populate it at startup.
/// </summary>
/// <remarks>
/// <para>
/// A perspective row's <c>id</c> column is its stream id, and a model's <c>[StreamId]</c> property starts
/// out as the same value. A filter written against the property (<c>r.Data.OrderId == id</c>) would
/// otherwise search the JSON document; with the property registered, <see cref="StreamIdPrimaryKeyRewriter"/>
/// adds <c>r.Id == id</c> beside it so the planner can read the row by its primary key.
/// </para>
/// <para>
/// Only a <see cref="Guid"/> or nullable <see cref="Guid"/> property is registered: the row's id is a
/// <see cref="Guid"/>, and a string or strongly-typed key has no comparison the database could use.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#stream-id-filters</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/StreamIdPrimaryKeySqlTests.cs</tests>
public static class StreamIdRegistry {
  private static readonly ConcurrentDictionary<Type, string> _properties = new();

  /// <summary>Registers <paramref name="propertyName"/> as the stream-id property of <typeparamref name="TModel"/>.</summary>
  /// <typeparam name="TModel">The perspective model type.</typeparam>
  /// <param name="propertyName">The name of the model's <c>[StreamId]</c> property.</param>
  public static void Register<TModel>(string propertyName) => Register(typeof(TModel), propertyName);

  /// <summary>Registers <paramref name="propertyName"/> as the stream-id property of <paramref name="modelType"/>.</summary>
  /// <param name="modelType">The perspective model type.</param>
  /// <param name="propertyName">The name of the model's <c>[StreamId]</c> property.</param>
  public static void Register(Type modelType, string propertyName) {
    ArgumentNullException.ThrowIfNull(modelType);
    ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
    _properties[modelType] = propertyName;
  }

  /// <summary>Whether <paramref name="propertyName"/> is the registered stream-id property of <paramref name="modelType"/>.</summary>
  /// <param name="modelType">The perspective model type.</param>
  /// <param name="propertyName">The property being filtered on.</param>
  /// <returns>True when the property is the model's registered stream id.</returns>
  public static bool IsStreamIdProperty(Type modelType, string propertyName) =>
    _properties.TryGetValue(modelType, out var registered) && string.Equals(registered, propertyName, StringComparison.Ordinal);
}
