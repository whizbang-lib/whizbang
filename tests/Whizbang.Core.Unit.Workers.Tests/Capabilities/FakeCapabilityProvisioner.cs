// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Capabilities;

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// A provisioner for the fake provider: records every action it is asked for and answers with the status after it,
/// by default the capability Ready at version 1.0 once created, or at its available version once upgraded.
/// </summary>
internal sealed class FakeCapabilityProvisioner(string provider) : ICapabilityProvisioner {
  public string Provider { get; } = provider;

  /// <summary>Every action asked for, in order.</summary>
  public List<(CapabilityId Id, string Scope, CapabilityProvisionAction Action)> Calls { get; } = [];

  /// <summary>When set, the answer to every action; otherwise the action succeeds.</summary>
  public Func<CapabilityStatus, CapabilityProvisionAction, CapabilityStatus>? Answer { get; set; }

  /// <summary>When set, every action throws this.</summary>
  public Exception? Failure { get; set; }

  public Task<CapabilityStatus> ProvisionAsync(
      CapabilityStatus status, CapabilityProvisionAction action, CancellationToken cancellationToken) {
    Calls.Add((status.Descriptor.Id, status.Scope, action));
    if (Failure is not null) {
      throw Failure;
    }
    return Task.FromResult(Answer is null ? Succeeded(status, action) : Answer(status, action));
  }

  public static CapabilityStatus Succeeded(CapabilityStatus status, CapabilityProvisionAction action) =>
    action == CapabilityProvisionAction.Create
      ? status with { State = CapabilityState.Ready, Version = "1.0", Provisioning = CapabilityProvisioningOutcome.Created }
      : status with { Version = status.AvailableVersion, Provisioning = CapabilityProvisioningOutcome.Upgraded };
}
