// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Capabilities;

namespace Whizbang.Core.Configuration;

/// <summary>
/// Every loaded assembly's configuration manifest (bindings, requirements and removed keys), registered at module load by the code the configuration
/// manifest generator emits into each assembly that binds a section from a literal key.
/// </summary>
/// <remarks>
/// <para>
/// The capability verdict reads <see cref="Requirements"/> to name, for each capability, the features that need it
/// and the setting that turned each one on. Attributes are read by the generator at compile time, so nothing here
/// reflects over an options type: a third-party package that runs the generator declares its requirements the
/// same way the library does.
/// </para>
/// <para>
/// A manifest is registered once per assembly; registering the same assembly again replaces it.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/capabilities#declaring-a-requirement</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/ConfigurationManifestRegistryTests.cs</tests>
public static class ConfigurationManifestRegistry {
  private static readonly ConcurrentDictionary<string, ConfigurationManifest> _byAssembly = new(StringComparer.Ordinal);

  /// <summary>Records an assembly's manifest, replacing one it registered before.</summary>
  /// <param name="manifest">The manifest.</param>
  public static void Register(ConfigurationManifest manifest) {
    ArgumentNullException.ThrowIfNull(manifest);
    _byAssembly[manifest.Assembly] = manifest;
  }

  /// <summary>Every registered manifest, ordered by assembly name so the order is the same on every start.</summary>
  public static IReadOnlyList<ConfigurationManifest> Manifests =>
    [.. _byAssembly.Values.OrderBy(manifest => manifest.Assembly, StringComparer.Ordinal)];

  /// <summary>Every removed key across every manifest, in manifest order.</summary>
  /// <docs>operations/configuration/configuration-reference#removed-keys</docs>
  public static IReadOnlyList<RemovedConfigurationKeyEntry> RemovedKeys =>
    [.. Manifests.SelectMany(manifest => manifest.RemovedKeys)];

  /// <summary>
  /// The removed-key entry for a configured key, ignoring case as configuration does. When two assemblies declare the
  /// same key, the one first by assembly name answers, so the answer is the same on every start.
  /// </summary>
  /// <param name="key">The configured key.</param>
  /// <returns>The entry, or <see langword="null"/> when no loaded assembly declares the key removed.</returns>
  /// <docs>operations/configuration/configuration-reference#removed-keys</docs>
  public static RemovedConfigurationKeyEntry? FindRemovedKey(string key) {
    ArgumentNullException.ThrowIfNull(key);
    return RemovedKeys.FirstOrDefault(removed => string.Equals(removed.Key, key, StringComparison.OrdinalIgnoreCase));
  }

  /// <summary>Every binding's requirements across every manifest, each stated once, in manifest order.</summary>
  public static IReadOnlyList<CapabilityRequirement> Requirements =>
    [.. Manifests.SelectMany(manifest => manifest.Bindings).SelectMany(binding => binding.Requirements).Distinct()];
}
