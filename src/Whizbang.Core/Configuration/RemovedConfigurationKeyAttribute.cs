// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Configuration;

/// <summary>
/// Declares a configuration key the library once read and no longer does, with the keys that now control what it
/// did. A deployment that still sets it is told, at startup, what to set instead.
/// </summary>
/// <remarks>
/// <para>
/// Read at compile time only. The configuration manifest generator records every declaration in the declaring
/// assembly's manifest (<see cref="ConfigurationManifest.RemovedKeys"/>), and the startup check of unknown keys reads
/// them from <see cref="ConfigurationManifestRegistry"/>. Nothing reads the attribute at run time, so a third-party
/// package that runs the generator declares its own removed keys the same way the library does.
/// </para>
/// <para>
/// Place it on the assembly, or on a type: by convention the options type of the replacement, so the declaration
/// sits beside the setting an operator moves to. A key removed with nothing in its place takes no replacement.
/// </para>
/// </remarks>
/// <param name="key">The removed key, in full, for example <c>Whizbang:WorkCoordinator:MaintenanceInterval</c>.</param>
/// <param name="replacedBy">The keys that now control what it did, in the order the warning names them.</param>
/// <docs>operations/configuration/configuration-reference#removed-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/RemovedConfigurationKeyAttributeTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/ConfigurationManifestRemovedKeyGeneratorTests.cs</tests>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RemovedConfigurationKeyAttribute(string key, params string[] replacedBy) : Attribute {
  /// <summary>The removed key.</summary>
  public string Key { get; } = key;

  /// <summary>The keys that now control what it did; empty when nothing replaces it.</summary>
  public IReadOnlyList<string> ReplacedBy { get; } = replacedBy;
}

/// <summary>A removed configuration key with the keys that replaced it, as a generated manifest records it.</summary>
/// <param name="Key">The removed key.</param>
/// <param name="ReplacedBy">The keys that now control what it did; empty when nothing replaces it.</param>
/// <docs>operations/configuration/configuration-reference#removed-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/ConfigurationManifestRegistryTests.cs</tests>
public sealed record RemovedConfigurationKeyEntry(string Key, IReadOnlyList<string> ReplacedBy);
