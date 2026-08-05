using System;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Workers;

/// <summary>
/// Dedicated pool of long-lived PostgreSQL connections held by the Whizbang
/// background workers, bypassing pgbouncer for hot-path traffic. The pool
/// hands out connections via <see cref="TryPinForAsync"/>; callers dispose
/// the returned borrow to release the connection back to the pool.
/// </summary>
/// <remarks>
/// <para>
/// When the feature is disabled (or no <see cref="WhizbangPinnedPoolOptions.ConnectionString"/>
/// is configured) the registered implementation is <see cref="NoOpPinnedConnectionPool"/>
/// — it returns a no-op borrow with a <c>null</c> connection so callers can
/// branch on it without conditionals around the entire borrow site.
/// </para>
/// <para>
/// Eligibility is decided by <see cref="PinnedWorkerRegistry"/> in combination
/// with <see cref="WhizbangPinnedPoolOptions.ExcludeWorkers"/>. Ineligible
/// callers always receive the no-op borrow regardless of feature state.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PinnedPoolRegistrationTests.cs:Register_CoreOnly_ResolvesNoOpAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PinnedPoolRegistrationTests.cs:Register_PostgresEnabledWithConnString_ResolvesRealPoolAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PinnedConnectionPoolPrimitivesTests.cs:NoOp_TryPin_ReturnsBorrowWithNullConnectionAsync</tests>
public interface IPinnedConnectionPool {
  /// <summary>
  /// Attempts to acquire a pinned connection for the supplied worker type.
  /// Always returns a non-null <see cref="IBorrowedConnection"/>; the returned
  /// borrow's <see cref="IBorrowedConnection.Connection"/> is non-null only
  /// when the pool is enabled AND the worker is eligible.
  /// </summary>
  /// <param name="workerType">CLR type of the calling background worker. Used to determine eligibility and to tag pool metrics.</param>
  /// <param name="cancellationToken">Caller cancellation. Honored both by the borrow-wait and by any underlying connection-open round-trip.</param>
  /// <returns>The borrow handle. Dispose to return the connection to the pool.</returns>
  /// <exception cref="OperationCanceledException">The borrow timed out or the supplied <paramref name="cancellationToken"/> fired before a connection became available.</exception>
  ValueTask<IBorrowedConnection> TryPinForAsync(Type workerType, CancellationToken cancellationToken);
}
