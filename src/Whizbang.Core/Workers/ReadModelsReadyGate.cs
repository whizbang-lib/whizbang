using System;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Workers;

/// <summary>
/// Thrown when a data-plane seam refuses because its barrier has not been released — a dispatch
/// before <c>Migrate</c> completes, or a lens read before the read models are consistent. The
/// message is framework-authored; HTTP layers should map it to 503.
/// </summary>
/// <docs>operations/startup/startup-pipeline#seams</docs>
public sealed class WhizbangNotReadyException : InvalidOperationException {
  /// <summary>Creates the refusal with the framework-authored explanation.</summary>
  public WhizbangNotReadyException(string message) : base(message) { }

  /// <summary>Creates the refusal with no message.</summary>
  public WhizbangNotReadyException() { }

  /// <summary>Creates the refusal with a message and an underlying cause.</summary>
  public WhizbangNotReadyException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The read-model barrier: opens when the schema is migrated AND the perspective startup scan
/// (registry init, orphan reconcile, rewind repair) has completed — later than <c>Migrate</c>,
/// earlier than <c>Ready</c>. A lens needs the read models consistent, and coupling reads to the
/// full composite would make them wait on transport provisioning they do not use.
/// </summary>
/// <docs>operations/startup/startup-pipeline#seams</docs>
public interface IReadModelsReadyGate {
  /// <summary>True once the read models are consistent; pure synchronous query.</summary>
  bool IsReady { get; }

  /// <summary>Awaits the read-models-ready signal. Sticky — late waiters return immediately.</summary>
  Task WaitForReadyAsync(CancellationToken cancellationToken);

  /// <summary>Signals the barrier. Idempotent. Called by <see cref="ReadModelsReadyDriver"/>.</summary>
  void MarkReady();
}

/// <summary>Default <see cref="IReadModelsReadyGate"/>: one sticky completion, any number of waiters.</summary>
/// <docs>operations/startup/startup-pipeline#seams</docs>
public sealed class ReadModelsReadyGate : IReadModelsReadyGate {
  private readonly TaskCompletionSource _ready =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <inheritdoc />
  public bool IsReady => _ready.Task.IsCompleted;

  /// <inheritdoc />
  public Task WaitForReadyAsync(CancellationToken cancellationToken)
    => _ready.Task.WaitAsync(cancellationToken);

  /// <inheritdoc />
  public void MarkReady() => _ready.TrySetResult();
}

/// <summary>
/// The one check every lens surface inherits: refuses (throws <see cref="WhizbangNotReadyException"/>)
/// while the read-model barrier is closed. A host with no barrier registered is ungated — test
/// fixtures and partial hosts behave exactly as before.
/// </summary>
/// <docs>operations/startup/startup-pipeline#seams</docs>
public static class ReadModelsGuard {
  /// <summary>Throws when a registered read-model barrier has not been released.</summary>
  public static void ThrowIfNotReady(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    var gate = (IReadModelsReadyGate?)services.GetService(typeof(IReadModelsReadyGate));
    if (gate is { IsReady: false }) {
      throw new WhizbangNotReadyException(
        "lens read refused: the read models are not ready — the schema is still migrating, or the "
        + "perspective startup repair has not completed. Reads resume when the read-model barrier "
        + "releases; probe readiness for when.");
    }
  }
}
