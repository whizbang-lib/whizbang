// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Capabilities;

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// A provider that is not Postgres: a message broker with plugins per virtual host. It proves the
/// registry, report and gauge know nothing about any one provider.
/// </summary>
internal sealed class FakeCapabilityProvider(string name, IReadOnlyList<string> scopes, params CapabilityDescriptor[] descriptors)
  : ICapabilityProvider {
  public string Name { get; } = name;
  public IReadOnlyList<CapabilityDescriptor> Descriptors { get; } = descriptors;
  public IReadOnlyList<string> Scopes { get; } = scopes;

  /// <summary>The state each capability reads, by name; anything absent reads Ready.</summary>
  public Dictionary<string, CapabilityState> States { get; } = new(StringComparer.Ordinal);

  /// <summary>When set, every probe throws this.</summary>
  public Exception? Failure { get; set; }

  public int Probes { get; private set; }

  public Task<IReadOnlyList<CapabilityStatus>> ProbeAsync(string scope, CancellationToken cancellationToken) {
    Probes++;
    if (Failure is not null) {
      throw Failure;
    }
    IReadOnlyList<CapabilityStatus> statuses = [.. Descriptors.Select(d => new CapabilityStatus(
      d, scope, States.GetValueOrDefault(d.Id.Name, CapabilityState.Ready), Version: "1.0", Detail: null, Reason: null))];
    return Task.FromResult(statuses);
  }

  public static CapabilityDescriptor Describe(string provider, string name, string purpose = "a purpose") =>
    new(new CapabilityId(provider, name), purpose, ["A feature"], Provisionable: true, ["enabled on the server"],
      new Dictionary<CapabilityState, string>());
}
