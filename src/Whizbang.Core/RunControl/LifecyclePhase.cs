namespace Whizbang.Core.RunControl;

/// <summary>
/// The one lifecycle state Whizbang owns and advances. It is the single signal both faces of the
/// managed-resource control plane read: <b>run-control</b> (each resource interprets the phase to
/// decide what it does) and <b>health</b> (each resource reports its state judged against the phase).
/// <para>
/// States are either <b>settled</b> (a resting point) or <b>transitional</b> — a transitional phase is
/// the window in which the coordinator has broadcast the change and is awaiting every resource's
/// acknowledgement (see <c>WhizbangLifecycleCoordinator</c>). The settled phase on the other side of a
/// transition is "all acknowledged".
/// </para>
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public enum LifecyclePhase {
  /// <summary>Process booted, host constructing. (transitional)</summary>
  Starting,
  /// <summary>Network warmup — DB/transport/offload connections coming up, before the migration. (transitional)</summary>
  Connecting,
  /// <summary>Schema/data migration in progress; it needs the DB connection from <see cref="Connecting"/>. (transitional)</summary>
  Migrating,
  /// <summary>
  /// The WRITE side is live — commands, events, transport, perspective writes — and the READ
  /// side (lenses) is not yet: the schema is migrated, so a dispatch lands durably in the event
  /// store and outbox (the transport client connected back in <see cref="Connecting"/>; publish
  /// workers drain the outbox; brokers buffer inbound), and the perspective startup repair is
  /// itself perspective-writing in progress — the write side actively making the read side
  /// trustworthy. A lens read is a synchronous claim about NOW — during the repair window it
  /// could be WRONG, not
  /// merely late, so reads keep refusing until <see cref="Running"/>. Accepting is not
  /// completing: events written here project once the repair finishes, and a caller that reads
  /// its own write through a lens is refused until the read side opens.
  /// <para>
  /// Per dispatch mode: an outbox-routed command is a durable promise — safe here
  /// unconditionally. A LOCAL command executes its receptor now: event-store writes are safe
  /// (events are source-of-truth; the repair rewrites perspectives, never events), and a
  /// receptor that READS a perspective inherits the read barrier at the lens seam and refuses —
  /// each dispatch gets exactly the barrier its actual dependencies require, which is the point
  /// of seam-level gating.
  /// </para>
  /// (transitional)
  /// </summary>
  AcceptingCommands,
  /// <summary>Fully operational — reads AND writes serving. (settled)</summary>
  Running,
  /// <summary>Broadcast "pause", awaiting acks. (transitional)</summary>
  Pausing,
  /// <summary>Intentionally held, resumable. (settled)</summary>
  Paused,
  /// <summary>
  /// Drained and holding for a peer's breaking migration (or standing down as obsolete):
  /// in-flight work finished, data plane held, capabilities released, still alive and reapable.
  /// Resumable — revival re-enters the startup pipeline at Assess. (settled)
  /// </summary>
  StandingBy,
  /// <summary>Broadcast "resume", awaiting acks; returns to <see cref="Running"/>. (transitional)</summary>
  Resuming,
  /// <summary>Broadcast "stop"; resources drain in-flight, awaiting acks. (transitional)</summary>
  Stopping,
  /// <summary>Settled graceful stop. (settled)</summary>
  Stopped,
  /// <summary>A fault occurred; a bounded window to record/report/emit before dying. (transitional)</summary>
  Faulted,
  /// <summary>Terminal dead state — the orchestrator will replace the pod. Reachable ONLY from <see cref="Faulted"/>. (settled)</summary>
  Halted
}

/// <summary>
/// Classification helpers over <see cref="LifecyclePhase"/> — a transitional phase is a coordinator
/// ack-barrier window; a terminal phase is an end state.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/LifecyclePhaseTests.cs:TransitionalPhases_AreTransitional_NotSettledAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/LifecyclePhaseTests.cs:SettledPhases_AreSettled_NotTransitionalAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/LifecyclePhaseTests.cs:TerminalPhases_AreTerminalAsync</tests>
public static class LifecyclePhaseExtensions {
  /// <summary>
  /// True while the coordinator is broadcasting the phase and awaiting acks
  /// (<see cref="LifecyclePhase.Starting"/>, <see cref="LifecyclePhase.Connecting"/>,
  /// <see cref="LifecyclePhase.Migrating"/>, <see cref="LifecyclePhase.Pausing"/>,
  /// <see cref="LifecyclePhase.Resuming"/>, <see cref="LifecyclePhase.Stopping"/>,
  /// <see cref="LifecyclePhase.Faulted"/>).
  /// </summary>
  public static bool IsTransitional(this LifecyclePhase phase) => phase is
    LifecyclePhase.Starting or LifecyclePhase.Connecting or LifecyclePhase.Migrating or
    LifecyclePhase.Pausing or LifecyclePhase.Resuming or LifecyclePhase.Stopping or LifecyclePhase.Faulted;

  /// <summary>True for a resting phase (the inverse of <see cref="IsTransitional"/>).</summary>
  public static bool IsSettled(this LifecyclePhase phase) => !phase.IsTransitional();

  /// <summary>True for an end state (<see cref="LifecyclePhase.Stopped"/> or <see cref="LifecyclePhase.Halted"/>).</summary>
  public static bool IsTerminal(this LifecyclePhase phase) =>
    phase is LifecyclePhase.Stopped or LifecyclePhase.Halted;
}
