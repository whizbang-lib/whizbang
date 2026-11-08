// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whizbang.Core.Startup;

/// <summary>
/// Keeps a held duty's lease alive while its holder does one long piece of exclusive work, by verifying the grant on
/// a timer for as long as the keepalive runs (#2127).
/// </summary>
/// <remarks>
/// <para>
/// A holder normally renews from its own work loop, so a holder that is stuck stops renewing and lapses. Startup is
/// the exception: the migrator's work between Elect and the end of Migrate is a handful of phases (the stored-form
/// scans, a wait for the schema lock, an index rebuild, a long migration step), each of which can run for longer than
/// the lease with nothing in it that could verify. A lease that lapses there is refused renewal even when nobody took
/// it, so the migrator loses the role it is still exercising and a waiter takes the same work over.
/// </para>
/// <para>
/// The timer ticks at half the grant's <see cref="IDutyGrant.RenewInterval"/>. A grant throttles its own renewals to
/// one per interval, so the database sees one renewal per interval, and never more than one and a half intervals
/// pass between two renewals, which every lease (at least two intervals) outlasts. Ticks that find the previous verify
/// still running are owed one verify, run as soon as it answers, so a slow database never queues renewals and no tick
/// is lost. A verify that answers false is not an end:
/// the grant says itself whether it lost the role or could not reach the database, and the next tick asks again.
/// </para>
/// <para>
/// Disposing stops the timer and waits for a verify in flight, so nothing renews the grant once it returns: the
/// owner disposes the keepalive first and the grant after it. It never disposes the grant.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/DutyGrantKeepaliveTests.cs</tests>
public sealed partial class DutyGrantKeepalive : IAsyncDisposable {
  private DutyGrantKeepalive(bool running) {
    IsRunning = running;
    TickInterval = running ? TimeSpan.Zero : null;
  }

  /// <summary>Whether the keepalive is renewing a grant now: started over a grant with a lease, and not yet disposed.</summary>
  public bool IsRunning { get; }

  /// <summary>How often it verifies the grant: half the grant's renew interval, or null when there is nothing to keep alive.</summary>
  public TimeSpan? TickInterval { get; }

  /// <summary>Starts keeping <paramref name="grant"/> alive (not yet: RED).</summary>
  /// <param name="grant">The grant, or null when nothing is held.</param>
  /// <param name="timeProvider">The clock the ticks are measured on.</param>
  /// <param name="logger">Where a verify that throws is reported.</param>
  /// <returns>The keepalive; dispose it before the grant.</returns>
  public static DutyGrantKeepalive Start(IDutyGrant? grant, TimeProvider timeProvider, ILogger? logger = null) {
    ArgumentNullException.ThrowIfNull(timeProvider);
    _ = grant;
    _ = logger ?? NullLogger.Instance;
    return new DutyGrantKeepalive(false);
  }

  /// <inheritdoc />
  public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
