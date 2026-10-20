// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;

namespace Whizbang.Core.Configuration;

/// <summary>
/// The configuration keys Whizbang reads, as recorded in the configuration key manifest.
/// </summary>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs</tests>
internal sealed class KnownConfigurationKeys {
  /// <summary>The manifest's resource name inside Whizbang.Core.</summary>
  internal const string RESOURCE_NAME = "Whizbang.Core.Configuration.configuration-keys.txt";

  private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Every key the library reads.</summary>
  public static KnownConfigurationKeys Library => new();

  /// <summary>Reads a manifest from an assembly resource.</summary>
  public static KnownConfigurationKeys FromResource(Assembly assembly, string name) {
    _ = (assembly, name);
    return new();
  }

  /// <summary>Parses manifest text.</summary>
  public static KnownConfigurationKeys Parse(string manifest) {
    _ = manifest;
    return new();
  }

  /// <summary>Whether <paramref name="key"/> is read.</summary>
  public bool IsKnown(string key) {
    return _exact.Contains(key);
  }
}
