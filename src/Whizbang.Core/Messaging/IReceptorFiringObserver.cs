using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Optional observability hook invoked by <see cref="ReceptorInvoker"/> before and after
/// each receptor fires. Primary use is deterministic test synchronization — tests can
/// resolve an <see cref="IReceptorFiringObserver"/>, register a
/// <see cref="TaskCompletionSource"/> for a specific receptor, and deterministically await
/// its next fire without <c>Task.Delay</c> / polling.
/// </summary>
/// <remarks>
/// <para>
/// Production code generally does not resolve this — it's a test hook. The invoker
/// resolves it via <see cref="IServiceProvider.GetService"/>; when nothing is registered,
/// no callbacks fire and there is zero cost.
/// </para>
/// <para>
/// Both callbacks are invoked on the same thread that invokes the receptor, immediately
/// before and after the receptor delegate. Exceptions raised from observer callbacks bubble
/// up to the caller; do not throw from production code paths.
/// </para>
/// </remarks>
/// <docs>operations/testing/receptor-firing-observer</docs>
public interface IReceptorFiringObserver {
  /// <summary>
  /// Called immediately before a receptor is invoked, after the dedup / replay guards but
  /// before the receptor delegate runs. Skipped invocations (guardrail-blocked, replay-filtered)
  /// do NOT invoke this callback.
  /// </summary>
  ValueTask OnReceptorFiringAsync(
    string receptorId,
    LifecycleStage stage,
    Guid messageId,
    IMessageEnvelope envelope,
    CancellationToken cancellationToken);

  /// <summary>
  /// Called after the receptor delegate returns, from the <c>finally</c> block so it fires
  /// on both success and failure. <paramref name="exception"/> is null on success.
  /// </summary>
  ValueTask OnReceptorFiredAsync(
    string receptorId,
    LifecycleStage stage,
    Guid messageId,
    IMessageEnvelope envelope,
    TimeSpan duration,
    Exception? exception,
    CancellationToken cancellationToken);
}
