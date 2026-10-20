// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Configuration;

/// <summary>A configuration key Whizbang once read and no longer does, with the keys that replaced it.</summary>
/// <param name="Key">The removed key.</param>
/// <param name="ReplacedBy">The keys that now control what it did.</param>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs</tests>
internal sealed record RemovedConfigurationKey(string Key, IReadOnlyList<string> ReplacedBy);

/// <summary>The removed keys startup names a replacement for.</summary>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs</tests>
internal static class RemovedConfigurationKeys {
  /// <summary>Every removed key.</summary>
  public static IReadOnlyList<RemovedConfigurationKey> All { get; } = [];

  /// <summary>The removed key, or null.</summary>
  public static RemovedConfigurationKey? Find(string key) {
    _ = key;
    return null;
  }
}
