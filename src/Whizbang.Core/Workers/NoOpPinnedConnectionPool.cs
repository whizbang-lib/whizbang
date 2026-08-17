using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Workers;

/// <summary>
/// No-op implementation of <see cref="IPinnedConnectionPool"/> used when the
/// pinned worker pool is disabled or no <see cref="WhizbangPinnedPoolOptions.ConnectionString"/>
/// is configured. Every <see cref="TryPinForAsync"/> call returns a shared
/// borrow with a <c>null</c> connection so callers can branch on it without
/// allocating per-call.
/// </summary>
/// <remarks>
/// Singleton: <see cref="Instance"/>. Hot-path allocation cost is zero —
/// the borrow returned is a pre-built struct-like sentinel.
/// </remarks>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PinnedConnectionPoolPrimitivesTests.cs:NoOp_TryPin_ReturnsBorrowWithNullConnectionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PinnedConnectionPoolPrimitivesTests.cs:NoOp_Instance_IsProcessWideSingletonAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PinnedConnectionPoolPrimitivesTests.cs:NoOp_TryPin_CancelledToken_ThrowsOperationCanceledAsync</tests>
public sealed class NoOpPinnedConnectionPool : IPinnedConnectionPool {
  /// <summary>Process-wide singleton instance.</summary>
  public static NoOpPinnedConnectionPool Instance { get; } = new();

  private NoOpPinnedConnectionPool() { }

  /// <inheritdoc />
  public ValueTask<IBorrowedConnection> TryPinForAsync(Type workerType, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(workerType);
    cancellationToken.ThrowIfCancellationRequested();
    return new ValueTask<IBorrowedConnection>(NoOpBorrow.Instance);
  }

  /// <summary>
  /// No-allocation borrow handle returned by <see cref="NoOpPinnedConnectionPool"/>.
  /// <see cref="Connection"/> is always <c>null</c>; <see cref="DisposeAsync"/>
  /// is a no-op.
  /// </summary>
  private sealed class NoOpBorrow : IBorrowedConnection {
    public static NoOpBorrow Instance { get; } = new();

    private NoOpBorrow() { }

    public DbConnection? Connection => null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}
