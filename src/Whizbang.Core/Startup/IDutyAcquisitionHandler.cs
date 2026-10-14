// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Startup;

/// <summary>
/// Work that belongs to acquiring a duty rather than to holding it: run by <see cref="DutyHolderWorker"/>
/// once on the pass that wins the role, never on the passes that merely keep it, and again each time
/// the role is lost and won back. An instance that never wins the role never runs it.
/// </summary>
/// <remarks>
/// <para>
/// It runs on the holder loop, before the role's owed work, so it is bounded by the same rule: a handler
/// that hangs stops the loop and the role lapses to another instance. Exclusive work verifies the grant
/// before it starts. A failure is logged and does not give the role up; it is not retried until the
/// role is acquired again.
/// </para>
/// <para>
/// <see cref="IDutyGrant.PreviousHolderInstanceId"/> tells a hand-off or a takeover (another instance
/// held the role) from a first assignment.
/// </para>
/// </remarks>
/// <docs>operations/startup/capabilities-and-duties#acquiring-a-duty</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/DutyHolderWorkerTests.cs</tests>
public interface IDutyAcquisitionHandler {
  /// <summary>The role whose acquisition runs this handler.</summary>
  string Role { get; }

  /// <summary>Runs on the pass that won <see cref="Role"/>.</summary>
  /// <param name="grant">The grant that won it.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes when the work is done.</returns>
  ValueTask OnAcquiredAsync(IDutyGrant grant, CancellationToken cancellationToken);
}
