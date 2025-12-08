using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// Default readiness check - always returns true (transport always ready).
/// Use for in-process transports or transports without connectivity concerns.
/// </summary>
public class DefaultTransportReadinessCheck : ITransportReadinessCheck {
  /// <summary>
  /// Always returns true indicating the transport is ready.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Always returns true</returns>
  public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult(true);
  }
}
