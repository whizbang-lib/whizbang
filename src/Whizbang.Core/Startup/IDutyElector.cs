using System;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Startup;

/// <summary>The framework's own duties — exclusive capabilities, held by one instance at a time.</summary>
/// <docs>proposals/startup-pipeline#capabilities</docs>
public static class StartupDuties {
#pragma warning disable CA1707 // SCREAMING_CASE constants are the established convention here
  /// <summary>The instance that runs schema migrations.</summary>
  public const string MIGRATOR = "migrator";
  /// <summary>The instance that runs post-ready maintenance (table rewrites).</summary>
  public const string MAINTAINER = "maintainer";
#pragma warning restore CA1707
}

/// <summary>
/// A duty currently held. Dispose to release cleanly; death releases server-side without a call
/// (the session ends, the primitive frees, the recorded holding is reaped with the instance row).
/// </summary>
/// <docs>proposals/startup-pipeline#capabilities</docs>
public interface IDutyGrant : IAsyncDisposable {
  /// <summary>The duty this grant holds.</summary>
  string Duty { get; }

  /// <summary>When the grant was acquired.</summary>
  DateTimeOffset AcquiredAt { get; }

  /// <summary>
  /// Fencing: verifies the grant is still actually held by round-tripping the session that holds
  /// the primitive. A long-tenure holder calls this before each unit of exclusive work — a grant
  /// whose session died is a grant another instance may already hold. Returns false (and marks
  /// the grant lost) instead of throwing.
  /// </summary>
  Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Wins duties. An instance never looks up whether it has been <em>assigned</em> a capability —
/// it attempts acquisition, and the primitive grants or refuses. <b>The lock decides, the row
/// reports</b>: implementations record the holding after winning, and if the record and the lock
/// ever disagree, the lock is right.
/// </summary>
/// <remarks>
/// Election is deliberately not membership: the primitive is linearizable against the database
/// every instance already depends on, with no timeout to tune and no split-brain window. Liveness
/// (heartbeats, <c>InstanceDiedSignal</c>) only prompts <em>re-attempts</em> — it never decides.
/// The eviction fence reaches here too: an evicted instance is refused at acquisition even when
/// it wins the primitive, and the implementation releases what it won.
/// </remarks>
/// <docs>proposals/startup-pipeline#capabilities</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DutyElectionE2ETests.cs</tests>
public interface IDutyElector {
  /// <summary>
  /// Attempts to win <paramref name="duty"/>. Returns the grant when this instance now holds it,
  /// or <see langword="null"/> when another instance does — or when this instance has been
  /// evicted and must not hold exclusive work. Never blocks waiting for the holder.
  /// </summary>
  Task<IDutyGrant?> TryAcquireAsync(string duty, CancellationToken cancellationToken);
}
