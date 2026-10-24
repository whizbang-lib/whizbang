// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Capabilities;

/// <summary>How a requirement in force was turned on.</summary>
/// <docs>operations/infrastructure/capabilities#activation</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityActivationResolverTests.cs</tests>
public enum CapabilityActivation {
  /// <summary>The operator set the key: a capability it lacks fails start.</summary>
  Explicit,

  /// <summary>The key is absent and the setting's declared default turns the feature on: a capability it lacks degrades.</summary>
  Implicit,
}

/// <summary>A requirement in force: the setting that turned its feature on, its value, and how.</summary>
/// <param name="Requirement">The declared requirement.</param>
/// <param name="Key">The setting's full key; for a declared key with a <c>*</c> segment, the configured entry's key.</param>
/// <param name="Value">The value in force (configured or default), or null for a section.</param>
/// <param name="Activation">Whether the operator set it or a default did.</param>
/// <docs>operations/infrastructure/capabilities#activation</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityActivationResolverTests.cs</tests>
public sealed record ActiveCapabilityRequirement(
  CapabilityRequirement Requirement,
  string Key,
  string? Value,
  CapabilityActivation Activation) {
  /// <summary><c>Feature (Key = Value, explicit)</c>, with <c>default</c> for an implicit one and no value for a section.</summary>
  /// <returns>The requirement as the verdict line names it.</returns>
  public override string ToString() {
    var how = Activation == CapabilityActivation.Explicit ? "explicit" : "default";
    return Value is null
      ? $"{Requirement.Feature} ({Key}, {how})"
      : $"{Requirement.Feature} ({Key} = {Value}, {how})";
  }
}

/// <summary>
/// Decides which declared requirements are in force, from configuration and each setting's declared default.
/// </summary>
/// <remarks>
/// <para>
/// A requirement is <b>explicit</b> when its key is set in configuration to a value that turns the feature on: the
/// declared value (an enum member's name, or <c>true</c>), compared without regard to case, or, for a requirement
/// with no declared value, any non-empty value or any key beneath it. A key with a <c>*</c> segment is matched
/// against every configured entry, each in force on its own.
/// </para>
/// <para>
/// When the key is absent, the requirement is <b>implicit</b> if the declared default turns the feature on. A key
/// that is set to anything else turns the feature off, whatever the default. An entry under a <c>*</c> segment
/// exists only when configured, so it has no default. A value set only in code is not seen here.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/capabilities#activation</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityActivationResolverTests.cs</tests>
public static class CapabilityActivationResolver {
  private const string WILDCARD = "*";

  /// <summary>The requirements in force, in the order given.</summary>
  /// <param name="requirements">The declared requirements.</param>
  /// <param name="configuration">The host's configuration, or null when it has none (only defaults apply).</param>
  /// <returns>One entry per requirement in force; per configured entry for a wildcard key.</returns>
  public static IReadOnlyList<ActiveCapabilityRequirement> Resolve(
      IEnumerable<CapabilityRequirement> requirements, IConfiguration? configuration) {
    ArgumentNullException.ThrowIfNull(requirements);
    var active = new List<ActiveCapabilityRequirement>();
    foreach (var requirement in requirements) {
      List<IConfigurationSection> configured = configuration is null ? [] : _configured(requirement.Key, configuration);
      if (configured.Count > 0) {
        active.AddRange(configured
          .Where(section => _turnsOn(requirement, section.Value, present: true))
          .Select(section => new ActiveCapabilityRequirement(requirement, section.Path, section.Value, CapabilityActivation.Explicit)));
      } else if (!requirement.Key.Contains(WILDCARD, StringComparison.Ordinal)
          && _turnsOn(requirement, requirement.Default, present: requirement.Default is not null)) {
        active.Add(new ActiveCapabilityRequirement(requirement, requirement.Key, requirement.Default, CapabilityActivation.Implicit));
      }
    }
    return active;
  }

  /// <summary>Every configured section at the key, a <c>*</c> segment matching each child.</summary>
  private static List<IConfigurationSection> _configured(string key, IConfiguration configuration) {
    IEnumerable<IConfiguration> nodes = [configuration];
    foreach (var segment in key.Split(':')) {
      nodes = segment == WILDCARD
        ? nodes.SelectMany(node => node.GetChildren())
        : nodes.Select(node => (IConfiguration)node.GetSection(segment));
    }
    return [.. nodes.Cast<IConfigurationSection>().Where(section => section.Exists())];
  }

  private static bool _turnsOn(CapabilityRequirement requirement, string? value, bool present) =>
    requirement.Value is null
      ? present && value is not { Length: 0 }
      : string.Equals(value, requirement.Value, StringComparison.OrdinalIgnoreCase);
}
