using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Drains ImmediateDetached lifecycle events after a lifecycle stage completes.
/// Uses a <see cref="ConcurrentQueue{T}"/> to support chaining: ImmediateDetached receptors
/// may dispatch further events that themselves have ImmediateDetached receptors.
/// </summary>
/// <remarks>
/// <para>
/// The drain loop processes items from the front of the queue while receptors may enqueue
/// at the back — no iteration, no modification-during-enumeration issues.
/// </para>
/// <para>
/// A configurable warning threshold (default: 10) logs when chain depth gets excessive.
/// No hard limit — chains run until the queue is empty.
/// </para>
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-stages#immediate-async</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateDetachedDrainerTests.cs</tests>
/// <remarks>
/// Creates a new ImmediateDetachedDrainer.
/// </remarks>
/// <param name="warningThreshold">Chain depth warning threshold. Logs when depth reaches a multiple of this value.</param>
/// <param name="logger">Optional logger for chain depth warnings.</param>
public sealed partial class ImmediateDetachedDrainer(int warningThreshold = 10, ILogger? logger = null) {
  private readonly ConcurrentQueue<(IMessageEnvelope Envelope, ILifecycleContext? Context)> _queue = new();
  private readonly int _warningThreshold = warningThreshold > 0 ? warningThreshold : 10;
  private readonly ILogger? _logger = logger;

  /// <summary>
  /// Gets the number of pending items in the queue.
  /// </summary>
  public int PendingCount => _queue.Count;

  /// <summary>
  /// Enqueues an envelope for ImmediateDetached processing.
  /// </summary>
  /// <param name="envelope">The message envelope to process.</param>
  /// <param name="context">Optional lifecycle context.</param>
  public void Enqueue(IMessageEnvelope envelope, ILifecycleContext? context = null) {
    ArgumentNullException.ThrowIfNull(envelope);
    _queue.Enqueue((envelope, context));
  }

  /// <summary>
  /// Drains the queue, invoking ImmediateDetached receptors for each pending envelope.
  /// Items enqueued during drain (by ImmediateDetached receptors that cascade events)
  /// are processed in the same drain cycle.
  /// </summary>
  /// <param name="receptorInvoker">The receptor invoker to use for invocation.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The total number of items drained.</returns>
  public ValueTask<int> DrainAsync(
      IReceptorInvoker receptorInvoker,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(receptorInvoker);
    return _drainCoreAsync(receptorInvoker, cancellationToken);
  }

  private async ValueTask<int> _drainCoreAsync(
      IReceptorInvoker receptorInvoker,
      CancellationToken cancellationToken) {
    var depth = 0;
    while (_queue.TryDequeue(out var pending)) {
      cancellationToken.ThrowIfCancellationRequested();

      if (++depth % _warningThreshold == 0 && _logger is not null) {
        Log.ChainDepthExceedsThreshold(_logger, depth, _warningThreshold);
      }

      await receptorInvoker.InvokeAsync(
          pending.Envelope,
          LifecycleStage.ImmediateDetached,
          pending.Context,
          cancellationToken).ConfigureAwait(false);
    }

    return depth;
  }

  private static partial class Log {
    [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Warning,
      Message = "ImmediateDetached chain depth {Depth} exceeds threshold {Threshold}")]
    public static partial void ChainDepthExceedsThreshold(ILogger logger, int depth, int threshold);
  }
}
