using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Startup;

/// <summary>
/// One piece of duty work that is owed: it stays owed until the holder of <see cref="Role"/>
/// completes it ("pending until done").
/// </summary>
/// <param name="Role">The role (duty) whose holder runs the work.</param>
/// <param name="WorkKey">What is owed, e.g. a startup step's name.</param>
/// <param name="FirstOwedAt">When anyone first owed it, in database time.</param>
/// <param name="LastOwedAt">When it was last owed, in database time. Completion presents this so
/// a need that arose while the work was running is not lost.</param>
/// <param name="Attempts">Failed attempts so far.</param>
/// <param name="LastError">Why the last attempt failed, if one did.</param>
/// <param name="IsDue">False while a failed attempt is backing off.</param>
/// <docs>proposals/duty-role-assignment</docs>
public sealed record PendingDutyWork(
  string Role, string WorkKey, DateTimeOffset FirstOwedAt, DateTimeOffset LastOwedAt, int Attempts, string? LastError, bool IsDue);

/// <summary>How an attempt to mark duty work done ended.</summary>
/// <docs>proposals/duty-role-assignment</docs>
public enum DutyWorkCompletion {
  /// <summary>The work is done and no longer owed.</summary>
  Completed,

  /// <summary>Someone owed the work again while it ran, so it stays owed and runs again.</summary>
  OwedAgain,

  /// <summary>The grant no longer holds the role: the fence refused it, and the next holder finishes the work.</summary>
  Fenced,
}

/// <summary>
/// Durable duty work: any instance may owe work to a role, and only the current holder completes
/// it, under the epoch fence. Work interrupted by a hand-off is therefore finished by the next
/// holder, exactly once.
/// </summary>
/// <docs>proposals/duty-role-assignment</docs>
public interface IPendingDutyWorkStore {
  /// <summary>True when a real store is registered. The framework's null default returns false, so a
  /// caller does not report work as owed when nothing would ever run it.</summary>
  bool IsConfigured => true;

  /// <summary>Records that <paramref name="workKey"/> is owed to the holder of <paramref name="role"/>. Idempotent.</summary>
  /// <param name="role">The role.</param>
  /// <param name="workKey">What is owed.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task OweAsync(string role, string workKey, CancellationToken cancellationToken);

  /// <summary>The work owed to the holder of <paramref name="role"/>, oldest first.</summary>
  /// <param name="role">The role.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The owed work.</returns>
  Task<IReadOnlyList<PendingDutyWork>> ListOwedAsync(string role, CancellationToken cancellationToken);

  /// <summary>Marks <paramref name="work"/> done, fenced by <paramref name="grant"/>'s epoch.</summary>
  /// <param name="work">The work as it was listed.</param>
  /// <param name="grant">The holder's grant.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How the completion ended.</returns>
  Task<DutyWorkCompletion> CompleteAsync(PendingDutyWork work, IDutyGrant grant, CancellationToken cancellationToken);

  /// <summary>Records a failed attempt; the work stays owed and backs off. Fenced like completion.</summary>
  /// <param name="work">The work as it was listed.</param>
  /// <param name="grant">The holder's grant.</param>
  /// <param name="failure">Why it failed.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>False when the fence refused the grant.</returns>
  Task<bool> RecordFailureAsync(PendingDutyWork work, IDutyGrant grant, string failure, CancellationToken cancellationToken);
}

/// <summary>What running one piece of duty work achieved.</summary>
/// <docs>proposals/duty-role-assignment</docs>
public enum DutyWorkStatus {
  /// <summary>Finished; it may stop being owed.</summary>
  Done,

  /// <summary>Attempted and not finished; it stays owed, and the failure is recorded so it backs off.</summary>
  NotDone,

  /// <summary>Not attempted yet (for example, this instance is still starting); it stays owed, with no failure recorded.</summary>
  Deferred,
}

/// <summary>The result of running one piece of duty work.</summary>
/// <param name="Status">Done, not done, or deferred.</param>
/// <param name="Detail">Why, when it is not done.</param>
/// <docs>proposals/duty-role-assignment</docs>
public readonly record struct DutyWorkResult(DutyWorkStatus Status, string? Detail = null) {
  /// <summary>The work is finished.</summary>
  public static DutyWorkResult Done() => new(DutyWorkStatus.Done);

  /// <summary>The work was attempted and is not finished.</summary>
  /// <param name="detail">Why.</param>
  public static DutyWorkResult NotDone(string detail) => new(DutyWorkStatus.NotDone, detail);

  /// <summary>The work was not attempted yet.</summary>
  /// <param name="detail">Why.</param>
  public static DutyWorkResult Deferred(string detail) => new(DutyWorkStatus.Deferred, detail);
}

/// <summary>
/// Runs one kind of owed duty work on the holder. Must be idempotent and resumable: a hand-off can
/// interrupt it, and the next holder runs it again from the start.
/// </summary>
/// <docs>proposals/duty-role-assignment</docs>
public interface IDutyWorkHandler {
  /// <summary>The role whose holder runs this work.</summary>
  string Role { get; }

  /// <summary>The work key this handler runs.</summary>
  string WorkKey { get; }

  /// <summary>Runs the work under <paramref name="grant"/>; exclusive writes present its epoch.</summary>
  /// <param name="grant">The holder's grant.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Whether the work is done.</returns>
  ValueTask<DutyWorkResult> RunAsync(IDutyGrant grant, CancellationToken cancellationToken);
}

/// <summary>
/// The framework's null default for <see cref="IPendingDutyWorkStore"/>: reports
/// <see cref="IPendingDutyWorkStore.IsConfigured"/> false and owes nothing, so a skipped duty step
/// is simply skipped, exactly as before owed work existed. Role assignment supplies the real store.
/// </summary>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupPipelineRunnerOweTests.cs</tests>
public sealed class NullPendingDutyWorkStore : IPendingDutyWorkStore, INullDefault {
  private NullPendingDutyWorkStore() { }

  /// <summary>The shared instance.</summary>
  public static NullPendingDutyWorkStore Instance { get; } = new();

  /// <inheritdoc />
  public bool IsConfigured => false;

  /// <inheritdoc />
  public Task OweAsync(string role, string workKey, CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public Task<IReadOnlyList<PendingDutyWork>> ListOwedAsync(string role, CancellationToken cancellationToken) =>
    Task.FromResult<IReadOnlyList<PendingDutyWork>>([]);

  /// <inheritdoc />
  public Task<DutyWorkCompletion> CompleteAsync(PendingDutyWork work, IDutyGrant grant, CancellationToken cancellationToken) =>
    throw new InvalidOperationException("No pending duty work store is registered, so no work is owed; check IsConfigured before calling.");

  /// <inheritdoc />
  public Task<bool> RecordFailureAsync(PendingDutyWork work, IDutyGrant grant, string failure, CancellationToken cancellationToken) =>
    throw new InvalidOperationException("No pending duty work store is registered, so no work is owed; check IsConfigured before calling.");
}
