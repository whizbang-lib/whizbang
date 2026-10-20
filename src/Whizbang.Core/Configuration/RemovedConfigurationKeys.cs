// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Configuration;

/// <summary>A configuration key Whizbang once read and no longer does, with the keys that replaced it.</summary>
/// <param name="Key">The removed key.</param>
/// <param name="ReplacedBy">The keys that now control what it did.</param>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs</tests>
internal sealed record RemovedConfigurationKey(string Key, IReadOnlyList<string> ReplacedBy);

/// <summary>
/// The removed keys the startup check names a replacement for. A key removed from the library is added
/// here in the same change, so a deployment that still sets it is told what to set instead.
/// </summary>
/// <remarks>
/// A test holds the table to the manifest: no removed key may be one the library reads, and every
/// replacement must be one it does.
/// </remarks>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs</tests>
internal static class RemovedConfigurationKeys {
  /// <summary>Every removed key, with its replacements.</summary>
  public static IReadOnlyList<RemovedConfigurationKey> All { get; } = [
    // Removed with the legacy publisher worker. Maintenance now runs on the idle-gated cadence: an
    // admission interval, a settled dwell, and a forced run once deferrals have lasted MaxDeferral.
    new("Whizbang:WorkCoordinator:MaintenanceInterval", [
      "Whizbang:Workers:Maintenance:IntervalMinutes",
      "Whizbang:Housekeeping:SettledCooldown",
      "Whizbang:Housekeeping:MaxDeferral",
    ]),
    // Renamed when extension detection became the generic capability pattern.
    new("Whizbang:PostgresExtensions:ProbeOnStartup", ["Whizbang:Capabilities:ProbeOnStartup"]),
  ];

  /// <summary>The removed-key entry for a configured key, ignoring case.</summary>
  /// <param name="key">The configured key.</param>
  /// <returns>The entry, or <see langword="null"/> when the key was never removed.</returns>
  public static RemovedConfigurationKey? Find(string key) =>
    All.FirstOrDefault(removed => string.Equals(removed.Key, key, StringComparison.OrdinalIgnoreCase));
}
