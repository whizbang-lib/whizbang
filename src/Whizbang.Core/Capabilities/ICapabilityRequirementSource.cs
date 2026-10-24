// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Capabilities;

/// <summary>
/// A source of capability requirements in force. The capability verdict reads every registered source: the
/// configuration manifests the generator emits are the first, judged against configuration; a schema's
/// requirements (pgvector for a vector column, pg_trgm for a trigram index) are in force whatever configuration
/// says, and register as a <see cref="FixedCapabilityRequirementSource"/>.
/// </summary>
/// <remarks>
/// Register an implementation as an <see cref="ICapabilityRequirementSource"/> singleton (with
/// <c>TryAddEnumerable</c>); the <c>Capabilities</c> startup step reads every one after the probe.
/// </remarks>
/// <docs>operations/infrastructure/capabilities#requirement-sources</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/ConfigurationManifestRequirementSourceTests.cs</tests>
public interface ICapabilityRequirementSource {
  /// <summary>The requirements this source puts in force, and how each was turned on.</summary>
  /// <param name="configuration">The host's configuration, or null when it has none.</param>
  /// <returns>The requirements in force.</returns>
  IReadOnlyList<ActiveCapabilityRequirement> Resolve(IConfiguration? configuration);
}

/// <summary>
/// The requirements every loaded assembly's generated configuration manifest declares, from
/// <see cref="ConfigurationManifestRegistry"/>, in force as configuration and each setting's declared default say
/// (<see cref="CapabilityActivationResolver"/>).
/// </summary>
/// <docs>operations/infrastructure/capabilities#requirement-sources</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/ConfigurationManifestRequirementSourceTests.cs</tests>
internal sealed class ConfigurationManifestRequirementSource : ICapabilityRequirementSource {
  /// <inheritdoc />
  public IReadOnlyList<ActiveCapabilityRequirement> Resolve(IConfiguration? configuration) =>
    CapabilityActivationResolver.Resolve(ConfigurationManifestRegistry.Requirements, configuration);
}

/// <summary>
/// Another source's requirements, each applied to one scope only: a context's schema requires its extensions on the
/// database that context uses, not on every database the host probes.
/// </summary>
/// <docs>operations/infrastructure/capabilities#requirement-sources</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/ScopedCapabilityRequirementSourceTests.cs</tests>
public sealed class ScopedCapabilityRequirementSource : ICapabilityRequirementSource {
  private readonly ICapabilityRequirementSource _inner;

  /// <summary>Scopes <paramref name="inner"/>'s requirements to <paramref name="scope"/>.</summary>
  /// <param name="inner">The source whose requirements are scoped.</param>
  /// <param name="scope">The scope, for example the database name a provider probes.</param>
  public ScopedCapabilityRequirementSource(ICapabilityRequirementSource inner, string scope) {
    ArgumentNullException.ThrowIfNull(inner);
    ArgumentNullException.ThrowIfNull(scope);
    _inner = inner;
    Scope = scope;
  }

  /// <summary>The scope every requirement applies to.</summary>
  public string Scope { get; }

  /// <inheritdoc />
  public IReadOnlyList<ActiveCapabilityRequirement> Resolve(IConfiguration? configuration) =>
    [.. _inner.Resolve(configuration).Select(requirement => requirement with { Scope = Scope })];
}

/// <summary>
/// Requirements in force whatever configuration says: what a schema declares. A vector column cannot exist without
/// pgvector, so its requirement is explicit; a trigram index the schema pass skips when pg_trgm is refused is
/// implicit.
/// </summary>
/// <docs>operations/infrastructure/capabilities#requirement-sources</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/FixedCapabilityRequirementSourceTests.cs</tests>
public sealed class FixedCapabilityRequirementSource : ICapabilityRequirementSource {
  private readonly IReadOnlyList<ActiveCapabilityRequirement> _requirements;

  /// <summary>Creates the source over the requirements it puts in force.</summary>
  /// <param name="requirements">The requirements, each with how it is turned on.</param>
  public FixedCapabilityRequirementSource(IReadOnlyList<ActiveCapabilityRequirement> requirements) {
    ArgumentNullException.ThrowIfNull(requirements);
    _requirements = requirements;
  }

  /// <inheritdoc />
  public IReadOnlyList<ActiveCapabilityRequirement> Resolve(IConfiguration? configuration) => _requirements;
}
