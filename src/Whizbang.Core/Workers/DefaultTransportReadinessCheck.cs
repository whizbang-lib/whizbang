using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// Default readiness check - always returns true (transport always ready).
/// Use for in-process transports or transports without connectivity concerns.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Workers/DefaultTransportReadinessCheckTests.cs:IsReadyAsync_Always_ReturnsTrueAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/DefaultTransportReadinessCheckTests.cs:IsReadyAsync_MultipleCalls_AlwaysReturnsTrueAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/DefaultTransportReadinessCheckTests.cs:IsReadyAsync_Cancellation_ThrowsOperationCanceledExceptionAsync</tests>
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
