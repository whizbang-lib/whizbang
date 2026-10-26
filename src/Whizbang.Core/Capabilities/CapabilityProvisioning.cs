// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Capabilities;

/// <summary>What the provisioning runner asks a provider to do about one capability in one scope.</summary>
/// <docs>operations/infrastructure/capabilities#provisioning</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityProvisioningTests.cs</tests>
public enum CapabilityProvisionAction {
  /// <summary>Install or enable it: it is <see cref="CapabilityState.Provisionable"/>.</summary>
  Create,

  /// <summary>Move it to <see cref="CapabilityStatus.AvailableVersion"/>: it is Ready at an older version.</summary>
  Upgrade,
}

/// <summary>What provisioning did about a required capability on this start, as the verdict line says it.</summary>
/// <docs>operations/infrastructure/capabilities#provisioning</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityProvisioningTests.cs</tests>
public enum CapabilityProvisioningOutcome {
  /// <summary>The library installed or enabled it (<c>created</c>).</summary>
  Created,

  /// <summary>It was already there, put there by an operator or another instance (<c>already present</c>).</summary>
  Present,

  /// <summary>The library moved it to a newer version (<c>upgraded</c>).</summary>
  Upgraded,
}

/// <summary>
/// A provider's provisioning actions: what creates or upgrades one of its capabilities in one scope. The generic
/// runner decides when (a required capability, the switch on, the descriptor provisionable); the provider decides how.
/// </summary>
/// <remarks>
/// <para>
/// Register an implementation as an <see cref="ICapabilityProvisioner"/> singleton (with <c>TryAddEnumerable</c>)
/// next to the provider's <see cref="ICapabilityProvider"/>. A provider without one is only detected.
/// </para>
/// <para>
/// The answer is the capability's status after the action, read back from the scope, with
/// <see cref="CapabilityStatus.Provisioning"/> saying what happened. A refusal the provider can classify is an answer,
/// not an exception: the state it reveals, with the server's words as the reason. Anything else may throw; the
/// runner reports it and keeps the status the probe read.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/capabilities#provisioning</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityProvisioningTests.cs</tests>
public interface ICapabilityProvisioner {
  /// <summary>The provider's name, as <see cref="ICapabilityProvider.Name"/> gives it.</summary>
  string Provider { get; }

  /// <summary>Creates or upgrades the capability <paramref name="status"/> describes, in its scope.</summary>
  /// <param name="status">The capability's status as the probe read it.</param>
  /// <param name="action">What to do.</param>
  /// <param name="cancellationToken">The caller's cancellation.</param>
  /// <returns>The status after the action.</returns>
  Task<CapabilityStatus> ProvisionAsync(CapabilityStatus status, CapabilityProvisionAction action, CancellationToken cancellationToken);
}

/// <summary>
/// The generic provisioning runner, run by the <c>Capabilities</c> step between the probe and the verdict: for each
/// status a requirement in force names, a Provisionable capability is created and a Ready one with a newer version
/// available is upgraded, through its provider's <see cref="ICapabilityProvisioner"/>, when
/// <see cref="CapabilityAutoProvision"/> allows it.
/// </summary>
/// <remarks>
/// It never throws but for cancellation. A capability it holds back (switch off, a capability the library never
/// provisions, no provisioner) keeps its state with the reason, and the verdict judges it as it is; an upgrade held
/// back or failed leaves the capability Ready and already present. A Ready capability with nothing to do reads
/// already present, whatever the switch says.
/// </remarks>
/// <docs>operations/infrastructure/capabilities#provisioning</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityProvisioningTests.cs</tests>
internal static partial class CapabilityProvisioningRunner {
  /// <summary>Provisions every required capability the switch allows.</summary>
  /// <param name="statuses">The statuses the probe read.</param>
  /// <param name="inForce">The requirements in force.</param>
  /// <param name="provisioners">Every provider's provisioner.</param>
  /// <param name="options">The bound options, which hold the global switch.</param>
  /// <param name="configuration">The host's configuration, which holds the per-provider and per-capability switches.</param>
  /// <param name="logger">Receives a warning for each action that failed.</param>
  /// <param name="cancellationToken">The caller's cancellation, the only thing that propagates.</param>
  /// <returns>The statuses after provisioning, in the order given.</returns>
  public static async Task<IReadOnlyList<CapabilityStatus>> RunAsync(
      IReadOnlyList<CapabilityStatus> statuses,
      IReadOnlyList<ActiveCapabilityRequirement> inForce,
      IReadOnlyList<ICapabilityProvisioner> provisioners,
      CapabilityOptions options,
      IConfiguration? configuration,
      ILogger logger,
      CancellationToken cancellationToken) {
    var after = new CapabilityStatus[statuses.Count];
    for (var i = 0; i < statuses.Count; i++) {
      after[i] = _isRequired(statuses[i], inForce)
        ? await _provisionAsync(statuses[i], provisioners, options, configuration, logger, cancellationToken).ConfigureAwait(false)
        : statuses[i];
    }
    return after;
  }

  private static bool _isRequired(CapabilityStatus status, IReadOnlyList<ActiveCapabilityRequirement> inForce) =>
    inForce.Any(required =>
      string.Equals(required.Requirement.Capability.Provider, status.Descriptor.Id.Provider, StringComparison.OrdinalIgnoreCase)
      && string.Equals(required.Requirement.Capability.Name, status.Descriptor.Id.Name, StringComparison.OrdinalIgnoreCase)
      && (required.Scope is null || string.Equals(required.Scope, status.Scope, StringComparison.Ordinal)));

  private static async Task<CapabilityStatus> _provisionAsync(
      CapabilityStatus status,
      IReadOnlyList<ICapabilityProvisioner> provisioners,
      CapabilityOptions options,
      IConfiguration? configuration,
      ILogger logger,
      CancellationToken cancellationToken) {
    CapabilityProvisionAction action;
    if (status.State == CapabilityState.Provisionable) {
      action = CapabilityProvisionAction.Create;
    } else if (status.State == CapabilityState.Ready && CapabilityVersion.IsNewer(status.AvailableVersion, status.Version)) {
      action = CapabilityProvisionAction.Upgrade;
    } else {
      return status.State == CapabilityState.Ready ? status with { Provisioning = CapabilityProvisioningOutcome.Present } : status;
    }

    var id = status.Descriptor.Id;
    var setting = CapabilityAutoProvision.Resolve(id, options, configuration);
    if (!setting.Enabled) {
      return _heldBack(status, action, $"provisioning is turned off ({setting.Key} = false)");
    }
    if (!status.Descriptor.Provisionable) {
      return _heldBack(status, action, $"Whizbang does not provision {id}");
    }
    var provisioner = provisioners.FirstOrDefault(p => string.Equals(p.Provider, id.Provider, StringComparison.OrdinalIgnoreCase));
    if (provisioner is null) {
      return _heldBack(status, action, $"no provisioner is registered for {id.Provider}");
    }
    try {
      return await provisioner.ProvisionAsync(status, action, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
      LogProvisioningFailed(logger, id, status.Scope, action, ex);
      return _heldBack(status, action, $"provisioning failed ({ex.GetType().Name}: {ex.Message})");
    }
  }

  // A create not done leaves the probe's state with the reason; an upgrade not done leaves a working, older version.
  private static CapabilityStatus _heldBack(CapabilityStatus status, CapabilityProvisionAction action, string reason) =>
    action == CapabilityProvisionAction.Create
      ? status with { Reason = reason }
      : status with {
        Provisioning = CapabilityProvisioningOutcome.Present,
        Reason = $"{status.AvailableVersion} is available, not upgraded: {reason}",
      };

  [LoggerMessage(EventId = 20, Level = LogLevel.Warning,
    Message = "Provisioning {Capability} on '{Scope}' ({Action}) failed; the capability keeps the state the probe read")]
  static partial void LogProvisioningFailed(ILogger logger, CapabilityId capability, string scope, CapabilityProvisionAction action, Exception exception);
}
