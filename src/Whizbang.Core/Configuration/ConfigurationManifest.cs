// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Capabilities;

namespace Whizbang.Core.Configuration;

/// <summary>
/// What one assembly reads from configuration, as its generated manifest records it: every section it binds from
/// a literal key, the keys under each, the capabilities its settings require, and the keys it declares removed.
/// </summary>
/// <param name="Assembly">The assembly's name.</param>
/// <param name="Bindings">One entry per section and options type the assembly binds, ordered by section.</param>
/// <docs>operations/infrastructure/capabilities#declaring-a-requirement</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/ConfigurationManifestRegistryTests.cs</tests>
public sealed record ConfigurationManifest(string Assembly, IReadOnlyList<ConfigurationBinding> Bindings) {
  /// <summary>
  /// The keys the assembly declares removed with <see cref="RemovedConfigurationKeyAttribute"/>, ordered by key; empty
  /// when it declares none.
  /// </summary>
  public IReadOnlyList<RemovedConfigurationKeyEntry> RemovedKeys { get; init; } = [];
}

/// <summary>One section bound to one options type, the keys the binder reads under it, and what they require.</summary>
/// <param name="Section">The section's full key, for example <c>Whizbang:Workers:Maintenance</c>.</param>
/// <param name="OptionsType">The type the section is bound to.</param>
/// <param name="Keys">
/// Every key the binder reads under the section, in declaration order. A <c>*</c> segment is a name the consumer
/// chooses (a dictionary entry or a list item).
/// </param>
/// <param name="Requirements">One requirement per attributed property or enum member reached from the options type.</param>
/// <docs>operations/infrastructure/capabilities#declaring-a-requirement</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/ConfigurationManifestRegistryTests.cs</tests>
public sealed record ConfigurationBinding(
  string Section,
  Type OptionsType,
  IReadOnlyList<string> Keys,
  IReadOnlyList<CapabilityRequirement> Requirements);
