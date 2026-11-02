// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// Signal that the schema is provisioned. The schema initializer
/// (<c>WhizbangDatabaseInitializerService</c>, which both Postgres drivers share) calls
/// <see cref="MarkReady"/> after migrations succeed. Workers do not wait on this gate themselves:
/// a worker is registered with the point it starts at (<c>AddWhizbangWorker</c> or
/// <c>AddWhizbangWorkerAtSchemaReady</c>), and the host starts it only once startup reaches that
/// point, which is never before this gate opens.
/// </summary>
/// <remarks>
/// <para>
/// This decouples worker startup from hosted-service registration order. Even when a
/// consumer's DI graph registers workers before the driver (the default for
/// <c>AddWhizbang().WithDriver.Postgres</c>, since <c>AddWhizbang()</c> calls
/// <c>AddWhizbangWorkers()</c>), no worker fires SQL against an unmigrated database: the hosting
/// wrapper holds it. Only the startup machinery that drives the later transitions reads the gate
/// directly; a source-scan guard test fails if a worker starts waiting on it itself.
/// </para>
/// <para>
/// On migration failure, the initializer must NOT mark ready. Workers will then never enter
/// their main loop, and the host's <c>StartAsync</c> sequence aborts (initializer threw),
/// keeping the system in a safe halted state instead of running on a broken schema.
/// </para>
/// </remarks>
/// <docs>operations/workers/database-readiness</docs>
public interface ISchemaReadyGate {
  /// <summary>
  /// Awaits the schema-ready signal. Returns immediately when ready; otherwise blocks until
  /// <see cref="MarkReady"/> is called or <paramref name="cancellationToken"/> fires.
  /// </summary>
  Task WaitForReadyAsync(CancellationToken cancellationToken);

  /// <summary>True once <see cref="MarkReady"/> has been called; pure synchronous query.</summary>
  bool IsReady { get; }

  /// <summary>
  /// Signals all waiters that the schema is provisioned. Idempotent — subsequent calls are
  /// no-ops. Called by the initializer in its <c>StartAsync</c> after migrations complete.
  /// </summary>
  void MarkReady();
}

/// <summary>
/// Default <see cref="ISchemaReadyGate"/> implementation. One <see cref="TaskCompletionSource"/>;
/// any number of waiters; signal is sticky (post-mark waiters return immediately).
/// </summary>
/// <docs>fundamentals/work-coordinator/startup-ordering</docs>
public sealed class SchemaReadyGate : ISchemaReadyGate {
  private readonly TaskCompletionSource _ready =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <inheritdoc />
  public bool IsReady => _ready.Task.IsCompleted;

  /// <inheritdoc />
  public Task WaitForReadyAsync(CancellationToken cancellationToken)
    => _ready.Task.WaitAsync(cancellationToken);

  /// <inheritdoc />
  public void MarkReady() => _ready.TrySetResult();

  /// <summary>A gate that is already open.</summary>
  /// <remarks>
  /// For a host with no schema step to wait on: an in-memory composition, or storage
  /// provisioned out of band. Such a host used to say "nothing to wait for" by supplying
  /// no gate, which is indistinguishable from forgetting to supply one, and the difference
  /// matters because the second case skipped a wait that should have run.
  /// </remarks>
  public static SchemaReadyGate AlreadyReady() {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return gate;
  }
}
