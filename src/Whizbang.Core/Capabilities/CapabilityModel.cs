// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Capabilities;

/// <summary>
/// Identifies one external dependency the library can use: the provider that knows how to probe it,
/// and its name within that provider, for example <c>postgres/pg_squeeze</c>.
/// </summary>
/// <param name="Provider">The provider's name, for example <c>postgres</c>.</param>
/// <param name="Name">The capability's name within the provider, for example <c>pg_squeeze</c>.</param>
/// <docs>operations/infrastructure/capabilities</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityDescriptorTests.cs</tests>
public readonly record struct CapabilityId(string Provider, string Name) {
  /// <summary><c>provider/name</c>.</summary>
  /// <returns>The identity as it appears in the verdict and in configuration.</returns>
  public override string ToString() => $"{Provider}/{Name}";
}

/// <summary>
/// Whether a capability can be used in one scope, in the same words for every provider.
/// </summary>
/// <remarks>
/// <see cref="Ready"/> and <see cref="Provisionable"/> are usable. Every other state names the one
/// thing in the way, and the descriptor gives its fix (<see cref="CapabilityDescriptor.Remedy"/>). A
/// provider keeps its own, finer state in <see cref="CapabilityStatus.Detail"/>.
/// </remarks>
/// <docs>operations/infrastructure/capabilities#states</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityDescriptorTests.cs</tests>
public enum CapabilityState {
  /// <summary>Could not be determined: the probe failed, is turned off, or has not run yet.</summary>
  Unknown = 0,

  /// <summary>Installed or enabled, and usable.</summary>
  Ready,

  /// <summary>Absent, and the library may provision it.</summary>
  Provisionable,

  /// <summary>The platform blocks it, for example an extension a managed server does not allowlist.</summary>
  NotPermitted,

  /// <summary>Needs operator action at the server: a preload, a restart, a plugin to enable.</summary>
  NeedsServerChange,

  /// <summary>The identity the library uses cannot provision it.</summary>
  NoPrivilege,

  /// <summary>Not available on this server at all.</summary>
  Missing,
}

/// <summary>
/// What a provider knows about one capability before probing anything: what it is for, which features
/// use it, whether the library may provision it, what the server needs, and the fix for each problem state.
/// </summary>
/// <param name="Id">The capability.</param>
/// <param name="Purpose">What it enables, in a phrase, for the verdict.</param>
/// <param name="Features">The features that use it.</param>
/// <param name="Provisionable">Whether the library may provision it (install, enable, upgrade).</param>
/// <param name="Prerequisites">What the server needs before it can be used, one phrase each.</param>
/// <param name="Remedies">The operator's fix per problem state; a state without one gets a generic fix.</param>
/// <docs>operations/infrastructure/capabilities#descriptor</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityDescriptorTests.cs</tests>
public sealed record CapabilityDescriptor(
  CapabilityId Id,
  string Purpose,
  IReadOnlyList<string> Features,
  bool Provisionable,
  IReadOnlyList<string> Prerequisites,
  IReadOnlyDictionary<CapabilityState, string> Remedies) {
  /// <summary>
  /// The operator's fix per provider detail (<see cref="CapabilityStatus.Detail"/>), used before the state's own fix.
  /// For details that share one generic state but need different fixes: Postgres reads both a missing preload and a
  /// WAL level that is not logical as <see cref="CapabilityState.NeedsServerChange"/>. Empty by default.
  /// </summary>
  public IReadOnlyDictionary<string, string> DetailRemedies { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

  /// <summary>What an operator does about this capability in <paramref name="state"/>, given the provider's detail.</summary>
  /// <param name="state">Its state in some scope.</param>
  /// <param name="detail">The provider's finer state, or null.</param>
  /// <returns>
  /// <c>none</c> for a usable state; otherwise the fix for <paramref name="detail"/> when the provider gave one, else
  /// <see cref="Remedy(CapabilityState)"/>.
  /// </returns>
  public string Remedy(CapabilityState state, string? detail) =>
    detail is not null && state is not (CapabilityState.Ready or CapabilityState.Provisionable)
      && DetailRemedies.TryGetValue(detail, out var fix)
      ? fix
      : Remedy(state);

  /// <summary>What an operator does about this capability in <paramref name="state"/>.</summary>
  /// <param name="state">Its state in some scope.</param>
  /// <returns><c>none</c> for a usable state; otherwise the provider's fix, or a generic one.</returns>
  public string Remedy(CapabilityState state) {
    if (state is CapabilityState.Ready or CapabilityState.Provisionable) {
      return "none";
    }
    return Remedies.TryGetValue(state, out var remedy) ? remedy : state switch {
      CapabilityState.NotPermitted => $"ask the platform to permit {Id}",
      CapabilityState.NeedsServerChange => $"make the server-level change {Id} needs, then restart the server",
      CapabilityState.NoPrivilege => $"provision {Id} once with an identity allowed to, or grant this identity that right",
      CapabilityState.Missing => $"install {Id} on the server",
      _ => $"the probe could not decide; check that {Id.Provider} is reachable from this service",
    };
  }
}

/// <summary>One capability's state in one scope, as a probe read it.</summary>
/// <param name="Descriptor">What the capability is.</param>
/// <param name="Scope">Where it was probed: a database, a broker virtual host, a namespace.</param>
/// <param name="State">The generic state.</param>
/// <param name="Version">The installed or enabled version, when the provider knows it.</param>
/// <param name="Detail">The provider's own, finer state (for Postgres, <c>NotAllowlisted</c>), or null.</param>
/// <param name="Reason">Why the state is Unknown (the probe failure), or null.</param>
/// <docs>operations/infrastructure/capabilities#states</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityDescriptorTests.cs</tests>
public sealed record CapabilityStatus(
  CapabilityDescriptor Descriptor,
  string Scope,
  CapabilityState State,
  string? Version,
  string? Detail,
  string? Reason) {
  /// <summary>
  /// The version the provider would install, or upgrade an installed one to, when it knows it (for Postgres, the
  /// extension's default version). An upgrade is due when it is newer than <see cref="Version"/>
  /// (<see cref="CapabilityVersion.IsNewer"/>).
  /// </summary>
  public string? AvailableVersion { get; init; }

  /// <summary>
  /// What provisioning did on this start: created, found already present, or upgraded. Null when provisioning did
  /// not consider the capability, because nothing requires it or it is not Ready.
  /// </summary>
  public CapabilityProvisioningOutcome? Provisioning { get; init; }

  /// <summary>Whether the capability can be used: <see cref="CapabilityState.Ready"/> or <see cref="CapabilityState.Provisionable"/>.</summary>
  public bool IsUsable => State is CapabilityState.Ready or CapabilityState.Provisionable;

  /// <summary>Every descriptor <see cref="CapabilityState.Unknown"/> in <paramref name="scope"/>, with the reason.</summary>
  /// <param name="descriptors">The capabilities.</param>
  /// <param name="scope">The scope.</param>
  /// <param name="reason">Why nothing is known.</param>
  /// <returns>One status per descriptor.</returns>
  public static IReadOnlyList<CapabilityStatus> Unknown(
      IEnumerable<CapabilityDescriptor> descriptors, string scope, string reason) {
    ArgumentNullException.ThrowIfNull(descriptors);
    return [.. descriptors.Select(d => new CapabilityStatus(d, scope, CapabilityState.Unknown, null, null, reason))];
  }
}

/// <summary>
/// A source of capabilities: Postgres extensions, broker plugins, a cloud service's features. A provider
/// describes what it knows and probes one scope at a time; everything else (aggregation, the verdict,
/// the gauge, configuration) is the registry's and the same for every provider.
/// </summary>
/// <remarks>
/// Register an implementation as an <see cref="ICapabilityProvider"/> singleton and call
/// <c>AddWhizbangCapabilities()</c>; the registry picks up every provider. A probe that throws is read as
/// Unknown for its scope, with the exception's message as the reason, so a provider never stops startup.
/// </remarks>
/// <docs>operations/infrastructure/capabilities#adding-a-capability-provider</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityRegistryTests.cs</tests>
public interface ICapabilityProvider {
  /// <summary>The provider's name, the first half of every <see cref="CapabilityId"/> it describes.</summary>
  string Name { get; }

  /// <summary>Every capability this provider knows.</summary>
  IReadOnlyList<CapabilityDescriptor> Descriptors { get; }

  /// <summary>The scopes it probes, for example the databases its driver registered.</summary>
  IReadOnlyList<string> Scopes { get; }

  /// <summary>Probes one scope.</summary>
  /// <param name="scope">One of <see cref="Scopes"/>.</param>
  /// <param name="cancellationToken">The caller's cancellation.</param>
  /// <returns>One status per descriptor.</returns>
  Task<IReadOnlyList<CapabilityStatus>> ProbeAsync(string scope, CancellationToken cancellationToken);
}
