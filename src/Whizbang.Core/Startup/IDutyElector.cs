using System;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Startup;

/// <summary>The framework's own duties — exclusive capabilities, held by one instance at a time.</summary>
/// <docs>operations/startup/capabilities-and-duties</docs>
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
/// <docs>operations/startup/capabilities-and-duties</docs>
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

  /// <summary>
  /// The fencing token for this grant, or <see langword="null"/> when the implementation has none.
  /// A role-assignment grant carries the assignment's epoch, which exclusive-work SQL presents with
  /// the holder's instance id (<c>wh_assert_role_epoch</c>) so a stale holder's writes are refused
  /// by the database rather than trusted client-side. A session-lock grant has no epoch.
  /// </summary>
  /// <remarks>
  /// A default member, so every existing grant implementation keeps compiling and keeps meaning
  /// what it meant: no fencing token, rather than a made-up one.
  /// </remarks>
  /// <docs>proposals/duty-role-assignment</docs>
  /// <tests>tests/Whizbang.Core.Tests/Startup/DutyGrantContractTests.cs</tests>
  long? Epoch => null;

  /// <summary>
  /// Whether a newer-version instance has asked this holder to drain: finish the current step,
  /// then release, so the role hands over to the newer release without waiting for a lapse. Learned
  /// on a renewal, so it turns true only after a <see cref="VerifyStillHeldAsync"/>. A holder that
  /// sees it releases after the step it is on; it never abandons work mid-step.
  /// </summary>
  /// <remarks>A default member: a grant that cannot be asked to drain is never asked.</remarks>
  /// <docs>proposals/duty-role-assignment</docs>
  /// <tests>tests/Whizbang.Core.Tests/Startup/DutyGrantContractTests.cs</tests>
  bool DrainRequested => false;
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
/// <docs>operations/startup/capabilities-and-duties</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DutyElectionE2ETests.cs</tests>
public interface IDutyElector {
  /// <summary>True when a real implementation is registered. The framework's null default returns false so
  /// a consumer takes the same skip path an unregistered subsystem produced, without a null check.</summary>
  bool IsConfigured => true;

  /// <summary>
  /// Attempts to win <paramref name="duty"/>. Returns a granted attempt when this instance now
  /// holds it, or a refused attempt that says WHY not — losing the race is the only refusal
  /// worth retrying, and a caller that cannot tell the reasons apart retries the unretryable
  /// forever (issue #494). Never blocks waiting for the holder.
  /// </summary>
  Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken);

  /// <summary>
  /// Attempts several duties at once, one attempt per duty, in the order given. An implementation
  /// that can vote for several roles in one round trip does; the default asks one at a time.
  /// </summary>
  /// <param name="duties">The duties to attempt.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>One attempt per duty, in the order of <paramref name="duties"/>.</returns>
  /// <docs>proposals/duty-role-assignment</docs>
  /// <tests>tests/Whizbang.Core.Tests/Startup/DutyGrantContractTests.cs</tests>
  async Task<IReadOnlyList<DutyAttempt>> TryAcquireManyAsync(IReadOnlyList<string> duties, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(duties);
    var attempts = new List<DutyAttempt>(duties.Count);
    foreach (var duty in duties) {
      attempts.Add(await TryAcquireAsync(duty, cancellationToken).ConfigureAwait(false));
    }
    return attempts;
  }
}

/// <summary>
/// Why a duty attempt did not grant. The distinction is load-bearing: <see cref="Contended"/> is
/// the only refusal that retrying can resolve.
/// </summary>
/// <docs>operations/startup/capabilities-and-duties</docs>
public enum DutyRefusal {
  /// <summary>Another instance holds the duty. Retry — the holder's release frees it.</summary>
  Contended,

  /// <summary>
  /// The elector cannot reach or resolve its coordination primitive at all — typically a missing
  /// connection configuration. A standing condition: retrying cannot succeed.
  /// </summary>
  Unavailable,

  /// <summary>
  /// This instance may not hold the duty — evicted or unregistered. Needs an operator or a
  /// restart, not a retry.
  /// </summary>
  Refused,
}

/// <summary>
/// One acquisition attempt's outcome: either a <see cref="Grant"/>, or a <see cref="Refusal"/>
/// with the <see cref="Detail"/> a human needs. Exactly one of grant/refusal is present.
/// </summary>
/// <docs>operations/startup/capabilities-and-duties</docs>
public sealed record DutyAttempt {
  private DutyAttempt(IDutyGrant? grant, DutyRefusal? refusal, string? detail) {
    Grant = grant;
    Refusal = refusal;
    Detail = detail;
  }

  /// <summary>The grant, when this instance now holds the duty.</summary>
  public IDutyGrant? Grant { get; }

  /// <summary>Why not, when it does not.</summary>
  public DutyRefusal? Refusal { get; }

  /// <summary>Human-readable detail for the refusal — what a log line or step reason should say.</summary>
  public string? Detail { get; }

  /// <summary>This instance now holds the duty.</summary>
  public static DutyAttempt Granted(IDutyGrant grant) {
    ArgumentNullException.ThrowIfNull(grant);
    return new DutyAttempt(grant, null, null);
  }

  /// <summary>The attempt did not grant, for the stated reason.</summary>
  public static DutyAttempt Lost(DutyRefusal refusal, string detail) {
    ArgumentException.ThrowIfNullOrEmpty(detail);
    return new DutyAttempt(null, refusal, detail);
  }
}

/// <summary>
/// The framework's null default for <see cref="IDutyElector"/>: reports <see cref="IDutyElector.IsConfigured"/> false, so
/// exclusive startup duties run on every instance. A notification transport supplies the real elector; callers check the
/// flag first and this implementation throws if they do not.
/// </summary>
public sealed class NullDutyElector : IDutyElector, INullDefault {
  private NullDutyElector() { }

  /// <summary>The shared instance.</summary>
  public static NullDutyElector Instance { get; } = new();

  /// <inheritdoc />
  public bool IsConfigured => false;

  /// <inheritdoc />
  public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) =>
    throw new InvalidOperationException("No duty elector is registered; a notification transport supplies one. Check IsConfigured before calling.");
}
