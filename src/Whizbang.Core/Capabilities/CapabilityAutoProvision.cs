// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Capabilities;

/// <summary>Whether provisioning is on for one capability, and the key that decided it.</summary>
/// <param name="Enabled">Whether the library may create or upgrade the capability.</param>
/// <param name="Key">The most specific key that is set, or the global key when neither override is.</param>
/// <docs>operations/infrastructure/capabilities#provisioning</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityAutoProvisionTests.cs</tests>
public readonly record struct CapabilityAutoProvisionSetting(bool Enabled, string Key);

/// <summary>
/// The three-level provisioning switch: per capability
/// (<c>Whizbang:Capabilities:&lt;provider&gt;:&lt;name&gt;:AutoProvision</c>), then per provider
/// (<c>Whizbang:Capabilities:&lt;provider&gt;:AutoProvision</c>), then the global
/// <see cref="CapabilityOptions.AutoProvision"/> (<c>Whizbang:Capabilities:AutoProvision</c>). The most specific level
/// that is set wins.
/// </summary>
/// <remarks>
/// The two overrides are named by the consumer's providers and capabilities, so they are read by key when the switch
/// is asked, rather than bound. A value that is not <c>true</c> or <c>false</c> (compared without regard to case)
/// counts as not set, so the next level decides, as every hand-read Whizbang key treats a mistyped value.
/// </remarks>
/// <docs>operations/infrastructure/capabilities#provisioning</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityAutoProvisionTests.cs</tests>
public static class CapabilityAutoProvision {
#pragma warning disable CA1707
  /// <summary>The global key, bound to <see cref="CapabilityOptions.AutoProvision"/>.</summary>
  public const string GLOBAL_KEY = SECTION + ":" + SETTING;
#pragma warning restore CA1707

  private const string SECTION = "Whizbang:Capabilities";
  private const string SETTING = "AutoProvision";

  /// <summary>The per-provider key, for example <c>Whizbang:Capabilities:postgres:AutoProvision</c>.</summary>
  /// <param name="provider">The provider's name.</param>
  /// <returns>The key.</returns>
  public static string ProviderKey(string provider) => $"{SECTION}:{provider}:{SETTING}";

  /// <summary>The per-capability key, for example <c>Whizbang:Capabilities:postgres:pg_squeeze:AutoProvision</c>.</summary>
  /// <param name="id">The capability.</param>
  /// <returns>The key.</returns>
  public static string CapabilityKey(CapabilityId id) => $"{SECTION}:{id.Provider}:{id.Name}:{SETTING}";

  /// <summary>Whether provisioning is on for <paramref name="id"/>, and which key said so.</summary>
  /// <param name="id">The capability.</param>
  /// <param name="options">The bound options, which hold the global level.</param>
  /// <param name="configuration">The host's configuration, or null when it has none (only the global level applies).</param>
  /// <returns>The setting.</returns>
  public static CapabilityAutoProvisionSetting Resolve(CapabilityId id, CapabilityOptions options, IConfiguration? configuration) {
    ArgumentNullException.ThrowIfNull(options);
    if (configuration is not null) {
      foreach (var key in (string[])[CapabilityKey(id), ProviderKey(id.Provider)]) {
        if (bool.TryParse(configuration[key], out var enabled)) {
          return new CapabilityAutoProvisionSetting(enabled, key);
        }
      }
    }
    return new CapabilityAutoProvisionSetting(options.AutoProvision, GLOBAL_KEY);
  }
}
