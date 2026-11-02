// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The event types whose rows keep their full metadata because they carry
/// <c>[StoredMetadata(StoredMetadataForm.Full)]</c>, directly, through a base class or through an interface. The
/// generator registers each at module load, by name, so the framework reads no attribute by reflection (#1725).
/// </summary>
/// <remarks>
/// Names are what the write paths match (<c>metadata_full_for</c>), so a type is registered by its CLR name: namespace
/// and type, nested types joined with <c>+</c>, no assembly. A private nested type is registered as well as any other.
/// </remarks>
/// <docs>data/event-store#keeping-an-event-type-full</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StoredMetadataRegistryTests.cs</tests>
public static class StoredMetadataRegistry {
  private static readonly ConcurrentDictionary<string, byte> _fullTypeNames = new(StringComparer.Ordinal);

  /// <summary>Records that rows of the type named <paramref name="typeName"/> keep their full metadata.</summary>
  /// <param name="typeName">The type's CLR name, without its assembly.</param>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StoredMetadataRegistryTests.cs:RegisterFull_ThenFullTypeNames_ListsItOnceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StoredMetadataRegistryTests.cs:RegisterFull_ABlankName_ThrowsAsync</tests>
  public static void RegisterFull(string typeName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
    _fullTypeNames.TryAdd(typeName, 0);
  }

  /// <summary>Every registered name, sorted ordinally: the attribute half of the list the write paths match.</summary>
  /// <returns>The names, each once.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StoredMetadataRegistryTests.cs:RegisterFull_ThenFullTypeNames_ListsItOnceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StoredMetadataRegistryTests.cs:TheGeneratedRegistration_ListsTheAttributedTestTypesAsync</tests>
  public static IReadOnlyList<string> FullTypeNames() => [.. _fullTypeNames.Keys.Order(StringComparer.Ordinal)];
}
