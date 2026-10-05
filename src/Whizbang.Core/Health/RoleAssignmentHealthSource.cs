// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Health;

/// <summary>
/// The <c>roles</c> health component (#966, requirement 9): every role held by assignment either
/// has a valid holder, or health says so. A role nobody validly holds is Degraded, never Faulted:
/// its duty work waits rather than breaking this instance, and restarting this instance would not
/// give the role a holder.
/// </summary>
/// <remarks>
/// A read that fails is Degraded too, with the reason, rather than an exception: the database's own
/// component reports reachability, and this one must not turn that into a second, noisier failure.
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Core.Tests/Health/RoleAssignmentHealthSourceTests.cs</tests>
public sealed class RoleAssignmentHealthSource : IWhizbangHealthSource {
  private readonly IRoleAssignmentReader _reader;
  private readonly RoleAssignmentOptions _options;

  /// <summary>Creates the source.</summary>
  /// <param name="reader">Reads the assignments.</param>
  /// <param name="options">Which roles are held by assignment.</param>
  public RoleAssignmentHealthSource(IRoleAssignmentReader reader, IOptions<RoleAssignmentOptions> options) {
    ArgumentNullException.ThrowIfNull(reader);
    ArgumentNullException.ThrowIfNull(options);
    _reader = reader;
    _options = options.Value;
  }

  /// <inheritdoc />
  public string Component => "roles";

  /// <inheritdoc />
  public async ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
    System.Collections.Generic.IReadOnlyList<RoleAssignmentSnapshot> snapshots;
    try {
      snapshots = await _reader.ReadAssignmentsAsync(cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      throw;
#pragma warning disable CA1031 // reported, not swallowed: the failure is the Degraded detail
    } catch (Exception ex) {
#pragma warning restore CA1031
      return new ComponentHealth(ComponentState.Degraded, $"role assignments could not be read: {ex.GetType().Name}");
    }

    var byRole = snapshots.ToDictionary(s => s.Role, StringComparer.Ordinal);
    var managed = _options.Roles.Where(_options.Manages).Order(StringComparer.Ordinal).ToList();
    var unassigned = managed
      .Where(role => _isUnassigned(role, byRole))
      .Select(role => byRole.TryGetValue(role, out var s) ? _describeUnassigned(s) : $"'{role}' unassigned (never elected)")
      .ToList();
    if (unassigned.Count > 0) {
      return new ComponentHealth(ComponentState.Degraded,
        "role unassigned: " + string.Join("; ", unassigned) + " — its duty work waits until an instance holds it");
    }

    var held = managed.Select(role => byRole.TryGetValue(role, out var s) && s.State == RoleAssignmentState.Held
      ? _describeHeld(s)
      : $"'{role}' idle (held only while it runs)");
    return new ComponentHealth(ComponentState.Operational, string.Join("; ", held));
  }

  private static string _describeHeld(RoleAssignmentSnapshot s) {
    var held = string.Create(CultureInfo.InvariantCulture, $"'{s.Role}' held by {s.HolderInstanceId} at epoch {s.Epoch}, {s.PendingWork} owed");
    return s.DrainRequestedAt is null ? held : held + ", draining";
  }

  /// <summary>
  /// A standing role is unassigned unless it is held. An episodic role (the migrator) is vacant
  /// between runs by design, so only a lapsed holder, one that stopped mid-run, counts.
  /// </summary>
  private static bool _isUnassigned(string role, Dictionary<string, RoleAssignmentSnapshot> byRole) {
    var found = byRole.TryGetValue(role, out var snapshot);
    if (RoleAssignmentOptions.IsEpisodic(role)) {
      return found && snapshot!.State == RoleAssignmentState.Lapsed;
    }
    return !found || snapshot!.State != RoleAssignmentState.Held;
  }

  private static string _describeUnassigned(RoleAssignmentSnapshot s) => s.State == RoleAssignmentState.Lapsed
    ? $"'{s.Role}' lapsed ({s.VoidReason}) on {s.HolderInstanceId}, {s.PendingWork} owed"
    : $"'{s.Role}' vacant (last {s.LastVacatedReason ?? "never held"}), {s.PendingWork} owed";
}
