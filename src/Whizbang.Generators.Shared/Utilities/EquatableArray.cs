// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// An array that compares by its elements, for the models an incremental generator caches.
/// </summary>
/// <remarks>
/// <para>
/// A generator's pipeline reuses a step's previous output, and skips everything downstream of it, only when the new
/// output equals the old one. A record compares member by member, but an array member compares by reference, so a
/// model holding one is unequal to its predecessor on every run even when nothing it describes changed, and the
/// generator regenerates its source on every edit in the consumer's IDE. Holding this instead keeps the model equal
/// while its contents are.
/// </para>
/// <para>
/// A class rather than a struct, so a member that may be absent stays a nullable reference exactly as the array it
/// replaces did. It converts implicitly from an array and is built by a collection expression, so the code that
/// fills a model does not change.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type, compared with its default equality.</typeparam>
/// <param name="items">The array to wrap, which the caller must not change afterwards.</param>
/// <docs>operations/performance/build-cost</docs>
/// <tests>tests/Whizbang.Generators.Tests/EquatableArrayTests.cs</tests>
[CollectionBuilder(typeof(EquatableArray), nameof(EquatableArray.Create))]
public sealed class EquatableArray<T>(T[] items) : IEquatable<EquatableArray<T>>, IReadOnlyList<T> {
  private readonly T[] _items = items;

  /// <summary>The number of elements, as an array names it.</summary>
  public int Length => _items.Length;

  /// <inheritdoc/>
  public int Count => _items.Length;

  /// <inheritdoc/>
  public T this[int index] => _items[index];

  /// <summary>Wraps an array, or passes null through as null.</summary>
  [return: NotNullIfNotNull(nameof(items))]
  public static implicit operator EquatableArray<T>?(T[]? items) => items is null ? null : new EquatableArray<T>(items);

  /// <inheritdoc/>
  public bool Equals(EquatableArray<T>? other) {
    if (other is null || other._items.Length != _items.Length) {
      return false;
    }

    var comparer = EqualityComparer<T>.Default;
    for (var i = 0; i < _items.Length; i++) {
      if (!comparer.Equals(_items[i], other._items[i])) {
        return false;
      }
    }

    return true;
  }

  /// <inheritdoc/>
  public override bool Equals(object? obj) => Equals(obj as EquatableArray<T>);

  /// <inheritdoc/>
  public override int GetHashCode() {
    var comparer = EqualityComparer<T>.Default;
    var hash = 17;
    foreach (var item in _items) {
      hash = unchecked((hash * 31) + (item is null ? 0 : comparer.GetHashCode(item)));
    }

    return hash;
  }

  /// <inheritdoc/>
  public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

  IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}

/// <summary>Builds an <see cref="EquatableArray{T}"/> from a collection expression.</summary>
/// <docs>operations/performance/build-cost</docs>
/// <tests>tests/Whizbang.Generators.Tests/EquatableArrayTests.cs</tests>
public static class EquatableArray {
  /// <summary>Copies <paramref name="items"/> into a new array that compares by its elements.</summary>
  public static EquatableArray<T> Create<T>(ReadOnlySpan<T> items) => new(items.ToArray());
}
