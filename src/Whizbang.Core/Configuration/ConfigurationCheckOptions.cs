// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Configuration;

/// <summary>
/// What startup does with a <c>Whizbang:*</c> configuration key that no part of Whizbang reads
/// (<c>Whizbang:Configuration:UnknownKeys</c>).
/// </summary>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs</tests>
public enum UnknownConfigurationKeyMode {
  /// <summary>One Warning per unknown key, naming the key and, for a removed key, its replacement. The default.</summary>
  Warn = 0,

  /// <summary>The same warnings, then startup fails with every unknown key named.</summary>
  Throw = 1,

  /// <summary>No check.</summary>
  Ignore = 2,
}

/// <summary>
/// Settings for the startup check of the configuration itself, bound from <c>Whizbang:Configuration</c>.
/// </summary>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs</tests>
public sealed class ConfigurationCheckOptions {
  /// <summary>
  /// What to do with a <c>Whizbang:*</c> key nothing reads (<c>Whizbang:Configuration:UnknownKeys</c>).
  /// Default <see cref="UnknownConfigurationKeyMode.Warn"/>.
  /// </summary>
  public UnknownConfigurationKeyMode UnknownKeys { get; set; } = UnknownConfigurationKeyMode.Warn;
}

/// <summary>
/// Binds <see cref="ConfigurationCheckOptions"/> from <c>Whizbang:Configuration</c>. The section is named
/// literally at the call so the configuration binder source generator compiles the bind to typed
/// assignments. A host without configuration keeps the code values.
/// </summary>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs</tests>
internal sealed class ConfigureConfigurationCheckOptions(IConfiguration? configuration) : IConfigureOptions<ConfigurationCheckOptions> {
  public void Configure(ConfigurationCheckOptions options) {
    _ = configuration;
  }
}
