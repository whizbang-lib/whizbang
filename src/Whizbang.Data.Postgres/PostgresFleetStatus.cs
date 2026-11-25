// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The fleet and each instance's assignment, read the same way by both Postgres drivers (#2595).
/// </summary>
/// <docs>operations/startup/startup-status#instance-assignments</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceAssignmentDriverParityTests.cs</tests>
internal static class PostgresFleetStatus {
  /// <summary>The kind <c>wh_instance_assignments()</c> names.</summary>
  /// <param name="name">The SQL name.</param>
  /// <returns>The kind.</returns>
  /// <exception cref="InvalidOperationException">A name this release does not know.</exception>
  internal static InstanceAssignmentKind KindOf(string name) => name switch {
    "starting" => InstanceAssignmentKind.Starting,
    "standard-worker" => InstanceAssignmentKind.StandardWorker,
    "role-holder" => InstanceAssignmentKind.RoleHolder,
    "awaiting-share" => InstanceAssignmentKind.AwaitingShare,
    "draining" => InstanceAssignmentKind.Draining,
    "ineligible" => InstanceAssignmentKind.Ineligible,
    "evicted" => InstanceAssignmentKind.Evicted,
    _ => throw new InvalidOperationException($"wh_instance_assignments() named an assignment this release does not know: '{name}'"),
  };
}
