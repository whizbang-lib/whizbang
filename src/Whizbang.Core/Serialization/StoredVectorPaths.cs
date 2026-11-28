// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Whizbang.Core.Serialization;

/// <summary>
/// Each event type's vector properties, as paths of JSON names from the root of the event's payload: the properties
/// whose stored form may be packed binary32 (#2624). The generated message JSON context registers them from its
/// module initializer, from the same declarations it attaches <see cref="StoredVectorConverters"/> to, so every path
/// here is read by a converter that accepts the packed form.
/// </summary>
/// <remarks>
/// <para>
/// A path is a list of segments rather than a dotted string, because a JSON name may itself contain a dot. A vector
/// inside a collection element, behind a polymorphic member, or inside a type that refers back to itself has no path:
/// it stays a JSON array (WHIZ064).
/// </para>
/// <para>
/// Registration is by <see langword="typeof"/> from generated code, so no reflection is involved. Every context that
/// registers a type derives the same paths from the same declaration, so a later registration replaces an earlier one.
/// </para>
/// </remarks>
/// <docs>data/event-store#packed-vectors</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/StoredVectorPathsTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Serialization/StoredVectorEventReadTests.cs</tests>
public static class StoredVectorPaths {
  private static readonly ConcurrentDictionary<Type, IReadOnlyList<IReadOnlyList<string>>> _byType = new();

  /// <summary>
  /// Registers the vector paths of <paramref name="eventType"/>, replacing any registered before. Called from
  /// generated module initializers.
  /// </summary>
  /// <param name="eventType">The event type.</param>
  /// <param name="paths">Its vector paths, each the JSON names from the payload's root to the vector.</param>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/StoredVectorPathsTests.cs:ARegisteredType_ListsItsPaths_InTheirOrderAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/StoredVectorPathsTests.cs:AnEmptyPathOrSegment_IsRefusedAsync</tests>
  public static void Register(Type eventType, params string[][] paths) {
    ArgumentNullException.ThrowIfNull(eventType);
    ArgumentNullException.ThrowIfNull(paths);
    if (paths.Length == 0) {
      throw new ArgumentException("An event type is registered only with at least one vector path.", nameof(paths));
    }
    var copied = new IReadOnlyList<string>[paths.Length];
    for (var i = 0; i < paths.Length; i++) {
      var path = paths[i] ?? throw new ArgumentNullException(nameof(paths), "A vector path is never null.");
      if (path.Length == 0 || Array.Exists(path, string.IsNullOrEmpty)) {
        throw new ArgumentException("A vector path names at least one segment, and every segment is a JSON name.", nameof(paths));
      }
      copied[i] = [.. path];
    }
    _byType[eventType] = copied;
  }

  /// <summary>The vector paths of <paramref name="eventType"/>, or none when it has no packable vector.</summary>
  /// <param name="eventType">The event type.</param>
  /// <returns>The paths, in the order the generated metadata lists the properties.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/StoredVectorPathsTests.cs:ATypeNeverRegistered_HasNoPathsAsync</tests>
  public static IReadOnlyList<IReadOnlyList<string>> For(Type eventType) {
    ArgumentNullException.ThrowIfNull(eventType);
    return _byType.TryGetValue(eventType, out var paths) ? paths : [];
  }

  /// <summary>Every event type registered with at least one vector path.</summary>
  /// <returns>The types, each once.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/StoredVectorEventReadTests.cs:EveryRegisteredTypeOfThisAssembly_HasASampleAsync</tests>
  public static IReadOnlyCollection<Type> EventTypes() => [.. _byType.Keys];
}
