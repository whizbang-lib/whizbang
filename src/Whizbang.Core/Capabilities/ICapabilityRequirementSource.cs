// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Configuration;

namespace Whizbang.Core.Capabilities;

/// <summary>
/// A source of declared capability requirements. The capability verdict reads every registered source: the
/// configuration manifests the generator emits are the first, and a source that finds requirements at run time
/// (a schema that declares vector columns, say) registers alongside it.
/// </summary>
/// <remarks>
/// Register an implementation as an <see cref="ICapabilityRequirementSource"/> singleton (with
/// <c>TryAddEnumerable</c>); the <c>Capabilities</c> startup step reads every one after the probe.
/// </remarks>
/// <docs>operations/infrastructure/capabilities#requirement-sources</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/ConfigurationManifestRequirementSourceTests.cs</tests>
public interface ICapabilityRequirementSource {
  /// <summary>The requirements this source declares.</summary>
  IReadOnlyList<CapabilityRequirement> Requirements { get; }
}

/// <summary>
/// The requirements every loaded assembly's generated configuration manifest declares, from
/// <see cref="ConfigurationManifestRegistry"/>.
/// </summary>
/// <docs>operations/infrastructure/capabilities#requirement-sources</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/ConfigurationManifestRequirementSourceTests.cs</tests>
internal sealed class ConfigurationManifestRequirementSource : ICapabilityRequirementSource {
  /// <inheritdoc />
  public IReadOnlyList<CapabilityRequirement> Requirements => ConfigurationManifestRegistry.Requirements;
}
