// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Capabilities;

/// <summary>
/// Declares that a setting needs a capability: on an options property, the feature it turns on needs it whenever
/// the property is set; on an enum member, it needs it when the setting selects that member.
/// </summary>
/// <remarks>
/// Read at compile time only. The configuration manifest generator finds each options type bound from a literal
/// <c>GetSection("...")</c> key, and records one <see cref="CapabilityRequirement"/> per attribute under the full
/// key of the property, or of every property of that enum type. Nothing reads the attribute at run time.
/// </remarks>
/// <param name="provider">The provider's name, for example <c>postgres</c>.</param>
/// <param name="name">The capability's name within the provider.</param>
/// <docs>operations/infrastructure/capabilities#declaring-a-requirement</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/RequiresCapabilityAttributeTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/ConfigurationManifestGeneratorTests.cs</tests>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true, Inherited = true)]
public sealed class RequiresCapabilityAttribute(string provider, string name) : Attribute {
  /// <summary>The provider's name.</summary>
  public string Provider { get; } = provider;

  /// <summary>The capability's name within the provider.</summary>
  public string Name { get; } = name;

  /// <summary>The feature that needs it, as the verdict names it; the member's name when not set.</summary>
  public string? Feature { get; set; }
}

/// <summary>
/// Declares that a setting needs a Postgres extension: shorthand for
/// <see cref="RequiresCapabilityAttribute"/> with the <c>postgres</c> provider.
/// </summary>
/// <param name="extension">The extension's name, as <c>CREATE EXTENSION</c> takes it, for example <c>pg_squeeze</c>.</param>
/// <docs>operations/infrastructure/capabilities#declaring-a-requirement</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/RequiresCapabilityAttributeTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/ConfigurationManifestGeneratorTests.cs</tests>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true, Inherited = true)]
public sealed class RequiresPostgresExtensionAttribute(string extension) : Attribute {
#pragma warning disable CA1707
  /// <summary>The Postgres provider's name, the first half of every extension's <see cref="CapabilityId"/>.</summary>
  public const string PROVIDER = "postgres";
#pragma warning restore CA1707

  /// <summary>The extension's name.</summary>
  public string Extension { get; } = extension;

  /// <summary>The feature that needs it, as the verdict names it; the member's name when not set.</summary>
  public string? Feature { get; set; }
}
