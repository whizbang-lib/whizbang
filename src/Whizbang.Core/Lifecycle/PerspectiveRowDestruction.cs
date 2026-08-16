using System.Text.Json;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// One perspective row the next retention sweep would destroy, offered to a registered guard
/// before it dies. Carries the row's payload so a guard can find the external resources the row
/// references (a blob name, a file path) and clean them up first — the row must outlive the
/// resource, never the reverse.
/// </summary>
/// <param name="ClrTypeName">The perspective model's CLR type name (the registry key).</param>
/// <param name="TableName">The perspective's physical table.</param>
/// <param name="RowId">The row id (the stream id for stream-keyed perspectives).</param>
/// <param name="Scope">The row's scope JSON, when present.</param>
/// <param name="Data">The row's model payload JSON.</param>
/// <param name="Reason">Which sweep selected it: <c>ttl</c> (expiry ladder) or <c>cap</c> (per-scope overflow).</param>
/// <docs>proposals/pre-destruction-seam</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveRowSweepTests.cs</tests>
public sealed record PerspectiveRowDestructionTarget(
  string ClrTypeName,
  string TableName,
  Guid RowId,
  JsonElement? Scope,
  JsonElement Data,
  string Reason);

/// <summary>A (table, row) reference for hold and failure bookkeeping.</summary>
/// <param name="TableName">The perspective's physical table.</param>
/// <param name="RowId">The row id.</param>
/// <docs>proposals/pre-destruction-seam</docs>
public readonly record struct PerspectiveRowRef(string TableName, Guid RowId);

/// <summary>How a guard disposed of one offered row.</summary>
/// <docs>proposals/pre-destruction-seam</docs>
public enum PerspectiveRowDispositionKind {
  /// <summary>The row may be destroyed — external cleanup is verified complete.</summary>
  Proceed = 0,

  /// <summary>Postpone destruction until the given instant; the row is re-offered after it lapses.</summary>
  Defer = 1,

  /// <summary>Keep the row indefinitely (an explicit, observable leak-risk decision).</summary>
  Cancel = 2,
}

/// <summary>A guard's per-row decision: proceed, defer until an instant, or cancel.</summary>
/// <param name="Kind">The disposition.</param>
/// <param name="DeferUntil">The re-offer instant, when <paramref name="Kind"/> is <see cref="PerspectiveRowDispositionKind.Defer"/>.</param>
/// <docs>proposals/pre-destruction-seam</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveRowSweepTests.cs</tests>
public readonly record struct PerspectiveRowDecision(
  PerspectiveRowDispositionKind Kind,
  DateTimeOffset? DeferUntil = null) {
  /// <summary>The row may be destroyed.</summary>
  public static PerspectiveRowDecision Proceed() => new(PerspectiveRowDispositionKind.Proceed);

  /// <summary>Postpone destruction until <paramref name="until"/>.</summary>
  public static PerspectiveRowDecision Defer(DateTimeOffset until) => new(PerspectiveRowDispositionKind.Defer, until);

  /// <summary>Keep the row indefinitely.</summary>
  public static PerspectiveRowDecision Cancel() => new(PerspectiveRowDispositionKind.Cancel);
}

/// <summary>
/// The opt-in pre-destruction guard for perspective rows: before the retention sweeps destroy the
/// rows of a guarded perspective, the batch is offered here so external resources the rows
/// reference can be cleaned up (and verified) first. Registering a guard is the opt-in — an
/// unguarded perspective keeps the pure-SQL sweep path, untouched.
/// </summary>
/// <remarks>
/// <para>
/// <b>A row absent from the returned decisions is DEFERRED, never destroyed.</b> The guard
/// exists to prevent orphaned external resources; silence must fail safe. A guard that throws
/// gets the destruction retry ladder (bounded retries, then the configured
/// <see cref="OnDestroyFailure"/> policy) — never a silent fail-open.
/// </para>
/// <para>
/// The offering is batched (one call per perspective per maintenance cycle, bounded) and fires on
/// every eviction path uniformly — TTL expiry and cap overflow — so a resource-referencing row
/// cannot slip out through a path the guard didn't cover.
/// </para>
/// </remarks>
/// <docs>proposals/pre-destruction-seam</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveRowSweepTests.cs</tests>
public interface IPerspectiveRowDestructionGuard {
  /// <summary>The perspective model types this guard protects.</summary>
  IReadOnlyCollection<Type> GuardedModels { get; }

  /// <summary>
  /// Offered the batch of about-to-die rows for this guard's perspectives; returns per-row
  /// decisions keyed by <see cref="PerspectiveRowDestructionTarget.RowId"/>. Rows without a
  /// decision are deferred.
  /// </summary>
  /// <param name="targets">The rows the next sweep would destroy.</param>
  /// <param name="cancellationToken">Cancels the offering.</param>
  ValueTask<IReadOnlyDictionary<Guid, PerspectiveRowDecision>> OnBeforeReapAsync(
    IReadOnlyList<PerspectiveRowDestructionTarget> targets,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Fired after the sweeps ran, with the rows this cycle released to destruction (the proceeding
  /// set). Never blocks destruction; a throw is logged and ignored.
  /// </summary>
  /// <param name="released">The rows that proceeded this cycle.</param>
  /// <param name="cancellationToken">Cancels the notification.</param>
  ValueTask OnAfterReapAsync(
    IReadOnlyList<PerspectiveRowDestructionTarget> released,
    CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
