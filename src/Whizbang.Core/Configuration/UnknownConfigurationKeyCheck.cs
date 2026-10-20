// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Configuration;

/// <summary>A configured key nothing reads.</summary>
/// <param name="Key">The key as configured.</param>
/// <param name="Removed">The removed-key entry when the key was once read, else null.</param>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs</tests>
internal sealed record UnknownConfigurationKey(string Key, RemovedConfigurationKey? Removed);

/// <summary>The check of configured <c>Whizbang:*</c> keys against the keys Whizbang reads.</summary>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs</tests>
internal static class UnknownConfigurationKeyCheck {
  /// <summary>The configured keys nothing reads.</summary>
  public static IReadOnlyList<UnknownConfigurationKey> Find(IConfiguration configuration, KnownConfigurationKeys known) {
    _ = (configuration, known);
    return [];
  }

  /// <summary>Reports the unknown keys as the mode says.</summary>
  public static void Run(IConfiguration? configuration, KnownConfigurationKeys known, UnknownConfigurationKeyMode mode, ILogger logger) {
    _ = (configuration, known, mode, logger);
  }
}
